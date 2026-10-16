using System.Diagnostics;

// 기본 점프 모터가 만든 경계 양쪽 상태를 실제 서버에 보내며 실제 키 입력 검사로 기록하지 않는다.
internal static class JumpBoundaryIntegration
{
    internal static async Task<int> RunAsync()
    {
        using FieldServerIntegration.Peer attacker=new(),observer=new(); await attacker.ConnectAsync("test",1002); await observer.ConnectAsync("test2",2001);
        foreach(var peer in new[]{attacker,observer})
        {
            ulong generation=peer.World.Generation; using PacketWriter writer=new(); writer.Write((ushort)2);
            await peer.SendAsync(GamePacketOpcode.ChangeChannelRequest,writer.ToArray());
            await peer.PumpAsync(()=>peer.Channels?.Current==2 && peer.World.Generation!=generation && peer.Monsters!=null);
        }
        Program.Check(attacker.World.JumpSpeed==160 && attacker.World.Gravity==400,"Expected the unmodified default jump settings");
        var motion=new ClientMotion3D(); motion.Configure(attacker.World); var attack=new BasicAttack3D(); var clock=Stopwatch.StartNew();
        ulong sent=0; double queuedY=0;
        async Task SendPending()
        {
            double now=clock.Elapsed.TotalSeconds;
            if(motion.TryCreateBatch(now,out var batch))
            {
                bool ready=attack.Take(batch,out var sequence,out var action); await attacker.SendAsync(GamePacketOpcode.MovementActions3D,Movement3DProtocol.CreateBatch(batch));
                if(ready) { sent=sequence; await attacker.SendAsync(GamePacketOpcode.BasicAttackRequest3D,CombatProtocol.Request(attacker.World.MapId,attacker.World.Generation,sequence,action)); }
            }
            attacker.Drain(); observer.Drain();
        }
        async Task Jump(bool above)
        {
            bool queued=false;
            for(int step=0;step<52;++step)
            {
                motion.SetInput(0,0,step==0); motion.Advance(0.02);
                bool boundary=above ? motion.State.Y>30 : motion.State.Y>29.8 && motion.State.Y<=30;
                if(!queued && boundary) { queuedY=motion.State.Y; Program.Check(attack.Queue(true,clock.Elapsed.TotalSeconds,motion),"Boundary attack was not queued"); queued=true; }
                await SendPending(); await Task.Delay(20);
            }
            Program.Check(queued,"Default jump did not reach the selected boundary");
            if(attacker.Attack?.Sequence!=sent) await attacker.PumpAsync(()=>attacker.Attack?.Sequence==sent);
            if(observer.Attack?.Sequence!=sent) await observer.PumpAsync(()=>observer.Attack?.Sequence==sent && observer.Attack.CharacterId==1002);
            Program.Check(attacker.Attack.Result==observer.Attack.Result && attacker.Attack.Damage==observer.Attack.Damage,"Observers disagreed about the boundary result");
            Console.WriteLine(FormattableString.Invariant($"BOUNDARY above={above} queuedY={queuedY:F3} sequence={sent} result={attacker.Attack.Result} yaw={attacker.Attack.Yaw} damage={attacker.Attack.Damage}"));
        }
        await Jump(true);
        Program.Check(attacker.Attack.Result==BasicAttackResult.Miss && attacker.Attack.Damage==0 && queuedY>30,"Above-1.5m default jump attack hit a ground target");
        Console.WriteLine(FormattableString.Invariant($"[PASS] Actual default jump y={queuedY:F3} units ({queuedY*0.05:F4}m) above 1.5m misses on both peers"));
        Program.Check(motion.State.Grounded,"Default jump did not land");
        await Task.Delay(650); await Jump(false);
        Program.Check(attacker.Attack.Result is BasicAttackResult.Hit or BasicAttackResult.Killed && attacker.Attack.Damage>0 && queuedY<=30,"Within-1.5m default jump attack was rejected");
        Console.WriteLine(FormattableString.Invariant($"[PASS] Actual default jump y={queuedY:F3} units ({queuedY*0.05:F4}m) within 1.5m hits on both peers")); return 0;
    }
}
