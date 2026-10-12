using System.Diagnostics;

internal static class FieldHardeningTests
{
    internal static void Run()
    {
        // 차단된 동안 누른 이동·점프는 포커스나 UI가 복귀해도 떼기 전까지 적용하지 않는다.
        var gate = new GameplayInput3D();
        foreach (string reason in new[] { "chat", "inventory", "focus", "map", "reentry" })
        {
            gate.Reset(); Program.Check(gate.Filter(false, GameplayInput3D.Right | GameplayInput3D.Jump) == 0, reason);
            Program.Check(gate.Filter(true, GameplayInput3D.Right | GameplayInput3D.Jump) == 0 && !gate.CanPress(GameplayInput3D.Jump), "Held keys leaked after " + reason);
            gate.Filter(true, 0); Program.Check(gate.Filter(true, GameplayInput3D.Right) == GameplayInput3D.Right, "Fresh input lost after " + reason);
        }
        var motion = new ClientMotion3D(); motion.Configure(World3DData.Preview());
        motion.SetInput(0, 0, true); motion.StopInput(); motion.Advance(Motion3DSimulation.FixedDelta);
        Program.Check(motion.JumpId == 0, "Canceled jump remained buffered");
        motion.SetInput(0, 0, true); motion.Advance(Motion3DSimulation.FixedDelta);
        ulong jump = motion.JumpId;
        for (int i = 0; i < 15; ++i) { motion.SetInput(0, 0, true); motion.Advance(Motion3DSimulation.FixedDelta); }
        Program.Check(motion.JumpId == jump && !motion.State.Grounded, "Air jump was accepted");
        for (int i = 0; i < 120 && !motion.State.Grounded; ++i) { motion.StopInput(); motion.Advance(Motion3DSimulation.FixedDelta); }
        motion.SetInput(0, 0, true); motion.Advance(Motion3DSimulation.FixedDelta);
        Program.Check(motion.JumpId == jump + 1, "New jump rejected after landing");
        Console.WriteLine("[PASS] Input gates reset held keys and buffered jumps; air jump blocked and landing permits a new jump");

        var recovery = new InventoryRecovery3D(); recovery.Accept(InventoryResult.Success, 0);
        Program.Check(recovery.Begin(InventoryOperation.Use, 0) && recovery.Expire(5), "Lost response did not expire");
        Program.Check(!recovery.Begin(InventoryOperation.Use, 5) && recovery.RequiresRefresh, "Unconfirmed mutation replay allowed");
        for (int i = 0; i < 3; ++i)
        {
            double now = 10 + i * 10;
            Program.Check(recovery.TryRetry(now) && recovery.Begin(InventoryOperation.Refresh, now), "Refresh retry missing");
            recovery.Accept(InventoryResult.StorageError, now);
        }
        Program.Check(!recovery.TryRetry(100) && recovery.Begin(InventoryOperation.Refresh, 100), "Retries unbounded or manual refresh blocked");
        recovery.Accept(InventoryResult.Success, 100);
        Program.Check(!recovery.RequiresRefresh && recovery.Begin(InventoryOperation.Move, 101), "Recovered inventory stayed locked");
        recovery.Reset(); Program.Check(!recovery.Busy && recovery.RequiresRefresh, "Inventory state leaked across character switch");
        Console.WriteLine("[PASS] Inventory timeout blocks mutations, limits refresh retries and permits recovery without replay");

        var monster = new MonsterMotion3D();
        monster.Apply(new() { Id = 1, Yaw = 35900 }, 1, 0);
        monster.Apply(new() { Id = 1, X = 20, Yaw = 100 }, 11, 0.2);
        var middle = monster.Sample(0.3);
        Program.Check(Math.Abs(middle.X - 10) < 0.001 && (middle.Yaw < 0.001 || middle.Yaw > 359.999), "Monster interpolation/yaw wrap incorrect");
        Program.Check(monster.Sample(10).X == 20, "Monster predicted past the last server state");
        monster.Apply(new() { Id = 1, X = 5 }, 10, 11); Program.Check(monster.Sample(11).X == 20, "Older monster tick applied");
        monster.Apply(new() { Id = 1, X = 100 }, 12, 12); Program.Check(monster.Sample(12).X == 100, "Large monster discrepancy not snapped");
        var many = Enumerable.Range(0, 128).Select(_ => new MonsterMotion3D()).ToArray();
        var clock = Stopwatch.StartNew(); long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 300; ++frame)
            for (int i = 0; i < many.Length; ++i) { many[i].Apply(new() { Id = (uint)i + 1, X = frame * 0.01 }, (ulong)frame + 1, frame * 0.02); many[i].Sample(frame * 0.02 + 0.01); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated; clock.Stop();
        Program.Check(bytes < 4096, "Monster interpolation allocated per update");
        Console.WriteLine($"[PASS] Monster interpolation handles silence, old ticks, yaw wrap and large corrections; 128 x 300 updates {clock.Elapsed.TotalMilliseconds:F2} ms, {bytes} allocated bytes");
    }
}
