// 실제 서버가 커밋한 뒤 응답을 잃은 클라이언트의 조회와 인벤토리 복구를 검증한다.
internal static class TradeFaultIntegration
{
    internal static async Task<int> RunAsync()
    {
        await using var proxy=new TradeLossProxy(); using FieldServerIntegration.Peer a=new(),b=new();
        string previousPort=Environment.GetEnvironmentVariable("MMORPG_TEST_GAME_PORT");
        try { Environment.SetEnvironmentVariable("MMORPG_TEST_GAME_PORT",proxy.Port.ToString()); await a.ConnectAsync("test",1002); }
        finally { Environment.SetEnvironmentVariable("MMORPG_TEST_GAME_PORT",previousPort); }
        await b.ConnectAsync("test2",2001); await Task.WhenAll(a.MoveSafeAsync(),b.MoveSafeAsync());
        var originalA=await a.OperationAsync(InventoryOperation.Refresh); var originalB=await b.OperationAsync(InventoryOperation.Refresh);
        async Task Send(FieldServerIntegration.Peer peer,TradeOperation operation,ulong mesos=0,uint target=0,TradeSlotData[] slots=null)
        {
            await Task.Delay(130); var previous=peer.Trade;
            await peer.SendAsync(GamePacketOpcode.TradeRequest3D,TradeProtocol.Request(operation,previous?.Id??0,previous?.Revision??0,target,peer.Inventory.Revision,mesos,slots));
            await peer.PumpAsync(()=>peer.Trade!=previous);
        }
        await Send(a,TradeOperation.Invite,target:2001); await b.PumpAsync(()=>b.Trade!=null);
        await Send(b,TradeOperation.Accept); await a.PumpAsync(()=>a.Trade.Stage==TradeStage.Editing);
        await Send(a,TradeOperation.Offer,7,slots:new[]{new TradeSlotData { Category=1,Slot=0,Quantity=1 }}); await b.PumpAsync(()=>b.Trade.Revision==a.Trade.Revision);
        proxy.DropFinal=true;
        await Send(a,TradeOperation.Confirm); await b.PumpAsync(()=>b.Trade.Confirmed[0]); await Send(b,TradeOperation.Confirm);
        await proxy.TerminalDropped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await b.PumpAsync(()=>b.Trade.Stage==TradeStage.Completed); a.Drain();
        Program.Check(proxy.DroppedSnapshots>0 && a.Inventory.Mesos==originalA.Mesos,"Commit response loss was not injected");
        proxy.DropFinal=false; await Send(a,TradeOperation.Query);
        Program.Check(a.Trade.Stage==TradeStage.Completed && a.Inventory.Mesos==originalA.Mesos-7 && b.Inventory.Mesos==originalB.Mesos+7 && a.Inventory.Entries[24].Quantity==originalA.Entries[24].Quantity-1 && b.Inventory.Entries[24].Quantity==originalB.Entries[24].Quantity+1,"Trade QUERY completed without restoring lost inventory state");
        var revision=a.Inventory.Revision; await Send(a,TradeOperation.Query);
        Program.Check(a.Inventory.Mesos==originalA.Mesos-7 && a.Inventory.Revision==revision,"Repeated recovery exchanged again");
        Console.WriteLine("[PASS] Actual committed trade response loss restores current inventory through QUERY exactly once"); return 0;
    }
}
