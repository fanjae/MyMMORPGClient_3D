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
        Program.Check(isolated.Entries[24].Quantity == 10 && isolated.Hp == 50, "Other account state leaked");
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
            Program.Check(original.Hp == 100 && original.Mp == 25 && original.Mesos == 1250 && original.Entries[24].Quantity == 13 &&
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
        Program.Check(differentCharacter.Entries[24].Quantity == 13 && differentCharacter.Hp == 100 && differentCharacter.Revision == 4,
            "Same-account character state leaked");
        Console.WriteLine("[PASS] Same-account and other-account isolation; map transfer/reentry restores current three-monster field");
        return 0;
    }
    private sealed class Peer : IDisposable
    {
        private readonly TcpSession _login = new(), _game = new();
        private readonly Channel<(ushort Op, byte[] Data)> _loginPackets = Channel.CreateUnbounded<(ushort, byte[])>();
        private readonly Channel<(ushort Op, byte[] Data)> _gamePackets = Channel.CreateUnbounded<(ushort, byte[])>();
        internal World3DData World; internal FieldMonstersData Monsters; internal InventoryData Inventory;
        internal Peer()
        {
            _login.PacketReceived += (op, data) => _loginPackets.Writer.TryWrite((op, data));
            _game.PacketReceived += (op, data) => _gamePackets.Writer.TryWrite((op, data));
        }
        internal async Task ConnectAsync(string account, uint character)
        {
            await _login.ConnectAsync("127.0.0.1", 7776);
            await _login.SendAsync((ushort)LoginPacketOpcode.LoginRequest, LoginProtocol.CreateLoginRequest(account, Environment.GetEnvironmentVariable("MMORPG_TEST_PASSWORD")));
            Program.Check(LoginProtocol.ReadLoginResponse(await LoginAsync(LoginPacketOpcode.LoginResponse)) == LoginResult.Success, "Test login failed");
            await _login.SendAsync((ushort)LoginPacketOpcode.CharacterListRequest, Array.Empty<byte>());
            Program.Check(LoginProtocol.ReadCharacterListResponse(await LoginAsync(LoginPacketOpcode.CharacterListResponse)).Characters.Any(c => c.CharacterId == character), "Test character missing");
            await _login.SendAsync((ushort)LoginPacketOpcode.CharacterSelectRequest, LoginProtocol.CreateCharacterSelectRequest(character));
            var selected = LoginProtocol.ReadCharacterSelectResponse(await LoginAsync(LoginPacketOpcode.CharacterSelectResponse));
            Program.Check(selected.Result == CharacterSelectResult.Success, "Test selection failed");
            await _game.ConnectAsync("127.0.0.1", selected.GameServerPort);
            await _game.SendAsync((ushort)GamePacketOpcode.EnterGameRequest, GameProtocol.CreateEnterGameRequest(selected.AuthKey));
            await PumpAsync(() => World != null && Monsters != null);
        }
        private async Task<byte[]> LoginAsync(LoginPacketOpcode opcode)
        {
            using CancellationTokenSource timeout = new(10000);
            var packet = await _loginPackets.Reader.ReadAsync(timeout.Token); Program.Check(packet.Op == (ushort)opcode, "Unexpected login response"); return packet.Data;
        }
        private async Task PumpAsync(Func<bool> ready)
        {
            using CancellationTokenSource timeout = new(10000);
            do
            {
                var packet = await _gamePackets.Reader.ReadAsync(timeout.Token);
                switch ((GamePacketOpcode)packet.Op)
                {
                    case GamePacketOpcode.EnterGameResponse: Program.Check(GameProtocol.ReadEnterGameResponse(packet.Data).Result == EnterGameResult.Success, "Game entry failed"); break;
                    case GamePacketOpcode.World3D: World = Movement3DProtocol.ReadWorld(packet.Data); Monsters = null; break;
                    case GamePacketOpcode.WorldBox3D: Movement3DProtocol.AddBox(World, packet.Data); break;
                    case GamePacketOpcode.WorldEnd3D: Movement3DProtocol.CompleteWorld(World, packet.Data); break;
                    case GamePacketOpcode.FieldMonsters3D:
                        var monsters = FieldProtocol.ReadMonsters(packet.Data);
                        if (World != null && monsters.MapId == World.MapId && monsters.Generation == World.Generation) Monsters = monsters; break;
                    case GamePacketOpcode.InventorySnapshot: Inventory = FieldProtocol.ReadInventory(packet.Data); break;
                    case GamePacketOpcode.ChangeMapResponse: Program.Check(GameProtocol.ReadChangeMapResponse(packet.Data).Result == ChangeMapResult.Success, "Map transfer failed"); break;
                }
            } while (!ready());
        }
        internal async Task<InventoryData> OperationAsync(InventoryOperation operation, byte category = 0, byte source = 0, byte target = 0)
        {
            await Task.Delay(120);
            var previous = Inventory;
            await _game.SendAsync((ushort)GamePacketOpcode.InventoryRequest, FieldProtocol.InventoryRequest(operation, category, source, target, Inventory?.Revision ?? 0));
            await PumpAsync(() => Inventory != previous); return Inventory;
        }
        internal async Task ChangeMapAsync(uint map)
        {
            await _game.SendAsync((ushort)GamePacketOpcode.ChangeMapRequest, GameProtocol.CreateChangeMapRequest(map));
            await PumpAsync(() => World?.MapId == map && Monsters?.MapId == map);
        }
        public void Dispose() { _game.Dispose(); _login.Dispose(); }
    }
}
