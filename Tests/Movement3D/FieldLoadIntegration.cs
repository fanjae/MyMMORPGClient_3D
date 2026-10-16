using System.Diagnostics;
using System.Globalization;

// 테스트 계정으로 실제 LoginServer·GameServer에 접속해 동시 공격과 주변 목록 전송을 측정한다.
internal static class FieldLoadIntegration
{
    internal static async Task<int> RunAsync(int count,string layout,int sampleSeconds=12)
    {
        Program.Check(count is 2 or 8 or 32 or 64 or 96 or 128,"Load count must be 2, 8, 32, 64, 96 or 128");
        Program.Check(sampleSeconds>=12 && sampleSeconds<=10800,"Invalid load duration");
        Program.Check(layout is "same" or "spread","Invalid load layout");
        var peers=new List<FieldServerIntegration.Peer>(); var motions=new List<ClientMotion3D>(); var attacks=new List<BasicAttack3D>();
        bool slow=Environment.GetEnvironmentVariable("MMORPG_LOAD_SLOW_PEER")=="1";
        await using var proxy=slow ? new SlowPeerProxy() : null;
        try
        {
            for (int i=0;i<count;++i)
            {
                var peer=new FieldServerIntegration.Peer(); peers.Add(peer);
                string previousPort=Environment.GetEnvironmentVariable("MMORPG_TEST_GAME_PORT");
                try { if(slow && i==0) Environment.SetEnvironmentVariable("MMORPG_TEST_GAME_PORT",proxy.Port.ToString()); await peer.ConnectAsync($"load{i+1}",(uint)(3001+i)); }
                finally { Environment.SetEnvironmentVariable("MMORPG_TEST_GAME_PORT",previousPort); }
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
            var measured=peers.Skip(slow?1:0).ToList(); if(slow) proxy.Enabled=true;
            long txStart=measured.Sum(p=>p.Traffic().SentBytes),rxStart=measured.Sum(p=>p.Traffic().ReceivedBytes);
            long monsterStart=measured.Sum(p=>p.Traffic((ushort)GamePacketOpcode.FieldMonsters3D).ReceivedBytes);
            long attackStart=measured.Sum(p=>p.Traffic((ushort)GamePacketOpcode.BasicAttackEvent3D).ReceivedBytes);
            int attempts=0,peak=0; double nextAttack=clock.Elapsed.TotalSeconds;
            double sampleStart=clock.Elapsed.TotalSeconds;
            Console.WriteLine($"MEASUREMENT_START utc={DateTime.UtcNow:O} peers={count} sampleSeconds={sampleSeconds} layout={layout}");
            var generations=peers.Select(p=>p.World.Generation).ToArray(); var deathAt=Enumerable.Repeat(-1.0,count).ToArray(); var reviveAt=new double[count];
            int reviveRequests=0; double nextProgress=sampleStart+60;
            while(clock.Elapsed.TotalSeconds-sampleStart<sampleSeconds)
            {
                double now=clock.Elapsed.TotalSeconds,delta=now-previous; previous=now;
                bool pulse=now>=nextAttack; if(pulse)nextAttack=now+0.6;
                for(int i=0;i<count;++i)
                {
                    var peer=peers[i]; peer.Drain();
                    if(!peer.Connected) { Program.Check(slow && i==0,"Normal load peer disconnected during measurement"); continue; }
                    var motion=motions[i];
                    if(peer.World.Generation!=generations[i]) { generations[i]=peer.World.Generation; motion.Configure(peer.World); attacks[i].Cancel(); }
                    // 장시간 검사 중 사망도 실제 부활 패킷으로 처리하고 DB에서 HP를 덮어쓰지 않는다.
                    if(sampleSeconds>600 && peer.Inventory.Hp==0)
                    {
                        if(deathAt[i]<0) deathAt[i]=now;
                        if(now-deathAt[i]>=3.3 && now>=reviveAt[i]) { await peer.SendAsync(GamePacketOpcode.ReviveRequest,Array.Empty<byte>()); reviveAt[i]=now+1; ++reviveRequests; }
                        continue;
                    }
                    deathAt[i]=-1; motion.SetInput(0,0,false);
                    if(pulse && attacks[i].Queue(true,now,motion)) ++attempts;
                    motion.Advance(delta);
                    if(motion.TryCreateBatch(now,out var batch))
                    {
                        bool attack=attacks[i].Take(batch,out ulong sequence,out ulong action);
                        try
                        {
                            await peer.SendAsync(GamePacketOpcode.MovementActions3D,Movement3DProtocol.CreateBatch(batch));
                            if(attack)await peer.SendAsync(GamePacketOpcode.BasicAttackRequest3D,CombatProtocol.Request(peer.World.MapId,peer.World.Generation,sequence,action));
                        }
                        catch(Exception error) when(slow && i==0 && (error is System.IO.IOException or System.Net.Sockets.SocketException or OperationCanceledException))
                        { /* 느린 수신자의 종료는 따로 기록하고 정상 수신자 검사를 계속한다. */ }
                    }
                    peak=Math.Max(peak,peer.Traffic().PeakQueuedSendBytes);
                }
                if(now>=nextProgress)
                {
                    using var process=Process.GetCurrentProcess();
                    long managed=GC.GetTotalMemory(false),heap=GC.GetGCMemoryInfo().HeapSizeBytes;
                    Console.WriteLine(FormattableString.Invariant($"PROGRESS seconds={now-sampleStart:F1} connected={measured.Count(p=>p.Connected)}/{measured.Count} paused={measured.Count(p=>p.CombatPaused)} dead={measured.Count(p=>p.Inventory.Hp==0)} reviveRequests={reviveRequests} privateBytes={process.PrivateMemorySize64} managedBytes={managed} managedSampleValid={managed>=0} lastGcHeapBytes={heap} txBytes={measured.Sum(p=>p.Traffic().SentBytes)-txStart} rxBytes={measured.Sum(p=>p.Traffic().ReceivedBytes)-rxStart}"));
                    nextProgress=now+60;
                }
                await Task.Delay(20);
            }
            // 표본 종료 직전 사망한 캐릭터는 기존 부활 대기 시간이 지난 뒤 최종 상태를 확인한다.
            double finishAt=clock.Elapsed.TotalSeconds+6;
            while(sampleSeconds>600 && clock.Elapsed.TotalSeconds<finishAt)
            {
                foreach(var peer in peers) peer.Drain();
                if(measured.All(p=>p.Inventory.Hp>0 && !p.CombatPaused)) break;
                double now=clock.Elapsed.TotalSeconds;
                for(int i=0;i<count;++i) if(peers[i].Connected && peers[i].Inventory.Hp==0 && now>=reviveAt[i])
                { await peers[i].SendAsync(GamePacketOpcode.ReviveRequest,Array.Empty<byte>()); reviveAt[i]=now+1; ++reviveRequests; }
                await Task.Delay(100);
            }
            foreach(var peer in peers)peer.Drain();
            double seconds=clock.Elapsed.TotalSeconds-sampleStart;
            long tx=measured.Sum(p=>p.Traffic().SentBytes)-txStart,rx=measured.Sum(p=>p.Traffic().ReceivedBytes)-rxStart;
            long monster=measured.Sum(p=>p.Traffic((ushort)GamePacketOpcode.FieldMonsters3D).ReceivedBytes)-monsterStart;
            long attackBytes=measured.Sum(p=>p.Traffic((ushort)GamePacketOpcode.BasicAttackEvent3D).ReceivedBytes)-attackStart;
            Console.WriteLine(FormattableString.Invariant($"LOAD peers={count} layout={layout} seconds={seconds:F3} attempts={attempts} txBytes={tx} rxBytes={rx} rxBytesPerSecond={rx/seconds:F1} monsterBytes={monster} attackBytes={attackBytes} peakClientQueuedBytes={peak}"));
            Console.WriteLine("VISIBLE "+string.Join(",",peers.Select(p=>p.Monsters?.Monsters.Count ?? -1)));
            Console.WriteLine("DROPS "+string.Join(",",peers.Select(p=>p.Drops?.Drops.Count ?? -1)));
            Console.WriteLine("INPUT_POSITIONS "+string.Join(";",motions.Select(m=>FormattableString.Invariant($"{m.State.X:F1},{m.State.Z:F1}"))));
            Console.WriteLine($"MEASURED normalPeers={measured.Count} slowPeer={slow} slowConnected={(slow?peers[0].Connected:false)} reviveRequests={reviveRequests}");
            if(slow)
            {
                Console.WriteLine(FormattableString.Invariant($"SLOW_RX bytes={proxy.ThrottledBytes} frames={proxy.ThrottledFrames} configuredBytesPerSecond=1024"));
                Program.Check(proxy.ThrottledFrames>0,"Slow receiver did not observe throttled frames");
            }
            Program.Check(measured.All(p=>p.Connected && !p.CombatPaused && p.Inventory.Hp>0 && p.Traffic().QueuedSendBytes==0),"Load stalled normal peers or left queued sends");
            return 0;
        }
        finally { foreach(var peer in peers)peer.Dispose(); }
    }
}
