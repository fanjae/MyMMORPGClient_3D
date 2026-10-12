using System;

public struct MonsterPose3D { public double X, Y, Z, Yaw; }

// 수신 상태 사이를 보간하며 패킷이 끊기면 마지막 위치에 멈춘다.
public sealed class MonsterMotion3D
{
    public const double SnapDistance = 40; // 서버 40단위 = Unity 2m.
    private MonsterPose3D _from, _target;
    private double _received, _duration = 0.2;
    private ulong _tick;
    public bool Ready { get; private set; }
    public void Apply(FieldMonsterData state, ulong tick, double now)
    {
        if (Ready && tick != 0 && tick <= _tick) return;
        MonsterPose3D pose = new() { X = state.X, Y = state.Y, Z = state.Z, Yaw = state.Yaw / 100.0 };
        var current = Sample(now);
        double dx = current.X - pose.X, dy = current.Y - pose.Y, dz = current.Z - pose.Z;
        bool snap = !Ready || dx * dx + dy * dy + dz * dz > SnapDistance * SnapDistance;
        _from = snap ? pose : current; _target = pose;
        _duration = tick > _tick && Ready ? Math.Clamp((tick - _tick) * 0.02, 0.05, 0.4) : 0.2;
        _received = now; _tick = tick; Ready = true;
    }
    public MonsterPose3D Sample(double now)
    {
        double ratio = Math.Clamp((now - _received) / _duration, 0, 1);
        // 359도에서 1도로 바뀔 때도 짧은 방향으로 회전한다.
        double yaw = ((_target.Yaw - _from.Yaw + 540) % 360) - 180;
        return new() { X = _from.X + (_target.X - _from.X) * ratio, Y = _from.Y + (_target.Y - _from.Y) * ratio,
            Z = _from.Z + (_target.Z - _from.Z) * ratio, Yaw = (_from.Yaw + yaw * ratio + 360) % 360 };
    }
}
