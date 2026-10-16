using System.Diagnostics;
using System.Globalization;

// 실제 서버의 행동을 표시 모델에 재생해 수치 전환을 검사한다. 화면의 자연스러움 검사는 아니다.
internal static class MovementTransitionIntegration
{
    internal static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await using var proxy=new DelayedGameProxy(20,0); using FieldServerIntegration.Peer sender=new(),receiver=new();
        await sender.ConnectAsync("test",1002);
        string previous=Environment.GetEnvironmentVariable("MMORPG_TEST_GAME_PORT");
        try { Environment.SetEnvironmentVariable("MMORPG_TEST_GAME_PORT",proxy.Port.ToString()); await receiver.ConnectAsync("test2",2001); }
        finally { Environment.SetEnvironmentVariable("MMORPG_TEST_GAME_PORT",previous); }
        async Task Channel(FieldServerIntegration.Peer peer)
        {
            ulong generation=peer.World.Generation; using PacketWriter writer=new(); writer.Write((ushort)2);
            await peer.SendAsync(GamePacketOpcode.ChangeChannelRequest,writer.ToArray());
            await peer.PumpAsync(()=>peer.Channels?.Current==2 && peer.World.Generation!=generation && peer.Monsters!=null);
        }
        await Channel(sender); await Channel(receiver);
        var motion=await sender.MoveSafeAsync(); await receiver.MoveSafeAsync();
        RemoteMotion3D remote=null; NetworkTiming3D timing=new(); var watch=Stopwatch.StartNew();
        int packets=0,jumps=0,lands=0,snaps=0,sampledJumps=0,sampledLands=0,turns=0;
        bool? lastGrounded=null; ushort? lastYaw=null;
        double badPeak=0,stableBuffer=0,finalBuffer=0; Motion3DState? frozen=null;
        receiver.MovementReceived=relay=>
        {
            if(relay.CharacterId!=1002) return;
            if(remote==null) { remote=new(); remote.Configure(receiver.World,relay,watch.Elapsed.TotalSeconds); }
            else remote.Add(relay,watch.Elapsed.TotalSeconds);
            ++packets; if(relay.Action.Kind==Action3DKind.Jump) ++jumps; if(relay.Action.Kind==Action3DKind.Land) ++lands;
        };
        using var trace=new StreamWriter(Path.Combine(directory,"movement-transitions.csv"));
        trace.WriteLine("seconds,phase,sourceX,sourceY,sourceZ,remoteX,remoteY,remoteZ,sourceYaw,remoteYaw,remoteGrounded,bufferMs,rttMs,snap");
        double previousAt=0; int phase=-1,nextJump=0; double[] jumpAt={2,10,20,41};
        while(watch.Elapsed.TotalSeconds<49)
        {
            double now=watch.Elapsed.TotalSeconds,delta=now-previousAt; previousAt=now;
            int current=now<6?0:now<14?1:now<35?2:now<39?3:4;
            if(current!=phase)
            {
                phase=current; proxy.SetNetwork(phase==1?150:20,phase==1?60:0); proxy.PauseDownload=phase==3;
                Console.WriteLine($"PHASE {phase} utc={DateTime.UtcNow:O} seconds={now:F2}");
            }
            sender.Drain(); receiver.Drain();
            if(timing.TryBegin(now,out var sequence)) { using PacketWriter writer=new(); writer.Write(sequence); await receiver.SendAsync(GamePacketOpcode.TimingPing3D,writer.ToArray()); }
            timing.Accept(receiver.Pong,now);
            bool jump=nextJump<jumpAt.Length && now>=jumpAt[nextJump]; if(jump) ++nextJump;
            motion.SetInput((int)(now/4)%2==0 ? 1 : -1,0,jump); motion.Advance(delta);
            if(motion.TryCreateBatch(20+now,out var batch)) await sender.SendAsync(GamePacketOpcode.MovementActions3D,Movement3DProtocol.CreateBatch(batch));
            if(remote!=null)
            {
                remote.SetNetworkTiming(timing); remote.Sample(now,out var state); if(remote.Snap) ++snaps;
                if(lastGrounded==true && !state.Grounded) ++sampledJumps;
                if(lastGrounded==false && state.Grounded) ++sampledLands;
                if(lastYaw.HasValue && state.Yaw!=lastYaw.Value) ++turns;
                lastGrounded=state.Grounded; lastYaw=state.Yaw;
                Program.Check(double.IsFinite(state.X) && double.IsFinite(state.Y) && double.IsFinite(state.Z),"Remote pose became non-finite");
                if(phase==1) badPeak=Math.Max(badPeak,remote.PlaybackBufferSeconds);
                if(phase==2) stableBuffer=remote.PlaybackBufferSeconds;
                if(phase==4) finalBuffer=remote.PlaybackBufferSeconds;
                if(now>37.8 && now<38.8)
                {
                    if(frozen.HasValue) Program.Check(Math.Abs(state.X-frozen.Value.X)+Math.Abs(state.Y-frozen.Value.Y)+Math.Abs(state.Z-frozen.Value.Z)<0.001,"Remote kept predicting during prolonged download silence");
                    frozen=state;
                }
                trace.WriteLine(FormattableString.Invariant($"{now:F3},{phase},{motion.State.X:F3},{motion.State.Y:F3},{motion.State.Z:F3},{state.X:F3},{state.Y:F3},{state.Z:F3},{motion.State.Yaw},{state.Yaw},{state.Grounded},{remote.PlaybackBufferSeconds*1000:F2},{timing.RttSeconds*1000:F2},{remote.Snap}"));
            }
            await Task.Delay(20);
        }
        motion.SetInput(0,0,false); motion.Advance(0.02);
        Motion3DBatch stopped;
        while(!motion.TryCreateBatch(20+watch.Elapsed.TotalSeconds,out stopped)) { motion.Advance(0.02); await Task.Delay(20); }
        await sender.SendAsync(GamePacketOpcode.MovementActions3D,Movement3DProtocol.CreateBatch(stopped));
        Program.Check(packets>10 && jumps>0 && lands>0 && sampledJumps==4 && sampledLands==4 && turns>=4 && badPeak>0.22 && stableBuffer<badPeak && frozen.HasValue && snaps>0,"Network transition evidence missing");
        receiver.MovementReceived=relay=> { if(receiver.World.MapId==100000003 && relay.CharacterId==1002) throw new InvalidDataException("Old-map movement crossed world reset"); };
        await receiver.ChangeMapAsync(100000003); await Task.Delay(700); receiver.Drain();
        receiver.MovementReceived=null; Snapshot3DData? reentry=null; receiver.SnapshotReceived=s=> { if(s.State.CharacterId==1002) reentry=s; };
        await receiver.ChangeMapAsync(100000002); receiver.Drain();
        if(reentry==null) await receiver.PumpAsync(()=>reentry!=null);
        Program.Check(reentry.HasValue && reentry.Value.Generation==receiver.World.Generation,"Map reentry omitted current movement baseline");
        Console.WriteLine(FormattableString.Invariant($"[PASS] Actual network transitions packets={packets} jumps={jumps} lands={lands} sampledJumps={sampledJumps} sampledLands={sampledLands} turns={turns} recoverySnaps={snaps} degradedBufferMs={badPeak*1000:F2} stableBufferMs={stableBuffer*1000:F2} finalBufferMs={finalBuffer*1000:F2}; map reset/reentry has a fresh baseline"));
        return 0;
    }
}
