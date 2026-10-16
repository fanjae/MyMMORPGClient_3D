// 늦게 도착한 이전 거래 응답으로 현재 거래를 닫거나 완료한 거래를 다시 열지 않는다.
public static class TradeStateGate3D
{
    public static bool CanApply(TradeStateData current,TradeStateData incoming,uint character)
    {
        if(incoming==null || character==0 || incoming.Side(character)<0) return false;
        if(incoming.Id==0 || current==null) return true;
        if(current.Id!=incoming.Id)
            return current.Stage>=TradeStage.Completed || incoming.Stage<TradeStage.Completed;
        if(current.Stage>=TradeStage.Completed && incoming.Stage<TradeStage.Completed) return false;
        return incoming.Stage>=TradeStage.Completed || incoming.Revision>=current.Revision;
    }
}
