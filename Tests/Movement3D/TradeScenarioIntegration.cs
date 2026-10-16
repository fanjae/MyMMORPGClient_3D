using System.Diagnostics;

// 실제 패킷으로 거래 제한·제안 변경·취소·만료를 검사하며 화면 조작 결과와 구분한다.
internal static class TradeScenarioIntegration
{
    private static async Task PumpFor(FieldServerIntegration.Peer a,FieldServerIntegration.Peer b,int milliseconds)
    { var watch=Stopwatch.StartNew(); while(watch.ElapsedMilliseconds<milliseconds) { a.Drain(); b?.Drain(); await Task.Delay(20); } }
    private static async Task<TradeStateData> Send(FieldServerIntegration.Peer peer,TradeOperation operation,ulong mesos=0,TradeSlotData[] slots=null,uint target=0)
    {
        await Task.Delay(140); peer.Drain(); var before=peer.Trade;
        await peer.SendAsync(GamePacketOpcode.TradeRequest3D,TradeProtocol.Request(operation,before?.Id??0,before?.Revision??0,target,peer.Inventory.Revision,mesos,slots));
        await peer.PumpAsync(()=>peer.Trade!=before); var result=peer.Trade;
        if(result.Id==0 && before!=null) peer.Trade=before;
        return result;
    }
    private static TradeSlotData Source(InventoryData data,uint item,ushort quantity=1)
    { int slot=Array.FindIndex(data.Entries,e=>e.ItemId==item); Program.Check(slot>=0,"Trade source item missing"); return new() {Category=(byte)(slot/24),Slot=(byte)(slot%24),Quantity=quantity}; }
    internal static async Task<int> RunAsync()
    {
        using FieldServerIntegration.Peer a=new(),b=new(); await a.ConnectAsync("test",1002); await b.ConnectAsync("test2",2001);
        await Task.WhenAll(a.MoveSafeAsync(),b.MoveSafeAsync());
        var originalA=await a.OperationAsync(InventoryOperation.Refresh); var originalB=await b.OperationAsync(InventoryOperation.Refresh);
        ulong sword=originalA.Entries[Source(originalA,1).Slot].InstanceId,hat=originalB.Entries[Source(originalB,2).Slot].InstanceId;
        async Task Open()
        { await Send(a,TradeOperation.Invite,target:2001); await b.PumpAsync(()=>b.Trade?.Id==a.Trade.Id); await Send(b,TradeOperation.Accept); await a.PumpAsync(()=>a.Trade.Stage==TradeStage.Editing); }
        await Open(); ulong generation=a.World.Generation;
        a.ExpectMapReject=true; a.LastMapResult=null;
        await a.SendAsync(GamePacketOpcode.ChangeMapRequest,GameProtocol.CreateChangeMapRequest(100000003)); await a.PumpAsync(()=>a.LastMapResult.HasValue);
        Program.Check(a.LastMapResult!=ChangeMapResult.Success && a.World.Generation==generation,"Editing trade allowed map transfer");
        using(var writer=new PacketWriter()) { writer.Write((ushort)2); await a.SendAsync(GamePacketOpcode.ChangeChannelRequest,writer.ToArray()); }
        await a.PumpAsync(()=>a.Channels?.Result==ChannelResult.Busy);
        Program.Check(a.Channels.Current==1 && a.World.Generation==generation,"Editing trade changed channel");
        var invalid=await Send(a,TradeOperation.Offer,originalA.Mesos+1);
        Program.Check(invalid.Result==TradeResult.Invalid,"Mesos above balance accepted");
        var duplicate=new[]{Source(originalA,101),Source(originalA,101)};
        Program.Check((await Send(a,TradeOperation.Offer,slots:duplicate)).Result==TradeResult.Invalid,"Duplicate source slot accepted");
        await Send(a,TradeOperation.Offer,5,new[]{Source(originalA,1),Source(originalA,101,2)}); await b.PumpAsync(()=>b.Trade.Revision==a.Trade.Revision);
        await Send(b,TradeOperation.Offer,2,new[]{Source(originalB,2),Source(originalB,102)}); await a.PumpAsync(()=>a.Trade.Revision==b.Trade.Revision);
        ulong oldRevision=a.Trade.Revision;
        byte[] repeat=TradeProtocol.Request(TradeOperation.Confirm,a.Trade.Id,oldRevision);
        for(int i=0;i<10;++i) await a.SendAsync(GamePacketOpcode.TradeRequest3D,repeat);
        await PumpFor(a,b,450); Program.Check(a.Trade.Confirmed[0] && !a.Trade.Confirmed[1],"Rapid confirms exchanged before bilateral approval");
        await Send(b,TradeOperation.Offer,0,Array.Empty<TradeSlotData>()); await a.PumpAsync(()=>a.Trade.Revision==b.Trade.Revision);
        Program.Check(!a.Trade.Confirmed[0] && !a.Trade.Confirmed[1] && a.Trade.Offers[1].Items.Count==0,"Offer removal retained confirmations");
        await Task.Delay(140); var state=a.Trade;
        await a.SendAsync(GamePacketOpcode.TradeRequest3D,TradeProtocol.Request(TradeOperation.Confirm,state.Id,oldRevision));
        await a.PumpAsync(()=>a.Trade!=state); Program.Check(a.Trade.Result==TradeResult.Stale && !a.Trade.Confirmed[0],"Old confirmation applied after offer change");
        await Send(b,TradeOperation.Offer,2,new[]{Source(originalB,2),Source(originalB,102)}); await a.PumpAsync(()=>a.Trade.Revision==b.Trade.Revision);
        await Send(a,TradeOperation.Confirm); await b.PumpAsync(()=>b.Trade.Confirmed[0]); await Send(b,TradeOperation.Confirm);
        await Task.WhenAll(a.PumpAsync(()=>a.Trade.Stage==TradeStage.Completed),b.PumpAsync(()=>b.Trade.Stage==TradeStage.Completed));
        var afterA=await a.OperationAsync(InventoryOperation.Refresh); var afterB=await b.OperationAsync(InventoryOperation.Refresh);
        Program.Check(afterA.Mesos==originalA.Mesos-3 && afterB.Mesos==originalB.Mesos+3 && afterA.Entries.Any(e=>e.InstanceId==hat) && afterB.Entries.Any(e=>e.InstanceId==sword),"Equipment owner or mesos exchange failed");
        Console.WriteLine("[PASS] Actual trade map/channel restrictions, limits, removal, rapid/stale confirms and equipment exchange");
        ulong completed=a.Trade.Id; await Open(); ulong active=a.Trade.Id;
        await Task.Delay(140); state=a.Trade; await a.SendAsync(GamePacketOpcode.TradeRequest3D,TradeProtocol.Request(TradeOperation.Query,completed,0)); await a.PumpAsync(()=>a.Trade!=state);
        Program.Check(a.Trade.Id==active && a.Trade.Stage==TradeStage.Editing,"Historical query replaced active transaction");
        await Send(a,TradeOperation.Confirm); await b.PumpAsync(()=>b.Trade.Confirmed[0]);
        Program.Check((await Send(b,TradeOperation.Confirm)).Result==TradeResult.Invalid,"Empty trade committed");
        await Send(a,TradeOperation.Query); Program.Check(a.Trade.Confirmed[0] && !a.Trade.Confirmed[1],"Rejected empty trade retained both confirmations");
        b.Dispose(); await a.PumpAsync(()=>a.Trade.Stage==TradeStage.Cancelled);
        Program.Check((await a.OperationAsync(InventoryOperation.Refresh)).Mesos==afterA.Mesos,"Disconnect cancellation changed inventory");
        Console.WriteLine("[PASS] Historical queries preserve active trade; empty offers and precommit disconnect remain recoverable");
        await Task.Delay(300); using FieldServerIntegration.Peer replacement=new(); await replacement.ConnectAsync("test2",2001);
        await Send(a,TradeOperation.Invite,target:2001); await replacement.PumpAsync(()=>replacement.Trade!=null);
        await PumpFor(a,replacement,31000); Program.Check(a.Trade.Stage==TradeStage.Cancelled && a.Trade.Result==TradeResult.Expired,"30-second invitation did not expire");
        Console.WriteLine("[PASS] Actual 30-second invitation expiry");
        await Send(a,TradeOperation.Invite,target:2001); await replacement.PumpAsync(()=>replacement.Trade.Id==a.Trade.Id);
        await Send(replacement,TradeOperation.Accept); await a.PumpAsync(()=>a.Trade.Stage==TradeStage.Editing);
        await PumpFor(a,replacement,121000); Program.Check(a.Trade.Stage==TradeStage.Cancelled && a.Trade.Result==TradeResult.Expired,"2-minute editing did not expire");
        Program.Check((await a.OperationAsync(InventoryOperation.Refresh)).Mesos==afterA.Mesos,"Expiry changed committed inventory");
        Console.WriteLine("[PASS] Actual 2-minute editing expiry releases inventory operations"); return 0;
    }
    internal static async Task<int> DamageAsync()
    {
        using FieldServerIntegration.Peer a=new(),b=new(); await a.ConnectAsync("test",1002); await b.ConnectAsync("test2",2001);
        await Send(a,TradeOperation.Invite,target:2001); await b.PumpAsync(()=>b.Trade!=null); await Send(b,TradeOperation.Accept); await a.PumpAsync(()=>a.Trade.Stage==TradeStage.Editing);
        await a.PumpAsync(()=>a.Trade.Stage==TradeStage.Cancelled);
        Program.Check(a.Trade.Result==TradeResult.Unavailable,"Incoming damage did not cancel editing trade");
        await a.OperationAsync(InventoryOperation.Refresh);
        Console.WriteLine("[PASS] Actual monster damage cancels editing trade and releases inventory operations"); return 0;
    }
}
