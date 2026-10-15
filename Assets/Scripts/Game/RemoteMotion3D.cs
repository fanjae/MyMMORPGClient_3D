using System;
using System.Collections.Generic;

public sealed class RemoteMotion3D
{
    public const double BufferSeconds = 0.2, MaxSilenceSeconds = 2;
    private readonly Queue<Motion3DAction> _actions = new();
    private Motion3DSimulation _simulation;
    private Motion3DState _state;
    private ulong _tick, _lastSequence, _lastActionTick;
    private double _referenceTick, _receivedAt;
    private short _inputX, _inputZ;
    private bool _hasRelay, _recovered;
    private AdaptivePlayback3D _playback = new();
    public double PlaybackBufferSeconds => _playback.BufferSeconds;
    public double ArrivalJitterSeconds => _playback.ArrivalJitterSeconds;
    public void SetNetworkTiming(NetworkTiming3D timing) => _playback.SetNetwork(timing.RttSeconds, timing.VariationSeconds, timing.Samples);
    public bool Ready => _simulation != null;
    public bool Snap { get; private set; }
    public Motion3DState State => _state;
    public void Configure(World3DData world, Relay3DAction snapshot, double now)
    {
        _actions.Clear(); _playback = new(); _simulation = new(world); _state = snapshot.Action.State;
        _tick = _lastActionTick = snapshot.Action.ClientTick; _lastSequence = snapshot.Action.Sequence;
        _inputX = snapshot.Action.InputX; _inputZ = snapshot.Action.InputZ;
        _referenceTick = snapshot.LatestClientTick; _receivedAt = now; _hasRelay = true; _recovered = false;
    }
    public bool Add(Relay3DAction relay, double now)
    {
        if (!Ready || relay.Action.Sequence <= _lastSequence || relay.Action.ClientTick < _lastActionTick) return false;
        _lastSequence = relay.Action.Sequence; _lastActionTick = relay.Action.ClientTick;
        bool recovered = _hasRelay && now - _receivedAt >= PredictionWindow;
        if (recovered)
        {
            // 끊기기 전의 예측 tick을 따라잡느라 오래된 물리를 재생하지 않고 새 기준점으로 복구한다.
            _actions.Clear(); _tick = relay.Action.ClientTick; _referenceTick = relay.LatestClientTick; _recovered = true;
        }
        else _referenceTick = Math.Max(SourceTick(now), relay.LatestClientTick);
        _playback.Receive(now, relay.LatestClientTick * Motion3DSimulation.FixedDelta);
        _receivedAt = now; _hasRelay = true;
        if (_actions.Count >= 128) throw new InvalidOperationException("Remote 3D action queue limit reached");
        _actions.Enqueue(relay.Action); return true;
    }
    public bool Sample(double now, out Motion3DState state)
    {
        state = default; if (!Ready) return false;
        Snap = false;
        _playback.Advance(now);
        double target = Math.Max(_tick, SourceTick(now) - _playback.BufferSeconds / Motion3DSimulation.FixedDelta);
        int steps = 0;
        while (true)
        {
            while (_actions.Count > 0 && _actions.Peek().ClientTick <= _tick)
            {
                var action = _actions.Dequeue(); ulong age = _tick - action.ClientTick;
                _state = action.State; _inputX = action.InputX; _inputZ = action.InputZ;
                Snap |= action.Kind == Action3DKind.Respawn || _recovered;
                // 오래 끊겼던 상태는 잘린 재실행 기록을 믿지 않고 새 기준점에서 재개한다.
                if (age > 100) { _tick = action.ClientTick; age = 0; Snap = true; }
                for (ulong i = 0; i < age; ++i) _simulation.Step(ref _state, _inputX, _inputZ, false);
                _recovered = false;
            }
            if (_tick + 1 > target || ++steps > 100) break;
            _simulation.Step(ref _state, _inputX, _inputZ, false); ++_tick;
        }
        state = _state; return true;
    }
    // 정속 이동도 1초 확인 주기를 사용하므로 그 기한 이후의 예측만 100~200ms로 제한한다.
    private double PredictionWindow => Math.Min(MaxSilenceSeconds, ClientMotion3D.CheckpointInterval + _playback.ExtraPredictionSeconds);
    private double SourceTick(double now) => _referenceTick + Math.Clamp(now - _receivedAt, 0, PredictionWindow) / Motion3DSimulation.FixedDelta;
}
