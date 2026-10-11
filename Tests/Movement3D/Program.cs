using System.Diagnostics;
using System.Threading.Channels;

internal static class Program
{
    internal static void Check(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--field-tcp") return await FieldIntegration.RunAsync(int.Parse(args[1]));
            if (args.Length > 0 && args[0] == "--tcp") return await TcpAsync(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]));
            void Run(string name, Action test) { test(); Console.WriteLine("[PASS] " + name); }
            Run("XYZ ground motion preserves diagonal speed and wall collision", () =>
            {
                var world = World3DData.Preview(); var sim = new Motion3DSimulation(world); var state = sim.Spawn();
                sim.Step(ref state, 707, 707, false);
                Check(Math.Abs(state.X - state.Z) < 1e-8 && Math.Sqrt(state.VelocityX * state.VelocityX + state.VelocityZ * state.VelocityZ) <= world.Speed, "Diagonal movement gained speed");
                state = sim.Spawn(); for (int i = 0; i < 100; ++i) sim.Step(ref state, 1000, 0, false);
                Check(Math.Abs(state.X - 74) < 1e-8 && state.VelocityX == 0 && state.Grounded, "3D wall penetration or ground lost");
            });
            Run("Jump pulses reject airborne repeats and landing permits a fresh jump", () =>
            {
                ClientMotion3D client = new(); client.Configure(World3DData.Preview()); client.SetInput(0, 0, true); client.Advance(0.02);
                Check(client.JumpId == 1 && !client.State.Grounded, "First jump missing");
                for (int i = 0; i < 20; ++i) { client.SetInput(0, 0, true); client.Advance(0.02); }
                Check(client.JumpId == 1, "Airborne jump repeated");
                for (int i = 0; i < 40; ++i) { client.SetInput(0, 0, false); client.Advance(0.02); }
                Check(client.State.Grounded && client.CanJump && client.JumpId == 1, "Landing left jump locked");
                client.SetInput(0, 0, true); client.Advance(0.02); Check(client.JumpId == 2, "Fresh post-landing jump rejected");
            });
            Run("Raised support landing and ceiling contact use the shared collision motor", () =>
            {
                var world = World3DData.Preview(); var sim = new Motion3DSimulation(world);
                Motion3DState state = new() { X = -100, Z = 110, Y = 60, Grounded = false };
                for (int i = 0; i < 80; ++i) sim.Step(ref state, 0, 0, false);
                Check(state.Grounded && state.SupportId == 4 && state.Y == 10, "Raised support not selected");
                world.Boxes.Add(new() { Id = 6, MinX = -50, MinY = 50, MinZ = -50, MaxX = 50, MaxY = 60, MaxZ = 50 });
                state = sim.Spawn(); double peak = 0;
                for (int i = 0; i < 70; ++i) { sim.Step(ref state, 0, 0, i == 0); peak = Math.Max(peak, state.Y); }
                Check(peak <= 14 && state.Grounded, "Ceiling collision tunneled");
            });
            Run("Falling through the gap emits Fall and exact-spawn Respawn", () =>
            {
                ClientMotion3D client = new(); var world = World3DData.Preview(); client.Configure(world);
                List<Motion3DAction> actions = new();
                for (int i = 0; i < 400; ++i)
                {
                    client.SetInput(0, 1, false); client.Advance(0.02);
                    if (client.TryCreateBatch(i * 0.02, out var batch)) actions.AddRange(batch.Actions);
                }
                Check(actions.Any(a => a.Kind == Action3DKind.Fall), "Fall event missing");
                Check(actions.Any(a => a.Kind == Action3DKind.Respawn && a.State.Grounded && a.State.X == world.SpawnX &&
                    a.State.Y == world.SpawnY && a.State.Z == world.SpawnZ), "Respawn did not report the exact spawn");
            });
            Run("All input and jump actions share five-send windows and one-second checkpoints", () =>
            {
                ClientMotion3D client = new(); client.Configure(World3DData.Preview()); List<double> sent = new(); List<Motion3DAction> actions = new();
                for (int i = 0; i <= 200; ++i)
                {
                    client.SetInput(i < 100 ? (i % 2 == 0 ? 1 : -1) : 0, 0, i == 3); client.Advance(0.02);
                    double now = i * 0.02; if (client.TryCreateBatch(now, out var batch)) { sent.Add(now); actions.AddRange(batch.Actions); }
                }
                foreach (double start in sent) Check(sent.Count(t => t >= start && t < start + 1 - 1e-8) <= 5, "3D movement exceeded five sends per second");
                Check(actions.Count(a => a.Kind == Action3DKind.Jump) == 1 && actions.Count(a => a.Kind == Action3DKind.Land) == 1, "Jump/Land order lost");
                Check(sent.Count(t => t > 2.3) <= 2 && actions.Any(a => a.Kind == Action3DKind.Checkpoint), "Idle traffic did not use one-second checkpoints");
            });
            Run("Map reset and focus neutralization clear pending jump and keep send budget", () =>
            {
                var world = World3DData.Preview(); ClientMotion3D client = new(); client.Configure(world); client.SetInput(1, 1, true); client.StopInput(); client.Advance(0.02);
                Check(client.JumpId == 0 && client.State.X == 0 && client.State.Z == 0, "Focus neutralization retained jump or movement");
                Check(client.TryCreateBatch(0, out _), "Initial batch missing"); client.Configure(world);
                Check(client.JumpId == 0 && client.State.Grounded && !client.TryCreateBatch(0.1, out _), "Map reset bypassed budget or retained jump");
            });
            Run("3D wire sizes and flags agree with packed C++ records", () =>
            {
                var world = World3DData.Preview(); ClientMotion3D client = new(); client.Configure(world); client.Advance(0.02); client.TryCreateBatch(0, out var batch);
                byte[] wire = Movement3DProtocol.CreateBatch(batch); Check(wire.Length == 114, "3D input payload size changed");
                using PacketWriter writer = new(); writer.Write(world.MapId); writer.Write(world.Generation); writer.Write(1UL); writer.Write((ushort)1);
                writer.Write(1001U); writer.Write(batch.LatestClientTick); Movement3DProtocol.WriteAction(writer, batch.Actions[0]);
                byte[] payload = writer.ToArray(); Check(payload.Length == 118 && Movement3DProtocol.ReadBroadcast(payload).Actions.Count == 1, "3D broadcast layout changed");
                payload[^1] = 255; bool rejected = false;
                try { Movement3DProtocol.ReadBroadcast(payload); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && GameProtocol.ProtocolVersion == 8, "Invalid flags accepted or 3D version wrong");
            });
            Run("Remote silence stops prediction and fresh events resume without sequence replay", () =>
            {
                var world = World3DData.Preview(); RemoteMotion3D remote = new();
                var state = new Motion3DSimulation(world).Spawn(); state.X = -200;
                Relay3DAction snapshot = new() { CharacterId = 1001, Action = new() { State = state } };
                remote.Configure(world, snapshot, 0);
                Relay3DAction relay = new() { CharacterId = 1001, LatestClientTick = 1, Action = new() { Sequence = 1, ClientTick = 1, InputZ = 1000, State = state } };
                Check(remote.Add(relay, 0), "Remote input rejected"); remote.Sample(3, out var stopped); remote.Sample(4, out var still);
                Check(stopped.Z == still.Z, "Remote extrapolated beyond silence limit");
                relay.Action.Sequence = 2; relay.Action.ClientTick = relay.LatestClientTick = 201; relay.Action.State.Z = 40;
                Check(remote.Add(relay, 4) && !remote.Add(relay, 4), "Remote duplicate action accepted"); remote.Sample(4, out var anchor);
                Check(anchor.Z == 40 && remote.Snap, "Silence recovery replayed the stale timeline instead of snapping to a fresh baseline");
                remote.Sample(4.3, out var resumed); Check(resumed.Z > 40, "Remote did not resume at fresh state");
            });
            Console.WriteLine("PASS 8 3D client tests"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("[FAIL] " + e); return 1; }
    }

    private static async Task<int> TcpAsync(int port, int delay, int jitter)
    {
        await using DelayedGameProxy pa = new(delay, jitter, targetPort: port), pb = new(delay, jitter, targetPort: port);
        using Peer a = new(), b = new(); await a.ConnectAsync(pa.Port); await b.ConnectAsync(pb.Port);
        int settle = 400 + delay * 4 + jitter * 2;
        await Pump(settle); Check(b.States.ContainsKey(a.Id), "Remote initial 3D state missing");
        ClientMotion3D client = new(); client.Configure(a.World); RemoteMotion3D remote = new();
        Stopwatch clock = Stopwatch.StartNew(); remote.Configure(b.World, b.States[a.Id], 0);
        b.Received += action => { if (action.CharacterId == a.Id) remote.Add(action, clock.Elapsed.TotalSeconds); };
        double previous = 0, peak = 0; List<double> sends = new();
        client.SetInput(-1, 1, true);
        while (clock.Elapsed.TotalSeconds < 1.8)
        {
            double now = clock.Elapsed.TotalSeconds; client.SetInput(now < 0.7 ? -1 : 0, now < 0.7 ? 1 : 0, now < 0.3);
            client.Advance(now - previous); previous = now;
            if (client.TryCreateBatch(now, out var batch)) { await a.SendAsync(batch); sends.Add(now); }
            a.Pump(); b.Pump(); remote.Sample(now, out var state); peak = Math.Max(peak, state.Y); await Task.Delay(10);
        }
        await Pump(settle);
        Check(client.JumpId == 1 && client.State.Grounded && b.States[a.Id].Action.State.Grounded && peak > 10, "3D jump/landing reproduction failed");
        Check(b.Actions.Count(x => x.CharacterId == a.Id && x.Action.Kind == Action3DKind.Jump) == 1 &&
            b.Actions.Count(x => x.CharacterId == a.Id && x.Action.Kind == Action3DKind.Land) == 1 && b.States[a.Id].Action.State.Z > 10, "3D XYZ event order lost");
        foreach (double start in sends) Check(sends.Count(t => t >= start && t < start + 1 - 1e-8) <= 5, "TCP 3D send budget exceeded");
        Console.WriteLine("[PASS] TCP XYZ jump, remote simulation, landing, input stop and five-send budget");
        var last = b.States[a.Id].Action;
        Motion3DAction jump = new() { Sequence = 1000, ClientTick = client.Tick + 1, JumpId = 2, Kind = Action3DKind.Jump, State = last.State };
        jump.State.Grounded = false; jump.State.SupportId = 0; jump.State.Y += 3.04; jump.State.VelocityY = 152;
        await a.ManualAsync(jump, 1000); await Pump(settle);
        Check(!b.States[a.Id].Action.State.Grounded, "Manual 3D jump missing"); int before = b.Actions.Count; await Pump(1200);
        jump.Sequence = 1001; ++jump.ClientTick; jump.JumpId = 3; await a.ManualAsync(jump, 1001); await Pump(settle);
        Check(b.Actions.Count == before && b.States[a.Id].Action.Sequence == 1000, "Server computed 3D landing or broadcast airborne rejump");
        Console.WriteLine("[PASS] TCP 3D server does not simulate physics and rejects airborne rejump");
        Motion3DAction land = new() { Sequence = 1002, ClientTick = jump.ClientTick + 1, JumpId = 2, Kind = Action3DKind.Land, State = last.State };
        await a.ManualAsync(land, 1002); await Pump(settle);
        jump.Sequence = 1003; jump.ClientTick = land.ClientTick + 1; jump.JumpId = 2; await a.ManualAsync(jump, 1003); await Pump(settle);
        Check(b.States[a.Id].Action.Sequence == 1002, "Old 3D jump resurrected");
        jump.Sequence = 1004; ++jump.ClientTick; jump.JumpId = 3; await a.ManualAsync(jump, 1004); await Pump(settle);
        Check(b.States[a.Id].Action.Sequence == 1004, "Fresh 3D jump after landing rejected");
        Console.WriteLine("[PASS] TCP matching 3D landing permits a fresh jump and excludes old jumpId");
        var traffic = a.Session.GetTraffic((ushort)GamePacketOpcode.MovementActions3D);
        Check(traffic.SentPackets == a.SentPackets && traffic.SentBytes == a.SentBytes && traffic.QueuedSendBytes == 0 &&
            b.Session.GetTraffic((ushort)GamePacketOpcode.MovementActionsBroadcast3D).ReceivedBytes == b.ReceivedBytes, "3D frame accounting mismatch");
        Console.WriteLine($"[PASS] TCP 3D frame metrics match: delay={delay}±{jitter}ms TX={traffic.SentPackets}/{traffic.SentBytes}B RX={b.ReceivedBytes}B");
        return 0;
        async Task Pump(int ms)
        {
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < ms) { a.Pump(); b.Pump(); await Task.Delay(5); }
            a.Pump(); b.Pump();
        }
    }

    private sealed class Peer : IDisposable
    {
        internal readonly TcpSession Session = new();
        private readonly Channel<(GamePacketOpcode Opcode, byte[] Payload)> _packets = Channel.CreateUnbounded<(GamePacketOpcode, byte[])>();
        private World3DData _pending;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _nextSend;
        internal uint Id;
        internal World3DData World;
        internal readonly Dictionary<uint, Relay3DAction> States = new();
        internal readonly List<Relay3DAction> Actions = new();
        internal long SentPackets, SentBytes, ReceivedBytes;
        internal event Action<Relay3DAction> Received;
        internal Peer() { Session.PacketReceived += (op, payload) => _packets.Writer.TryWrite(((GamePacketOpcode)op, payload)); }
        internal async Task ConnectAsync(int port)
        {
            await Session.ConnectAsync("127.0.0.1", port); using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            while (World == null || !States.ContainsKey(Id)) Handle(await _packets.Reader.ReadAsync(timeout.Token));
        }
        internal void Pump() { while (_packets.Reader.TryRead(out var packet)) Handle(packet); }
        internal async Task SendAsync(Motion3DBatch batch)
        {
            double wait = _nextSend - _clock.Elapsed.TotalSeconds; if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait)); _nextSend = _clock.Elapsed.TotalSeconds + 0.2;
            byte[] payload = Movement3DProtocol.CreateBatch(batch); await Session.SendAsync((ushort)GamePacketOpcode.MovementActions3D, payload);
            ++SentPackets; SentBytes += payload.Length + TcpSession.HeaderSize;
        }
        internal Task ManualAsync(Motion3DAction action, ulong batchId)
        {
            Motion3DBatch batch = new() { MapId = World.MapId, Generation = World.Generation, BatchSequence = batchId, LatestClientTick = action.ClientTick };
            batch.Actions.Add(action); return SendAsync(batch);
        }
        private void Handle((GamePacketOpcode Opcode, byte[] Payload) packet)
        {
            switch (packet.Opcode)
            {
                case GamePacketOpcode.EnterGameResponse: Id = GameProtocol.ReadEnterGameResponse(packet.Payload).CharacterId; break;
                case GamePacketOpcode.World3D: _pending = Movement3DProtocol.ReadWorld(packet.Payload); break;
                case GamePacketOpcode.WorldBox3D: Movement3DProtocol.AddBox(_pending, packet.Payload); break;
                case GamePacketOpcode.WorldEnd3D: Movement3DProtocol.CompleteWorld(_pending, packet.Payload); World = _pending; _pending = null; break;
                case GamePacketOpcode.MovementState3D:
                    var state = Movement3DProtocol.ReadState(packet.Payload); Check(state.MapId == World.MapId && state.Generation == World.Generation, "Old 3D snapshot"); States[state.State.CharacterId] = state.State; break;
                case GamePacketOpcode.MovementActionsBroadcast3D:
                    var batch = Movement3DProtocol.ReadBroadcast(packet.Payload); Check(batch.MapId == World.MapId && batch.Generation == World.Generation, "Wrong 3D relay generation");
                    ReceivedBytes += packet.Payload.Length + 4;
                    foreach (var action in batch.Actions) { Actions.Add(action); States[action.CharacterId] = action; Received?.Invoke(action); }
                    break;
                case GamePacketOpcode.MapInfo: case GamePacketOpcode.PlayerEnterMap: case GamePacketOpcode.PlayerLeaveMap: break;
                // 몬스터가 없는 이동 전용 Map에서도 빈 목록과 현재 입장 번호를 검증한다.
                case GamePacketOpcode.FieldMonsters3D:
                    var monsters = FieldProtocol.ReadMonsters(packet.Payload); Check(monsters.MapId == World.MapId && monsters.Generation == World.Generation, "Wrong monster snapshot generation"); break;
                default: throw new InvalidDataException("Unexpected 3D fixture opcode " + packet.Opcode);
            }
        }
        public void Dispose() => Session.Dispose();
    }
}
