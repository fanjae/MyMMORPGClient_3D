// 실제 로그인·게임 서버의 제안 변경, 양측 확인과 중복 확정을 검증한다.
internal static class TradeIntegration
{
    public static async Task<int> QueryAsync(ulong id)
    {
        using FieldServerIntegration.Peer peer=new(); await peer.ConnectAsync("test",1002);
        var before=await peer.OperationAsync(InventoryOperation.Refresh);
        await peer.SendAsync(GamePacketOpcode.TradeRequest3D,TradeProtocol.Request(TradeOperation.Query,id,0));
        await peer.PumpAsync(()=>peer.Trade!=null);
        Program.Check(peer.Trade.Id==id && peer.Trade.Stage==TradeStage.Completed && peer.Trade.Result==TradeResult.Success,"Restarted server did not restore the committed receipt");
        var after=await peer.OperationAsync(InventoryOperation.Refresh);
        Program.Check(before.Revision==after.Revision && before.Mesos==after.Mesos && before.Entries.Select(e=>(e.ItemId,e.Quantity,e.InstanceId)).SequenceEqual(after.Entries.Select(e=>(e.ItemId,e.Quantity,e.InstanceId))),"Receipt query changed current inventory");
        Console.WriteLine("[PASS] Restarted actual server restores durable trade result without exchanging again"); return 0;
    }
    public static async Task<int> RunAsync()
    {
        using FieldServerIntegration.Peer a=new(), b=new();
        await a.ConnectAsync("test",1002); await b.ConnectAsync("test2",2001);
        await Task.WhenAll(a.MoveSafeAsync(),b.MoveSafeAsync());
        var originalA=await a.OperationAsync(InventoryOperation.Refresh); var originalB=await b.OperationAsync(InventoryOperation.Refresh);
        TradeSlotData Source(InventoryData inventory,uint item)
        { int index=Array.FindIndex(inventory.Entries,e=>e.ItemId==item); Program.Check(index>=0,"Trade fixture item missing"); return new() {Category=(byte)(index/24),Slot=(byte)(index%24),Quantity=1}; }
        async Task Send(FieldServerIntegration.Peer peer,TradeOperation operation,ulong mesos=0,TradeSlotData[] slots=null,uint target=0)
        {
            await Task.Delay(130); var previous=peer.Trade;
            await peer.SendAsync(GamePacketOpcode.TradeRequest3D,TradeProtocol.Request(operation,previous?.Id??0,previous?.Revision??0,target,peer.Inventory.Revision,mesos,slots));
            await peer.PumpAsync(()=>peer.Trade!=previous);
        }
        await Send(a,TradeOperation.Invite,target:2001); await b.PumpAsync(()=>b.Trade!=null);
        Program.Check(a.Trade.Stage==TradeStage.Invited && a.Trade.Id==b.Trade.Id,"Trade invitation missing");
        await Send(b,TradeOperation.Accept); await a.PumpAsync(()=>a.Trade.Stage==TradeStage.Editing);
        var beforeLocked=a.Inventory;
        await a.SendAsync(GamePacketOpcode.InventoryRequest,FieldProtocol.InventoryRequest(InventoryOperation.Use,1,Source(a.Inventory,101).Slot,0,a.Inventory.Revision,900000001));
        await a.PumpAsync(()=>a.Inventory!=beforeLocked && a.Inventory.RequestId==900000001); var locked=a.Inventory;
        Program.Check(locked.Result!=InventoryResult.Success && locked.Revision==originalA.Revision,"Editing trade allowed inventory mutation");
        await Send(a,TradeOperation.Offer,100,new[]{Source(a.Inventory,101)});
        await b.PumpAsync(()=>b.Trade.Revision==a.Trade.Revision);
        await Send(a,TradeOperation.Confirm); await b.PumpAsync(()=>b.Trade.Confirmed[0]);
        await Send(b,TradeOperation.Offer,50,new[]{Source(b.Inventory,102)});
        await a.PumpAsync(()=>a.Trade.Revision==b.Trade.Revision);
        Program.Check(!a.Trade.Confirmed[0] && !a.Trade.Confirmed[1],"Offer change preserved old confirmations");
        Console.WriteLine("[PASS] Actual server invite/accept blocks inventory changes and offer changes clear both confirmations");
        await Send(a,TradeOperation.Confirm); await b.PumpAsync(()=>b.Trade.Confirmed[0]);
        await Send(b,TradeOperation.Confirm);
        await Task.WhenAll(a.PumpAsync(()=>a.Trade.Stage==TradeStage.Completed),b.PumpAsync(()=>b.Trade.Stage==TradeStage.Completed));
        var afterA=await a.OperationAsync(InventoryOperation.Refresh); var afterB=await b.OperationAsync(InventoryOperation.Refresh);
        Program.Check(afterA.Mesos==originalA.Mesos-50 && afterB.Mesos==originalB.Mesos+50,"Trade mesos not conserved");
        int Count(InventoryData data,uint item)=>data.Entries.Where(e=>e.ItemId==item).Sum(e=>e.Quantity);
        Program.Check(Count(afterA,101)==Count(originalA,101)-1 && Count(afterB,101)==Count(originalB,101)+1 && Count(afterA,102)==Count(originalA,102)+1,"Trade item quantities incorrect");
        await Send(b,TradeOperation.Confirm);
        Program.Check(b.Trade.Stage==TradeStage.Completed && (await a.OperationAsync(InventoryOperation.Refresh)).Mesos==afterA.Mesos,"Duplicate confirmation exchanged again");
        Console.WriteLine("[PASS] Actual server bilateral item/mesos exchange and duplicate confirmation once");
        await Send(a,TradeOperation.Invite,target:2001); await b.PumpAsync(()=>b.Trade.Id==a.Trade.Id);
        await Send(b,TradeOperation.Accept); await a.PumpAsync(()=>a.Trade.Stage==TradeStage.Editing);
        await Send(a,TradeOperation.Cancel); await b.PumpAsync(()=>b.Trade.Stage==TradeStage.Cancelled);
        Program.Check((await a.OperationAsync(InventoryOperation.Refresh)).Mesos==afterA.Mesos,"Cancellation changed inventory");
        Console.WriteLine("[PASS] Actual server cancellation preserves inventory"); return 0;
    }
}
