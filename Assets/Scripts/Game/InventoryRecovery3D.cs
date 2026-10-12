using System;

// 응답을 잃은 조작은 재실행하지 않고 조회만 제한된 횟수로 다시 요청한다.
public sealed class InventoryRecovery3D
{
    public bool Busy { get; private set; }
    public bool RequiresRefresh { get; private set; } = true;
    public int Retries { get; private set; }
    private double _deadline, _retryAt = double.PositiveInfinity;
    public bool Begin(InventoryOperation operation, double now)
    {
        if (Busy || (operation != InventoryOperation.Refresh && RequiresRefresh)) return false;
        Busy = true; _deadline = now + 5; return true;
    }
    public void Accept(InventoryResult result, double now)
    {
        Busy = false;
        if (result == InventoryResult.StorageError || result == InventoryResult.RateLimited) { Fail(now); return; }
        RequiresRefresh = false; Retries = 0; _retryAt = double.PositiveInfinity;
    }
    public void Fail(double now)
    {
        Busy = false; RequiresRefresh = true; _retryAt = now + 0.5 * Math.Pow(2, Retries);
    }
    public bool Expire(double now)
    {
        if (!Busy || now < _deadline) return false;
        Fail(now); return true;
    }
    public bool TryRetry(double now)
    {
        if (Busy || !RequiresRefresh || Retries >= 3 || now < _retryAt) return false;
        ++Retries; _retryAt = double.PositiveInfinity; return true;
    }
    public void Reset()
    {
        Busy = false; RequiresRefresh = true; Retries = 0; _retryAt = double.PositiveInfinity;
    }
}
