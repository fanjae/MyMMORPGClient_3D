using System;
using System.Collections.Generic;

public sealed class ClientMotion3D
{
    public const double SendInterval = 0.2, CheckpointInterval = 1;
    private readonly Queue<Motion3DAction> _pending = new();
    private World3DData _world;
    private Motion3DSimulation _simulation;
    private Motion3DState _state;
    private short _inputX, _inputZ;
    private bool _changed, _jumpPending, _airborne;
    private double _accumulator, _nextSendAt, _checkpointAt;
    private ulong _sequence, _batch;
    public ulong Tick { get; private set; }
    public ulong JumpId { get; private set; }
    public bool Ready => _world != null;
    public bool CanJump => Ready && _state.Grounded && !_airborne && !_jumpPending;
    public Motion3DState State => _state;
    public int PendingActions => _pending.Count;
    public void Configure(World3DData world)
    {
        Reset(); _world = world; _simulation = new(world); _state = _simulation.Spawn(); _changed = true;
    }
    public void SetInput(int x, int z, bool jumpPressed)
    {
        if (x < -1 || x > 1 || z < -1 || z > 1) throw new ArgumentOutOfRangeException(nameof(x));
        double length = Math.Max(1, Math.Sqrt(x * x + z * z));
        short inputX = (short)Math.Round(x / length * 1000), inputZ = (short)Math.Round(z / length * 1000);
        if (inputX != _inputX || inputZ != _inputZ) { _inputX = inputX; _inputZ = inputZ; _changed = true; }
        if (jumpPressed && CanJump) _jumpPending = true;
    }
    public void StopInput() { SetInput(0, 0, false); _jumpPending = false; }
    public void Advance(double elapsed)
    {
        if (!Ready) return;
        if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0) throw new ArgumentOutOfRangeException(nameof(elapsed));
        _accumulator = Math.Min(_accumulator + elapsed, Motion3DSimulation.FixedDelta * 5);
        while (_accumulator + 1e-9 >= Motion3DSimulation.FixedDelta)
        {
            bool wasGrounded = _state.Grounded, jump = _jumpPending && _state.Grounded && !_airborne;
            _jumpPending = false;
            _simulation.Step(ref _state, _inputX, _inputZ, jump); ++Tick;
            if (jump || (wasGrounded && !_state.Grounded))
            {
                _airborne = true; ++JumpId; Enqueue(jump ? Action3DKind.Jump : Action3DKind.Fall);
            }
            else if (_airborne && _state.Grounded)
            { _airborne = false; Enqueue(_simulation.Respawned ? Action3DKind.Respawn : Action3DKind.Land); }
            else if (_changed) Enqueue(Action3DKind.Input);
            _changed = false; _accumulator = Math.Max(0, _accumulator - Motion3DSimulation.FixedDelta);
        }
    }
    // 공격 직전 위치도 기존 이동 송신 상한 안에서 서버에 먼저 전달한다.
    public ulong AttackCheckpoint() { if (!Ready) return 0; Enqueue(Action3DKind.Checkpoint); return _sequence; }
    private void Enqueue(Action3DKind kind)
    {
        if (_pending.Count >= 128) throw new InvalidOperationException("3D movement action queue limit reached");
        _pending.Enqueue(new() { Sequence = ++_sequence, ClientTick = Tick, JumpId = JumpId, State = _state, InputX = _inputX, InputZ = _inputZ, Kind = kind });
    }
    public bool TryCreateBatch(double now, out Motion3DBatch batch)
    {
        batch = null;
        if (!Ready || now + 1e-9 < _nextSendAt) return false;
        if (_pending.Count == 0) { if (now < _checkpointAt) return false; Enqueue(Action3DKind.Checkpoint); }
        batch = new() { MapId = _world.MapId, Generation = _world.Generation, BatchSequence = ++_batch, LatestClientTick = Tick };
        while (_pending.Count > 0 && batch.Actions.Count < Movement3DProtocol.MaxActions) batch.Actions.Add(_pending.Dequeue());
        _nextSendAt = now + SendInterval; _checkpointAt = now + CheckpointInterval; return true;
    }
    public void Reset()
    {
        _world = null; _simulation = null; _state = default; _pending.Clear();
        _inputX = _inputZ = 0; _changed = _jumpPending = _airborne = false;
        _sequence = _batch = Tick = JumpId = 0; _accumulator = _checkpointAt = 0;
        // 맵 변경으로 동일 연결의 200ms 송신 상한을 초기화하지 않는다.
    }
}
