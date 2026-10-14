using System.Diagnostics;
using System.Globalization;

// 테스트 계정으로 실제 LoginServer·GameServer에 접속해 동시 공격과 주변 목록 전송을 측정한다.
internal static class FieldLoadIntegration
{
    internal static async Task<int> RunAsync(int count,string layout)
    {
        Program.Check(count is 2 or 8 or 32,"Load count must be 2, 8 or 32");
        Program.Check(layout is "same" or "spread","Invalid load layout");
        var peers=new List<FieldServerIntegration.Peer>(); var motions=new List<ClientMotion3D>(); var attacks=new List<BasicAttack3D>();
        try
        {
            for (int i=0;i<count;++i)
            {
                var peer=new FieldServerIntegration.Peer(); peers.Add(peer); await peer.ConnectAsync($"load{i+1}",(uint)(3001+i));
                var motion=new ClientMotion3D(); motion.Configure(peer.World); motions.Add(motion); attacks.Add(new());
                foreach(var present in peers) present.Drain();
            }
            // 준비 시간에는 공격을 보내지 않고 분산 조건의 캐릭터만 각기 다른 위치로 이동한다.
            var clock=Stopwatch.StartNew(); double previous=0;
            while(clock.Elapsed.TotalSeconds<4)
            {
                double now=clock.Elapsed.TotalSeconds,delta=now-previous; previous=now;
                for(int i=0;i<count;++i)
                {
                    peers[i].Drain(); var motion=motions[i];
                    motion.SetInput(layout=="spread" && now<2.8 ? (i%2==0 ? 1 : -1) : 0,layout=="spread" && now<2.8 ? (i%4<2 ? 1 : -1) : 0,false);
                    motion.Advance(delta);
                    if(motion.TryCreateBatch(now,out var batch)) await peers[i].SendAsync(GamePacketOpcode.MovementActions3D,Movement3DProtocol.CreateBatch(batch));
                }
                await Task.Delay(20);
            }
            long txStart=peers.Sum(p=>p.Traffic().SentBytes),rxStart=peers.Sum(p=>p.Traffic().ReceivedBytes);
            long monsterStart=peers.Sum(p=>p.Traffic((ushort)GamePacketOpcode.FieldMonsters3D).ReceivedBytes);
            long attackStart=peers.Sum(p=>p.Traffic((ushort)GamePacketOpcode.BasicAttackEvent3D).ReceivedBytes);
            int attempts=0,peak=0; double nextAttack=clock.Elapsed.TotalSeconds;
            double sampleStart=clock.Elapsed.TotalSeconds;
            while(clock.Elapsed.TotalSeconds-sampleStart<12)
            {
                double now=clock.Elapsed.TotalSeconds,delta=now-previous; previous=now;
                bool pulse=now>=nextAttack; if(pulse)nextAttack=now+0.6;
                for(int i=0;i<count;++i)
                {
                    var peer=peers[i]; peer.Drain(); var motion=motions[i]; motion.SetInput(0,0,false);
                    if(pulse && attacks[i].Queue(true,now,motion)) ++attempts;
                    motion.Advance(delta);
                    if(motion.TryCreateBatch(now,out var batch))
                    {
                        bool attack=attacks[i].Take(batch,out ulong sequence,out ulong action);
                        await peer.SendAsync(GamePacketOpcode.MovementActions3D,Movement3DProtocol.CreateBatch(batch));
                        if(attack)await peer.SendAsync(GamePacketOpcode.BasicAttackRequest3D,CombatProtocol.Request(peer.World.MapId,peer.World.Generation,sequence,action));
                    }
                    peak=Math.Max(peak,peer.Traffic().PeakQueuedSendBytes);
                }
                await Task.Delay(20);
            }
            foreach(var peer in peers)peer.Drain();
            double seconds=clock.Elapsed.TotalSeconds-sampleStart;
            long tx=peers.Sum(p=>p.Traffic().SentBytes)-txStart,rx=peers.Sum(p=>p.Traffic().ReceivedBytes)-rxStart;
            long monster=peers.Sum(p=>p.Traffic((ushort)GamePacketOpcode.FieldMonsters3D).ReceivedBytes)-monsterStart;
            long attackBytes=peers.Sum(p=>p.Traffic((ushort)GamePacketOpcode.BasicAttackEvent3D).ReceivedBytes)-attackStart;
            Console.WriteLine(FormattableString.Invariant($"LOAD peers={count} layout={layout} seconds={seconds:F3} attempts={attempts} txBytes={tx} rxBytes={rx} rxBytesPerSecond={rx/seconds:F1} monsterBytes={monster} attackBytes={attackBytes} peakClientQueuedBytes={peak}"));
            Console.WriteLine("VISIBLE "+string.Join(",",peers.Select(p=>p.Monsters?.Monsters.Count ?? -1)));
            Console.WriteLine("DROPS "+string.Join(",",peers.Select(p=>p.Drops?.Drops.Count ?? -1)));
            Console.WriteLine("INPUT_POSITIONS "+string.Join(";",motions.Select(m=>FormattableString.Invariant($"{m.State.X:F1},{m.State.Z:F1}"))));
            Program.Check(peers.All(p=>!p.CombatPaused && p.Inventory.Hp>0 && p.Traffic().QueuedSendBytes==0),"Load stalled combat or left queued sends");
            return 0;
        }
        finally { foreach(var peer in peers)peer.Dispose(); }
    }
}
