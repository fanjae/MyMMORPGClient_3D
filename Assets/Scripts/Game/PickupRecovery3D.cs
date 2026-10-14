// 응답을 잃은 획득은 재지급 요청 대신 결과만 조회한다.
public sealed class PickupRecovery3D
{
    public ulong DropId { get; private set; }
    public bool Pending => DropId != 0;
    public int Queries { get; private set; }
    private double _queryAt;
    public void Begin(ulong dropId, double now) { DropId = dropId; Queries = 0; _queryAt = now + 5; }
    public void Restore(ulong dropId, double now) { Begin(dropId, now); _queryAt = now; }
    public bool TryQuery(double now)
    {
        if (!Pending || Queries >= 3 || now < _queryAt) return false;
        ++Queries; _queryAt = now + 5; return true;
    }
    // 마지막 조회의 응답 제한까지 지난 경우 수동 조회 안내로 전환한다.
    public bool NeedsManualQuery(double now) => Pending && Queries >= 3 && now >= _queryAt;
    public bool Accept(ulong dropId, PickupResult result)
    {
        if (DropId != dropId || !Pending) return false;
        if (result == PickupResult.Busy || result == PickupResult.StorageError) return false;
        DropId = 0; return true;
    }
    public void Clear() { DropId = 0; Queries = 0; }
}
