internal static class MotionTransitionTests
{
    internal static void Run()
    {
        var playback=new AdaptivePlayback3D();
        for(int i=0;i<50;++i) { double now=i*0.02; playback.SetNetwork(0.3,0.08,20); playback.Advance(now); }
        double enlarged=playback.BufferSeconds;
        Program.Check(enlarged>=0.35 && enlarged<=0.4,"Degraded network did not enlarge playback buffer");
        for(int i=50;i<550;++i) { playback.SetNetwork(0.03,0,20); playback.Advance(i*0.02); }
        Program.Check(playback.BufferSeconds<enlarged && Math.Abs(playback.BufferSeconds-0.2)<0.001,"Stable network did not restore baseline buffer");
        var world=World3DData.Preview(); var state=new Motion3DSimulation(world).Spawn(); state.X=-220; state.Z=-220;
        Relay3DAction Relay(ulong sequence,ulong tick,Motion3DState pose,short x=0)=>new() { LatestClientTick=tick,Action=new() { Sequence=sequence,ClientTick=tick,State=pose,InputX=x } };
        var remote=new RemoteMotion3D(); remote.Configure(world,Relay(0,0,state),0); remote.Add(Relay(1,1,state,1000),0);
        remote.Sample(1.4,out var frozen); remote.Sample(8,out var later);
        Program.Check(frozen.X==later.X && frozen.Y==later.Y && frozen.Z==later.Z,"Silence continued unlimited prediction");
        state.X=-100; remote.Add(Relay(2,400,state,1000),8); remote.Sample(8,out var recovered);
        Program.Check(remote.Snap && Math.Abs(recovered.X-state.X)<0.001,"Resume replayed pre-gap motion instead of a fresh checkpoint");
        remote.Add(Relay(3,420,state,1000),8.1);
        var destination=state; destination.X=150; destination.Z=150;
        remote.Configure(world,Relay(0,0,destination),9); remote.Sample(9.3,out var reset);
        Program.Check(reset.X==destination.X && reset.Z==destination.Z,"World reset replayed queued old actions");
        Program.Check(!remote.Add(Relay(0,0,state),9.4),"Duplicate snapshot was replayed as a new action");
        Console.WriteLine("[PASS] Network deterioration/recovery, silence freeze, fresh resume and world reset transitions");
    }
}
