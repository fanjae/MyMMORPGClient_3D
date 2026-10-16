// DB 행 잠금으로 확정을 지연시킨 뒤 양측 연결을 끊고 같은 거래 번호를 재조회한다.
internal static class TradeDisconnectIntegration
{
    internal static async Task<int> RunAsync(string directory)
    {
        var a=new FieldServerIntegration.Peer(); var b=new FieldServerIntegration.Peer();
        ulong tradeId=0,mesosA=0,mesosB=0;
        async Task Send(FieldServerIntegration.Peer peer,TradeOperation operation,ulong mesos=0,uint target=0)
        {
            await Task.Delay(140); var before=peer.Trade;
            await peer.SendAsync(GamePacketOpcode.TradeRequest3D,TradeProtocol.Request(operation,before?.Id??0,before?.Revision??0,target,peer.Inventory.Revision,mesos));
            await peer.PumpAsync(()=>peer.Trade!=before);
        }
        try
        {
            await a.ConnectAsync("test",1002); await b.ConnectAsync("test2",2001); await Task.WhenAll(a.MoveSafeAsync(),b.MoveSafeAsync());
            mesosA=a.Inventory.Mesos; mesosB=b.Inventory.Mesos;
            await Send(a,TradeOperation.Invite,target:2001); await b.PumpAsync(()=>b.Trade!=null);
            await Send(b,TradeOperation.Accept); await a.PumpAsync(()=>a.Trade.Stage==TradeStage.Editing);
            await Send(a,TradeOperation.Offer,9); await b.PumpAsync(()=>b.Trade.Revision==a.Trade.Revision); tradeId=a.Trade.Id;
            File.WriteAllText(Path.Combine(directory,"trade-ready"),tradeId.ToString());
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while(!File.Exists(Path.Combine(directory,"trade-lock-ready"))) await Task.Delay(50,timeout.Token);
            await Send(a,TradeOperation.Confirm); await b.PumpAsync(()=>b.Trade.Confirmed[0]); await Send(b,TradeOperation.Confirm);
            Program.Check(b.Trade.Stage==TradeStage.Committing,"Trade committed before both disconnects could be injected");
        }
        finally { a.Dispose(); b.Dispose(); }
        File.WriteAllText(Path.Combine(directory,"trade-disconnected"),tradeId.ToString());
        await Task.Delay(11000);
        using FieldServerIntegration.Peer recoveredA=new(),recoveredB=new();
        await recoveredA.ConnectAsync("test",1002); await recoveredB.ConnectAsync("test2",2001);
        foreach(var peer in new[]{recoveredA,recoveredB})
        {
            await peer.SendAsync(GamePacketOpcode.TradeRequest3D,TradeProtocol.Request(TradeOperation.Query,tradeId,0));
            await peer.PumpAsync(()=>peer.Trade?.Id==tradeId && peer.Trade.Stage==TradeStage.Completed);
        }
        Program.Check(recoveredA.Inventory.Mesos==mesosA-9 && recoveredB.Inventory.Mesos==mesosB+9,"Both-disconnected trade was cancelled or exchanged twice");
        Console.WriteLine("[PASS] Actual DB lock and both disconnects preserve one committed trade and reconnect queries"); return 0;
    }
}
