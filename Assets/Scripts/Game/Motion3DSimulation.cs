using System;

// 발 위치를 기준으로 고정 20ms 단계의 XZ 이동과 Y 점프를 계산한다.
// 캡슐은 표시용이며 충돌은 양쪽 클라이언트가 공유하는 축 정렬 몸체로 처리한다.
public sealed class Motion3DSimulation
{
    public const double FixedDelta = 0.02;
    private const double Epsilon = 0.000001;
    private readonly World3DData _world;
    public bool Respawned { get; private set; }
    public Motion3DSimulation(World3DData world) { _world = world ?? throw new ArgumentNullException(nameof(world)); }
    public Motion3DState Spawn() => new() { X = _world.SpawnX, Y = _world.SpawnY, Z = _world.SpawnZ, Grounded = true, SupportId = _world.SpawnSupport() };
    public void Step(ref Motion3DState s, short inputX, short inputZ, bool jump)
    {
        Respawned = false;
        s.VelocityX = inputX / 1000.0 * _world.Speed; s.VelocityZ = inputZ / 1000.0 * _world.Speed;
        if (inputX != 0 || inputZ != 0)
            s.Yaw = (ushort)((Math.Round((Math.Atan2(inputX, inputZ) * 180 / Math.PI + 360) % 360 * 100)) % 36000);
        if (jump && s.Grounded) { s.Grounded = false; s.SupportId = 0; s.VelocityY = _world.JumpSpeed; }
        MoveHorizontal(ref s, s.VelocityX * FixedDelta, true);
        MoveHorizontal(ref s, s.VelocityZ * FixedDelta, false);
        if (s.Grounded)
        {
            s.SupportId = FindSupport(s);
            if (s.SupportId == 0) s.Grounded = false;
        }
        if (!s.Grounded)
        {
            s.VelocityY = Math.Max(-_world.MaxFallSpeed, s.VelocityY - _world.Gravity * FixedDelta);
            double target = s.Y + s.VelocityY * FixedDelta;
            uint support = 0;
            bool ascending = s.VelocityY > 0, ceiling = false;
            foreach (var b in _world.Boxes)
            {
                if (!Overlap(s.X, _world.Radius, b.MinX, b.MaxX) || !Overlap(s.Z, _world.Radius, b.MinZ, b.MaxZ)) continue;
                if (s.VelocityY < 0 && s.Y >= b.MaxY - Epsilon && target <= b.MaxY && (support == 0 || b.MaxY > target))
                { target = b.MaxY; support = b.Id; }
                else if (ascending && s.Y + _world.Height <= b.MinY + Epsilon && target + _world.Height > b.MinY)
                { target = Math.Min(target, b.MinY - _world.Height); ceiling = true; }
            }
            if (ceiling) s.VelocityY = 0;
            if (target + _world.Height > _world.MaxY) { target = _world.MaxY - _world.Height; s.VelocityY = 0; }
            s.Y = target;
            if (support != 0) { s.Grounded = true; s.SupportId = support; s.VelocityY = 0; }
        }
        if (s.Y < _world.MinY) { s = Spawn(); Respawned = true; }
    }
    private void MoveHorizontal(ref Motion3DState s, double delta, bool xAxis)
    {
        double position = xAxis ? s.X : s.Z;
        double minimum = (xAxis ? _world.MinX : _world.MinZ) + (double)_world.Radius;
        double maximum = (xAxis ? _world.MaxX : _world.MaxZ) - (double)_world.Radius;
        double target = Math.Clamp(position + delta, minimum, maximum);
        foreach (var b in _world.Boxes)
        {
            if (s.Y >= b.MaxY - Epsilon || s.Y + _world.Height <= b.MinY + Epsilon ||
                !(xAxis ? Overlap(s.Z, _world.Radius, b.MinZ, b.MaxZ) : Overlap(s.X, _world.Radius, b.MinX, b.MaxX))) continue;
            double low = (xAxis ? b.MinX : b.MinZ) - (double)_world.Radius;
            double high = (xAxis ? b.MaxX : b.MaxZ) + (double)_world.Radius;
            if (delta > 0 && position <= low + Epsilon && target > low) target = Math.Min(target, low);
            if (delta < 0 && position >= high - Epsilon && target < high) target = Math.Max(target, high);
        }
        if (Math.Abs(target - position - delta) > Epsilon)
        { if (xAxis) s.VelocityX = 0; else s.VelocityZ = 0; }
        if (xAxis) s.X = target; else s.Z = target;
    }
    private uint FindSupport(Motion3DState s)
    {
        foreach (var b in _world.Boxes)
            if (Math.Abs(s.Y - b.MaxY) < Epsilon && Overlap(s.X, _world.Radius, b.MinX, b.MaxX) && Overlap(s.Z, _world.Radius, b.MinZ, b.MaxZ)) return b.Id;
        return 0;
    }
    private static bool Overlap(double center, double radius, double min, double max) => center + radius > min + Epsilon && center - radius < max - Epsilon;
}
