using System;

// 같은 연결의 왕복 시간만 측정한다. RTT 절반을 상대의 정확한 편도 지연으로 사용하지 않는다.
public sealed class NetworkTiming3D
{
    public double RttSeconds { get; private set; }
    public double VariationSeconds { get; private set; }
    public int Samples { get; private set; }
    private ulong _sequence, _pending;
    private double _sentAt, _nextAt;
    public bool TryBegin(double now, out ulong sequence)
    {
        sequence = 0;
        if (now < _nextAt || (_pending != 0 && now - _sentAt < 5)) return false;
        _pending = sequence = ++_sequence; _sentAt = now; _nextAt = now + 1;
        return true;
    }
    public bool Accept(ulong sequence, double receivedAt)
    {
        double sample = receivedAt - _sentAt;
        if (_pending == 0 || sequence != _pending || !double.IsFinite(sample) || sample < 0 || sample > 5) return false;
        _pending = 0;
        if (Samples == 0) { RttSeconds = sample; VariationSeconds = 0; }
        else
        {
            VariationSeconds += (Math.Abs(sample - RttSeconds) - VariationSeconds) * 0.25;
            RttSeconds += (sample - RttSeconds) * 0.125;
        }
        ++Samples; return true;
    }
    public void Reset() { RttSeconds = VariationSeconds = _sentAt = _nextAt = 0; Samples = 0; _pending = 0; }
}

// 200ms 송신 간격을 기본으로 하고 도착 간격의 흔들림에만 추가 여유를 둔다.
public sealed class AdaptivePlayback3D
{
    public double BufferSeconds { get; private set; } = 0.2;
    public double ArrivalJitterSeconds { get; private set; }
    public double ExtraPredictionSeconds { get; private set; } = 0.1;
    private double _receivedAt, _sourceAt, _updatedAt, _target = 0.2;
    private bool _received, _updated;
    public void SetNetwork(double rtt, double variation, int samples)
    {
        if (!double.IsFinite(rtt) || !double.IsFinite(variation) || rtt < 0 || variation < 0) return;
        ExtraPredictionSeconds = samples > 0 ? Math.Clamp(0.1 + rtt * 0.25, 0.1, 0.2) : 0.1;
        _target = Math.Clamp(0.2 + 2 * ArrivalJitterSeconds + (samples >= 3 ? 2 * variation : 0), 0.2, 0.4);
    }
    public void Receive(double now, double sourceSeconds)
    {
        if (_received && sourceSeconds > _sourceAt && now >= _receivedAt)
        {
            // 1초 체크포인트와 한 묶음의 여러 행동을 네트워크 지터로 오인하지 않는다.
            double error = Math.Abs((now - _receivedAt) - (sourceSeconds - _sourceAt));
            ArrivalJitterSeconds += (Math.Min(error, 0.5) - ArrivalJitterSeconds) * 0.125;
        }
        if (!_received || sourceSeconds > _sourceAt) { _receivedAt = now; _sourceAt = sourceSeconds; _received = true; }
    }
    public void Advance(double now)
    {
        if (!_updated) { _updated = true; _updatedAt = now; return; }
        double elapsed = Math.Clamp(now - _updatedAt, 0, 0.5); _updatedAt = now;
        double difference = _target - BufferSeconds;
        BufferSeconds += Math.Clamp(difference, -0.02 * elapsed, 0.5 * elapsed);
    }
}
