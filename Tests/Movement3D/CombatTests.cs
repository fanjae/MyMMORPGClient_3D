internal static class CombatTests
{
    internal static void Run()
    {
        Program.Check(CombatProtocol.Request(1, 2, 3, 4).Length == 28 && GameProtocol.ProtocolVersion == 12, "Attack request layout/version");
        using PacketWriter writer = new(); writer.Write((uint)1); writer.Write((ulong)2); writer.Write((ulong)3); writer.Write((uint)1001);
        writer.Write((byte)BasicAttackResult.Hit); writer.Write((uint)1); writer.Write((ushort)40); writer.Write((ushort)60); writer.Write((ulong)2);
        writer.Write((ushort)20); writer.Write((ushort)0);
        var hit = CombatProtocol.Read(writer.ToArray()); Program.Check(hit.Hp == 40 && hit.Damage == 20 && writer.ToArray().Length == 45, "Attack result layout");
        Console.WriteLine("[PASS] Combat protocol request/event sizes and authoritative damage");
        var motion = new ClientMotion3D(); motion.Configure(World3DData.Preview()); motion.Advance(Motion3DSimulation.FixedDelta);
        var attack = new BasicAttack3D();
        Program.Check(!attack.Queue(false, 0, motion) && attack.Queue(true, 0, motion) && !attack.Queue(true, 0.1, motion), "Attack input gates/cooldown");
        motion.Advance(Motion3DSimulation.FixedDelta);
        ulong sequence=0, action=0;
        Program.Check(motion.TryCreateBatch(0, out var batch) && attack.Take(batch, out sequence, out action) && sequence == 1 &&
            action == batch.Actions[^1].Sequence, "Attack sent before its movement checkpoint");
        Program.Check(!attack.Queue(true, 0.3, motion) && attack.Queue(true, 0.6, motion), "Attack cooldown failed");
        Program.Check(attack.CanSend(sequence), "Fresh attack was canceled before movement send");
        attack.Cancel(); motion.TryCreateBatch(1, out batch); Program.Check(!attack.Take(batch, out _, out _), "Canceled attack crossed map/focus boundary");
        Program.Check(!attack.CanSend(sequence), "Attack already taken from the queue survived focus loss during movement send");
        Program.Check(attack.Queue(true, 1.2, motion), "Cancel blocked a fresh attack"); motion.Advance(Motion3DSimulation.FixedDelta);
        Program.Check(motion.TryCreateBatch(1.4, out batch) && attack.Take(batch, out ulong freshSequence, out _) && attack.CanSend(freshSequence), "Fresh input failed after canceled in-flight attack");
        Console.WriteLine("[PASS] Attacks wait for validated movement, preserve send budget and cancel buffered input");
        Program.Check(VitalsFill.Ratio(50, 100) == 0.5f && VitalsFill.Ratio(0, 0) == 0 && VitalsFill.Ratio(200, 100) == 1, "HP/MP gauge invalid ratio");
        Console.WriteLine("[PASS] Vitals gauge handles loading, partial/full values and over-range values");
    }
}
