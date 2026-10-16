using System.Diagnostics;

// 현재 몬스터·드롭 주변 목록의 진입·이탈과 플레이어 전체 전송 상태를 실제 서버에서 비교한다.
internal static class FieldVisibilityIntegration
{
    internal static async Task<int> RunAsync()
    {
        using FieldServerIntegration.Peer anchor=new(),moving=new(); await anchor.ConnectAsync("test",1002); await moving.ConnectAsync("test2",2001);
        async Task Channel(FieldServerIntegration.Peer peer)
        {
            ulong generation=peer.World.Generation; using PacketWriter writer=new(); writer.Write((ushort)3);
            await peer.SendAsync(GamePacketOpcode.ChangeChannelRequest,writer.ToArray());
            await peer.PumpAsync(()=>peer.Channels?.Current==3 && peer.World.Generation!=generation && peer.Monsters!=null && peer.Drops!=null);
        }
        await Channel(anchor); await Channel(moving);
        var oldDrops=anchor.Drops?.Drops.Select(d=>d.Id).ToHashSet() ?? new HashSet<ulong>();
        for(ulong sequence=1;sequence<=24 && !(anchor.Drops?.Drops.Any(d=>!oldDrops.Contains(d.Id))??false);++sequence)
        {
            await Task.Delay(620); await anchor.SendAsync(GamePacketOpcode.BasicAttackRequest3D,CombatProtocol.Request(anchor.World.MapId,anchor.World.Generation,sequence,0));
            await anchor.PumpAsync(()=>anchor.Attack?.Sequence==sequence);
            Console.WriteLine($"ATTACK sequence={sequence} result={anchor.Attack.Result} monster={anchor.Attack.MonsterId} hp={anchor.Attack.Hp}");
            anchor.Drain();
        }
        if(!(anchor.Drops?.Drops.Any(d=>!oldDrops.Contains(d.Id))??false)) await anchor.PumpAsync(()=>anchor.Drops?.Drops.Any(d=>!oldDrops.Contains(d.Id))??false);
        ulong drop=anchor.Drops.Drops.First(d=>!oldDrops.Contains(d.Id)).Id; var motion=new ClientMotion3D(); motion.Configure(moving.World);
        var clock=Stopwatch.StartNew(); double previous=0;
        async Task Travel(int direction,double seconds)
        {
            double end=clock.Elapsed.TotalSeconds+seconds;
            while(clock.Elapsed.TotalSeconds<end)
            {
                double now=clock.Elapsed.TotalSeconds,delta=now-previous; previous=now;
                anchor.Drain(); moving.Drain(); motion.SetInput(0,direction,false); motion.Advance(delta);
                if(motion.TryCreateBatch(now,out var batch)) await moving.SendAsync(GamePacketOpcode.MovementActions3D,Movement3DProtocol.CreateBatch(batch));
                await Task.Delay(20);
            }
        }
        for(int cycle=0;cycle<3;++cycle)
        {
            await Travel(1,4.5); await Travel(0,1.3);
            Console.WriteLine($"BOUNDARY far cycle={cycle+1} x={motion.State.X:F3} z={motion.State.Z:F3} monster={moving.Monsters.Monsters.Any(m=>m.Id==1)} drop={moving.Drops.Drops.Any(d=>d.Id==drop)}");
            Program.Check(!moving.Monsters.Monsters.Any(m=>m.Id==1) && !moving.Drops.Drops.Any(d=>d.Id==drop),"Far observer retained the anchored monster/drop");
            Program.Check(moving.Remotes.Contains(1002) && anchor.Remotes.Contains(2001),"Player roster no longer uses current map-wide behavior");
            foreach(var monster in moving.Monsters.Monsters)
            {
                double dx=monster.X-motion.State.X,dz=monster.Z-motion.State.Z;
                Program.Check(dx*dx+dz*dz<=320*320+1,"Stopped observer received a monster beyond the 16m list boundary");
            }
            await Travel(-1,4.5); await Travel(0,1.3);
            Console.WriteLine($"BOUNDARY near cycle={cycle+1} x={motion.State.X:F3} z={motion.State.Z:F3} monster={moving.Monsters.Monsters.Any(m=>m.Id==1)} drop={moving.Drops.Drops.Any(d=>d.Id==drop)}");
            Program.Check(moving.Monsters.Monsters.Any(m=>m.Id==1) && moving.Drops.Drops.Any(d=>d.Id==drop),"Reentry omitted the same anchored monster/drop identity");
            Console.WriteLine($"[PASS] Actual visibility boundary cycle={cycle+1} monster reentry, same drop={drop}, map-wide player roster");
        }
        return 0;
    }
}
