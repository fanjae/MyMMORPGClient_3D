using System.Diagnostics;

// 거래 결과 조회가 별도로 진행 중인 피격 복구의 조작 제한을 풀지 않는지 확인한다.
internal static class TradeRecoveryIntegration
{
    internal static async Task<int> RunAsync(string directory,bool separateChannel=false)
    {
        using FieldServerIntegration.Peer owner=new(); await owner.ConnectAsync("test",1002);
        if(separateChannel)
        {
            ulong generation=owner.World.Generation;
            using(var writer=new PacketWriter())
            {
                writer.Write((ushort)2); await owner.SendAsync(GamePacketOpcode.ChangeChannelRequest,writer.ToArray());
            }
            await owner.PumpAsync(()=>owner.Channels?.Current==2 && owner.World.Generation!=generation && owner.Monsters!=null);
        }
        ushort hp=owner.Inventory.Hp; ulong revision=owner.Inventory.Revision;
        await owner.PumpAsync(()=>owner.CombatPaused);
        await owner.SendAsync(GamePacketOpcode.TradeRequest3D,TradeProtocol.Request(TradeOperation.Query,81000129999,0));
        await owner.PumpAsync(()=>owner.Trade?.Id==81000129999 && owner.Trade.Stage==TradeStage.Cancelled);
        Program.Check(owner.Trade.Result==TradeResult.Expired,"Unknown receipt did not finish its inventory lookup");
        var watch=Stopwatch.StartNew();
        while(watch.ElapsedMilliseconds<400)
        {
            owner.Drain(); Program.Check(owner.CombatPaused,"Trade receipt lookup lifted active damage recovery");
            await Task.Delay(20);
        }
        await owner.SendAsync(GamePacketOpcode.BasicAttackRequest3D,CombatProtocol.Request(owner.World.MapId,owner.World.Generation,991,0));
        await owner.PumpAsync(()=>owner.Attack?.Sequence==991);
        Program.Check(owner.Attack.Result==BasicAttackResult.Unavailable,"Receipt lookup permitted an attack before damage recovery");
        File.WriteAllText(Path.Combine(directory,"recovery-held"),"paused after receipt lookup");
        await owner.PumpAsync(()=>!owner.CombatPaused && owner.Inventory.Hp<hp);
        Program.Check(owner.Inventory.Hp==hp-10 && owner.Inventory.Revision==revision+1,"Receipt lookup lost or duplicated recovered damage");
        Console.WriteLine("[PASS] Trade receipt inventory lookup preserves damage recovery; the same damage commits once and then resumes");
        return 0;
    }
}
