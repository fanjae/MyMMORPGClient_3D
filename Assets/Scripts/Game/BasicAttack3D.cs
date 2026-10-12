// 이동 확인이 포함된 묶음의 송신이 끝난 뒤에만 기본 공격 요청을 보낸다.
public sealed class BasicAttack3D
{
    private ulong _checkpoint, _sequence;
    private double _nextAt;
    public bool Queue(bool allowed, double now, ClientMotion3D motion)
    {
        if (!allowed || _checkpoint != 0 || now < _nextAt || !motion.Ready) return false;
        _checkpoint = motion.AttackCheckpoint(); _nextAt = now + 0.5; return true;
    }
    public bool Take(Motion3DBatch batch, out ulong sequence, out ulong action)
    {
        sequence = action = 0;
        if (_checkpoint == 0) return false;
        foreach (var entry in batch.Actions)
            if (entry.Sequence == _checkpoint) { sequence = ++_sequence; action = batch.Actions[batch.Actions.Count - 1].Sequence; _checkpoint = 0; return true; }
        return false;
    }
    // Map 전환·채팅·창 비활성화에서는 대기 공격만 취소하고 요청 순서는 유지한다.
    public void Cancel() { _checkpoint = 0; }
}
