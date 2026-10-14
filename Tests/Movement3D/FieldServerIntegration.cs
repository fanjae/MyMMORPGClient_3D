using System.Threading.Channels;

// 임시 DB를 연결한 실제 LoginServer·GameServer의 저장과 재입장을 검증한다.
internal static class FieldServerIntegration
{
    internal static async Task<int> RunAsync(bool verify)
    {
        Program.Check(Environment.GetEnvironmentVariable("DB_NAME")?.StartsWith("mymmorpg_full_test") == true, "Use the isolated integration DB");
        using Peer owner = new(), other = new();
        await owner.ConnectAsync("test", 1002); await other.ConnectAsync("test2", 2001);
        var original = await owner.OperationAsync(InventoryOperation.Refresh);
        var isolated = await other.OperationAsync(InventoryOperation.Refresh);
        Program.Check(isolated.Entries[24].Quantity == 10 && isolated.Hp <= 50 && isolated.MaxHp==100, "Other account inventory state leaked");
        // 저장 검사는 전투 피해와 섞이지 않도록 두 검사 캐릭터를 안전한 바닥으로 이동한다.
        await Task.WhenAll(owner.MoveSafeAsync(),other.MoveSafeAsync());
        if (!verify)
        {
            Program.Check(original.Revision == 1, "Expected fresh character inventory");
            var merged = await owner.OperationAsync(InventoryOperation.Move, 1, 4, 0);
            Program.Check(merged.Entries[24].Quantity == 15 && merged.Entries[28].ItemId == 0, "Actual server merge failed");
            var used = await owner.OperationAsync(InventoryOperation.Use, 1, 0, 0);
            Program.Check(used.Hp == 80 && used.Entries[24].Quantity == 14, "Actual server potion failed");
            await owner.OperationAsync(InventoryOperation.Move, 1, 1, 8);
            var sorted = await owner.OperationAsync(InventoryOperation.Sort, 1);
            Program.Check(sorted.Entries[25].ItemId == 102 && sorted.Entries[32].ItemId == 0, "Actual server sort failed");
            var full = await owner.OperationAsync(InventoryOperation.Use, 1);
            var rejected = await owner.OperationAsync(InventoryOperation.Use, 1);
            Program.Check(full.Hp == 100 && rejected.Result == InventoryResult.CannotUse && rejected.Entries[24].Quantity == 13, "Full-health use consumed a potion");
            var invalid = await owner.OperationAsync(InventoryOperation.Move, 5);
            Program.Check(invalid.Result == InventoryResult.InvalidSlot && invalid.Revision == full.Revision, "Invalid slots changed actual DB state");
            Console.WriteLine("[PASS] Actual server inventory merge, move, sort, potion, full-health rejection and invalid slots");
        }
        else
        {
            Program.Check(original.Hp <= 100 && original.Mp == 25 && original.Mesos == 1250 && original.Entries[24].Quantity == 13 &&
                original.Entries[25].ItemId == 102 && original.Entries[25].Quantity == 8 && original.Revision > 1, "Inventory did not survive server restart");
            Console.WriteLine("[PASS] Server restart preserves inventory slots, quantities, mesos, HP, MP and revision");
        }
        ulong before = owner.World.Generation, inventoryRevision = owner.Inventory.Revision;
        await owner.ChangeMapAsync(100000003);
        Program.Check(owner.World.MapId == 100000003 && owner.World.Generation != before && owner.Monsters.Monsters.Count == 3, "Map transfer kept old field state");
        var afterMap = await owner.OperationAsync(InventoryOperation.Refresh);
        Program.Check(afterMap.Revision == inventoryRevision && afterMap.Entries[24].Quantity == 13, "Map transfer lost inventory");
        await owner.ChangeMapAsync(100000002);
        // 한 계정당 한 캐릭터 접속 정책에 맞춰 먼저 퇴장한 뒤 다른 캐릭터로 재접속한다.
        owner.Dispose(); await Task.Delay(250);
        using Peer sameAccount = new(); await sameAccount.ConnectAsync("test", 1001);
        var differentCharacter = await sameAccount.OperationAsync(InventoryOperation.Refresh);
        // 재입장 직후에도 몬스터가 공격하므로 저장한 상태와 피격에 따른 HP·revision의 관계를 함께 확인한다.
        Program.Check(differentCharacter.Entries[24].Quantity == 13 && differentCharacter.MaxHp == 100 && differentCharacter.Mp == 25 &&
            differentCharacter.Hp <= 100 && (100 - differentCharacter.Hp) % 10 == 0 &&
            differentCharacter.Revision == 4u + (100u - differentCharacter.Hp) / 10 &&
            differentCharacter.Entries[0].InstanceId != original.Entries[0].InstanceId,
            $"Same-account character state leaked: result={differentCharacter.Result}, hp={differentCharacter.Hp}, revision={differentCharacter.Revision}, quantity={differentCharacter.Entries[24].Quantity}");
        Console.WriteLine("[PASS] Same-account and other-account isolation; map transfer/reentry restores current three-monster field");
        return 0;
    }
    internal sealed class Peer : IDisposable
    {
        private readonly TcpSession _login = new(), _game = new();
        private readonly Channel<(ushort Op, byte[] Data)> _loginPackets = Channel.CreateUnbounded<(ushort, byte[])>();
        private readonly Channel<(ushort Op, byte[] Data)> _gamePackets = Channel.CreateUnbounded<(ushort, byte[])>();
        internal World3DData World; internal FieldMonstersData Monsters; internal InventoryData Inventory;
        private uint _character;
        private ulong _requestId;
        private readonly FieldMonsterAssembler _monsterAssembler = new();
        internal bool CombatPaused, SawCombatPause;
        internal bool ExpectMapReject;
        internal ChangeMapResult? LastMapResult;
        internal ChannelData Channels; internal DropsData Drops; internal BasicAttackData Attack;
        internal PickupResult? PickupResult; internal int ChatCount;
        internal TcpSession.TrafficSnapshot Traffic(ushort? opcode=null) => _game.GetTraffic(opcode);
        internal readonly HashSet<uint> Remotes=new();
        internal Task SendAsync(GamePacketOpcode opcode,byte[] payload) => _game.SendAsync((ushort)opcode,payload);
        internal Peer()
        {
            _login.PacketReceived += (op, data) => _loginPackets.Writer.TryWrite((op, data));
            _game.PacketReceived += (op, data) => _gamePackets.Writer.TryWrite((op, data));
        }
        internal async Task ConnectAsync(string account, uint character)
        {
            _character=character;
            await _login.ConnectAsync("127.0.0.1", 7776);
            await _login.SendAsync((ushort)LoginPacketOpcode.LoginRequest, LoginProtocol.CreateLoginRequest(account, Environment.GetEnvironmentVariable("MMORPG_TEST_PASSWORD")));
            Program.Check(LoginProtocol.ReadLoginResponse(await LoginAsync(LoginPacketOpcode.LoginResponse)) == LoginResult.Success, "Test login failed");
            await _login.SendAsync((ushort)LoginPacketOpcode.CharacterListRequest, Array.Empty<byte>());
            Program.Check(LoginProtocol.ReadCharacterListResponse(await LoginAsync(LoginPacketOpcode.CharacterListResponse)).Characters.Any(c => c.CharacterId == character), "Test character missing");
            await _login.SendAsync((ushort)LoginPacketOpcode.CharacterSelectRequest, LoginProtocol.CreateCharacterSelectRequest(character));
            var selected = LoginProtocol.ReadCharacterSelectResponse(await LoginAsync(LoginPacketOpcode.CharacterSelectResponse));
            Program.Check(selected.Result == CharacterSelectResult.Success, "Test selection failed");
            int port=int.TryParse(Environment.GetEnvironmentVariable("MMORPG_TEST_GAME_PORT"),out int proxyPort) ? proxyPort : selected.GameServerPort;
            await _game.ConnectAsync("127.0.0.1", port);
            await _game.SendAsync((ushort)GamePacketOpcode.EnterGameRequest, GameProtocol.CreateEnterGameRequest(selected.AuthKey));
            await PumpAsync(() => World != null && Monsters != null);
            // 게임 입장 뒤 인벤토리 조회를 명시적으로 보내고 DB 상태가 준비된 뒤 다음 검사를 진행한다.
            await OperationAsync(InventoryOperation.Refresh);
        }
        private async Task<byte[]> LoginAsync(LoginPacketOpcode opcode)
        {
            using CancellationTokenSource timeout = new(10000);
            var packet = await _loginPackets.Reader.ReadAsync(timeout.Token); Program.Check(packet.Op == (ushort)opcode, "Unexpected login response"); return packet.Data;
        }
        internal async Task PumpAsync(Func<bool> ready)
        {
            using CancellationTokenSource timeout = new(10000);
            do
            {
                var packet = await _gamePackets.Reader.ReadAsync(timeout.Token);
                Handle(packet);
            } while (!ready());
        }
        internal void Drain()
        {
            while (_gamePackets.Reader.TryRead(out var packet)) Handle(packet);
        }
        private void Handle((ushort Op,byte[] Data) packet)
        {
                switch ((GamePacketOpcode)packet.Op)
                {
                    case GamePacketOpcode.ChannelState: Channels=FieldExtensions.ReadChannels(packet.Data); break;
                    case GamePacketOpcode.FieldDrops3D: Drops=FieldExtensions.ReadDrops(packet.Data); break;
                    case GamePacketOpcode.BasicAttackEvent3D: Attack=CombatProtocol.Read(packet.Data); break;
                    case GamePacketOpcode.PickupResponse3D: PickupResult=FieldExtensions.ReadPickup(packet.Data).Result; break;
                    case GamePacketOpcode.PlayerEnterMap: Remotes.Add(GameProtocol.ReadPlayerEnterMap(packet.Data).CharacterId); break;
                    case GamePacketOpcode.PlayerLeaveMap: Remotes.Remove(GameProtocol.ReadPlayerLeaveMap(packet.Data)); break;
                    case GamePacketOpcode.PlayerChat: ++ChatCount; break;
                    case GamePacketOpcode.Vitals3D:
                        var vitals=FieldExtensions.ReadVitals(packet.Data);
                        if (vitals.CharacterId == _character) { CombatPaused = vitals.Paused; SawCombatPause |= vitals.Paused; }
                        if (Inventory!=null && vitals.CharacterId==_character && vitals.Revision>=Inventory.Revision)
                        { Inventory.Hp=vitals.Hp; Inventory.Mp=vitals.Mp; Inventory.Revision=vitals.Revision; } break;
                    case GamePacketOpcode.EnterGameResponse: Program.Check(GameProtocol.ReadEnterGameResponse(packet.Data).Result == EnterGameResult.Success, "Game entry failed"); break;
                    case GamePacketOpcode.World3D: World = Movement3DProtocol.ReadWorld(packet.Data); Monsters = null; _monsterAssembler.Clear(); break;
                    case GamePacketOpcode.WorldBox3D: Movement3DProtocol.AddBox(World, packet.Data); break;
                    case GamePacketOpcode.WorldEnd3D: Movement3DProtocol.CompleteWorld(World, packet.Data); break;
                    case GamePacketOpcode.FieldMonsters3D:
                        var part = FieldProtocol.ReadMonsters(packet.Data);
                        if (World != null && part.MapId == World.MapId && part.Generation == World.Generation)
                        { var monsters = _monsterAssembler.Accept(part); if (monsters != null) Monsters = monsters; } break;
                    case GamePacketOpcode.InventorySnapshot: Inventory = FieldProtocol.ReadInventory(packet.Data); break;
                    case GamePacketOpcode.ChangeMapResponse:
                        LastMapResult = GameProtocol.ReadChangeMapResponse(packet.Data).Result;
                        if (!ExpectMapReject) Program.Check(LastMapResult == ChangeMapResult.Success, "Map transfer failed"); break;
                }
        }
        internal async Task<InventoryData> OperationAsync(InventoryOperation operation, byte category = 0, byte source = 0, byte target = 0)
        {
            for (int attempt = 0; attempt < 10; ++attempt)
            {
                await Task.Delay(120);
                var previous = Inventory;
                ulong requestId = ++_requestId;
                await _game.SendAsync((ushort)GamePacketOpcode.InventoryRequest, FieldProtocol.InventoryRequest(operation, category, source, target, Inventory?.Revision ?? 0, requestId));
                await PumpAsync(() => Inventory != previous && Inventory.RequestId == requestId);
                // RateLimited는 적용하지 않은 요청만 반환하므로 제한이 풀린 뒤 다시 요청해도 소비를 반복하지 않는다.
                if (Inventory.Result != InventoryResult.RateLimited) return Inventory;
            }
            throw new InvalidDataException("Inventory request remained rate limited");
        }
        internal async Task ChangeMapAsync(uint map)
        {
            await _game.SendAsync((ushort)GamePacketOpcode.ChangeMapRequest, GameProtocol.CreateChangeMapRequest(map));
            await PumpAsync(() => World?.MapId == map && Monsters?.MapId == map);
        }
        internal async Task MoveSafeAsync()
        {
            var motion=new ClientMotion3D(); motion.Configure(World); var watch=System.Diagnostics.Stopwatch.StartNew();
            for (int step=0;step<23;++step)
            {
                motion.SetInput(step<7 ? 1 : 0,1,false); motion.Advance(0.1);
                if (motion.TryCreateBatch(watch.Elapsed.TotalSeconds,out var batch)) await SendAsync(GamePacketOpcode.MovementActions3D,Movement3DProtocol.CreateBatch(batch));
                await Task.Delay(220); await PumpAsync(()=>true);
            }
            motion.StopInput(); motion.Advance(0.02); await Task.Delay(220);
            if (motion.TryCreateBatch(watch.Elapsed.TotalSeconds,out var stopped)) await SendAsync(GamePacketOpcode.MovementActions3D,Movement3DProtocol.CreateBatch(stopped));
        }
        public void Dispose() { _game.Dispose(); _login.Dispose(); }
    }
}
