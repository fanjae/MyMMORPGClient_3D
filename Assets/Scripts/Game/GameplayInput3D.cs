// 조작이 차단된 동안 누른 키는 떼기 전까지 다시 적용하지 않는다.
public sealed class GameplayInput3D
{
    public const int Right = 1, Left = 2, Forward = 4, Back = 8, Jump = 16, Attack = 32;
    private int _blocked;
    private bool _reset;
    public int Filter(bool allowed, int held)
    {
        if (!allowed || _reset) { _blocked |= held; _reset = false; }
        _blocked &= held;
        return allowed ? held & ~_blocked : 0;
    }
    public bool CanPress(int key) => (_blocked & key) == 0;
    // Map 변경과 포커스 복귀의 첫 관측에서도 이미 눌린 키를 구분한다.
    public void Reset() { _reset = true; }
}
