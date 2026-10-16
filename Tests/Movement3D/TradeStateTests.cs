internal static class TradeStateTests
{
    internal static void Run()
    {
        TradeStateData State(ulong id,ulong revision,TradeStage stage)=>new() { Id=id,Revision=revision,Stage=stage,Characters=new[]{1002u,2001u} };
        var editing=State(10,8,TradeStage.Editing);
        Program.Check(!TradeStateGate3D.CanApply(editing,State(10,7,TradeStage.Editing),1002),"Stale offer overwrote current confirmations");
        Program.Check(!TradeStateGate3D.CanApply(editing,State(9,20,TradeStage.Completed),1002),"Old receipt closed a different active trade");
        Program.Check(TradeStateGate3D.CanApply(editing,State(10,1,TradeStage.Completed),1002),"Durable receipt with its own revision was rejected");
        var completed=State(10,1,TradeStage.Completed);
        Program.Check(!TradeStateGate3D.CanApply(completed,State(10,9,TradeStage.Editing),1002),"Late editing state reopened a completed trade");
        Program.Check(TradeStateGate3D.CanApply(completed,State(11,1,TradeStage.Invited),1002),"Fresh invitation blocked after completion");
        Program.Check(!TradeStateGate3D.CanApply(editing,State(10,9,TradeStage.Editing),9999),"Other participant state accepted");
        Console.WriteLine("[PASS] Trade receipt recovery and late responses preserve the current transaction");
    }
}
