using System.Diagnostics;
using System.Threading.Channels;

internal static class FieldIntegration
{
    internal static async Task<int> RunAsync(int port)
    {
        // 두 번째 클라이언트를 늦게 접속시켜 현재 필드 상태를 바로 받는지 확인한다.
        using Peer a = new(), b = new();
        await a.ConnectAsync(port); await a.PumpUntilAsync(() => a.World != null && a.Monsters.Count >= 2);
        double firstX = a.Monsters.Last().Value.Monsters[0].X;
        await Task.Delay(350);
        await b.ConnectAsync(port); await b.PumpUntilAsync(() => b.World != null && b.Monsters.Count > 0);
        await a.RefreshAsync(); await b.RefreshAsync();
        await a.PumpUntilAsync(() => a.Inventory != null); await b.PumpUntilAsync(() => b.Inventory != null);
        Program.Check(a.Inventory.Entries[24].Quantity == 10 && b.Inventory.Entries[24].Quantity == 10, "Field starter inventory missing");
        ulong revision = a.Inventory.Revision;
        await a.OperationAsync(InventoryOperation.Move, 1, 4, 0, revision);
        await a.PumpUntilAsync(() => a.Inventory.Revision > revision);
        Program.Check(a.Inventory.Entries[24].Quantity == 15 && a.Inventory.Entries[28].ItemId == 0, "TCP inventory merge failed");
        // 병합 이전의 변경 번호로 사용을 요청해 수량과 HP가 그대로 유지되는지 확인한다.
        await a.OperationAsync(InventoryOperation.Use, 1, 0, 0, revision);
        await a.PumpUntilAsync(() => a.Inventory.Result == InventoryResult.StaleRevision);
        Program.Check(a.Inventory.Hp == 50 && a.Inventory.Entries[24].Quantity == 15, "Stale use changed state");
        await Task.Delay(120); revision = a.Inventory.Revision;
        await a.OperationAsync(InventoryOperation.Use, 1, 0, 0, revision); await a.PumpUntilAsync(() => a.Inventory.Revision > revision);
        Program.Check(a.Inventory.Hp == 80 && a.Inventory.Entries[24].Quantity == 14, "TCP potion use failed");
        Program.Check(b.Inventory.Hp == 50 && b.Inventory.Entries[24].Quantity == 10, "Private inventory leaked to peer");
        await Task.Delay(120); revision = a.Inventory.Revision;
        await a.OperationAsync(InventoryOperation.Sort, 1, 0, 0, revision); await a.PumpUntilAsync(() => a.Inventory.Revision > revision);
        Program.Check(a.Inventory.Entries[24].ItemId == 101 && a.Inventory.Entries[25].ItemId == 102, "TCP inventory sort failed");
        await a.PumpForAsync(500); await b.PumpForAsync(200);
        // 수신 시각이 달라도 같은 서버 tick의 몬스터 위치는 두 클라이언트에서 같아야 한다.
        ulong common = a.Monsters.Keys.Intersect(b.Monsters.Keys).Max();
        var left = a.Monsters[common]; var right = b.Monsters[common];
        Program.Check(left.Monsters.Count == 3 && left.Monsters.Zip(right.Monsters).All(pair => pair.First.Id == pair.Second.Id && pair.First.X == pair.Second.X && pair.First.Z == pair.Second.Z), "TCP peers disagree on same-tick monsters");
        Program.Check(left.Monsters[0].X != firstX, "TCP monsters did not move");
        Console.WriteLine("[PASS] TCP inventory move/merge, stale revision, use, sort and private owner state");
        Console.WriteLine("[PASS] TCP late entry receives three moving monsters and same-tick state matches both peers");
        return 0;
    }
    private sealed class Peer : IDisposable
    {
        private readonly TcpSession _session = new();
        private readonly Channel<(ushort Opcode, byte[] Payload)> _packets = Channel.CreateUnbounded<(ushort, byte[])>();
        internal World3DData World; internal InventoryData Inventory;
        internal readonly SortedDictionary<ulong, FieldMonstersData> Monsters = new();
        internal Peer() { _session.PacketReceived += (opcode, payload) => _packets.Writer.TryWrite((opcode, payload)); }
        internal async Task ConnectAsync(int port)
        { await _session.ConnectAsync("127.0.0.1", port); await _session.SendAsync((ushort)GamePacketOpcode.EnterGameRequest, GameProtocol.CreateEnterGameRequest(0)); }
        internal Task RefreshAsync() => OperationAsync(InventoryOperation.Refresh, 0, 0, 0, 0);
        internal Task OperationAsync(InventoryOperation op, byte category, byte source, byte target, ulong revision) =>
            _session.SendAsync((ushort)GamePacketOpcode.InventoryRequest, FieldProtocol.InventoryRequest(op, category, source, target, revision));
        internal async Task PumpUntilAsync(Func<bool> condition)
        {
            using CancellationTokenSource timeout = new(5000);
            do { Process(await _packets.Reader.ReadAsync(timeout.Token)); } while (!condition());
        }
        internal async Task PumpForAsync(int milliseconds)
        {
            using CancellationTokenSource timeout = new(milliseconds);
            try { while (true) Process(await _packets.Reader.ReadAsync(timeout.Token)); } catch (OperationCanceledException) { }
        }
        private void Process((ushort Opcode, byte[] Payload) packet)
        {
            switch ((GamePacketOpcode)packet.Opcode)
            {
                case GamePacketOpcode.World3D: World = Movement3DProtocol.ReadWorld(packet.Payload); break;
                case GamePacketOpcode.WorldBox3D: Movement3DProtocol.AddBox(World, packet.Payload); break;
                case GamePacketOpcode.WorldEnd3D: Movement3DProtocol.CompleteWorld(World, packet.Payload); break;
                case GamePacketOpcode.InventorySnapshot: Inventory = FieldProtocol.ReadInventory(packet.Payload); break;
                case GamePacketOpcode.FieldMonsters3D:
                    var data = FieldProtocol.ReadMonsters(packet.Payload); Program.Check(data.MapId == World.MapId && data.Generation == World.Generation, "Wrong monster generation"); Monsters[data.Tick] = data; break;
            }
        }
        public void Dispose() => _session.Dispose();
    }
}
