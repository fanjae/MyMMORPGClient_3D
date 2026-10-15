internal static class AdaptiveTimingTests
{
    public static void Run()
    {
        var timing = new NetworkTiming3D();
        Program.Check(timing.TryBegin(0, out var first) && !timing.TryBegin(0.1, out _), "Overlapping probe accepted");
        Program.Check(!timing.Accept(first + 1, 0.1) && timing.Accept(first, 0.1) && !timing.Accept(first, 0.2), "Probe identity not checked");
        Program.Check(Math.Abs(timing.RttSeconds - 0.1) < 1e-9, "RTT was not measured as a round trip");
        timing.Reset();
        Program.Check(timing.TryBegin(1, out var second) && !timing.Accept(first, 1.1) && timing.Accept(second, 1.1), "Old connection probe affected new sample");
        var smooth = new AdaptivePlayback3D();
        for (int i = 0; i < 20; ++i) { smooth.Receive(i * 0.2, i * 0.2); smooth.SetNetwork(0.3, 0, 20); smooth.Advance(i * 0.2); }
        Program.Check(Math.Abs(smooth.BufferSeconds - 0.2) < 1e-9, "Stable high RTT unnecessarily added a full RTT to buffer");
        var jitter = new AdaptivePlayback3D();
        for (int i = 0; i < 20; ++i) { jitter.Receive(i * 0.2 + (i % 2) * 0.15, i * 0.2); jitter.SetNetwork(0.2, 0.06, 20); jitter.Advance(i * 0.2); }
        Program.Check(jitter.BufferSeconds > 0.3 && jitter.BufferSeconds <= 0.4, "Jitter buffer did not adapt within bounds");
        double before = jitter.BufferSeconds;
        jitter.SetNetwork(0, 0, 20); jitter.Advance(4.01);
        Program.Check(before - jitter.BufferSeconds <= 0.011, "Buffer shrank abruptly");
        var world = World3DData.Preview(); var remote = new RemoteMotion3D();
        var state = new Motion3DSimulation(world).Spawn(); state.X = -200;
        remote.Configure(world, new() { Action = new() { State = state } }, 0);
        Program.Check(remote.Add(new() { LatestClientTick = 1, Action = new() { Sequence = 1, ClientTick = 1, InputZ = 1000, State = state } }, 0), "Remote input rejected");
        remote.Sample(0.9, out var moving); remote.Sample(1.4, out var stopped); remote.Sample(1.8, out var frozen);
        Program.Check(stopped.Z > moving.Z && stopped.Z == frozen.Z, "Normal heartbeat interval or prediction cap broken");
        Console.WriteLine("[PASS] RTT identity/reset, stable delay, adaptive jitter bounds and bounded heartbeat extrapolation");
    }
}
