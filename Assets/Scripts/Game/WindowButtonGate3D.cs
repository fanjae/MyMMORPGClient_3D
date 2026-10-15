using System.Collections.Generic;

// 한 번 누름에서 키 코드와 문자 이벤트가 함께 와도 같은 버튼을 다시 실행하지 않는다.
public sealed class WindowButtonGate3D<T>
{
    private readonly HashSet<T> _held=new();
    public bool Press(T key,bool hasKeyCode,bool deviceHeld)
    {
        if(hasKeyCode) return _held.Add(key);
        return !deviceHeld && !_held.Contains(key);
    }
    public bool Contains(T key)=>_held.Contains(key);
    public void Remove(T key)=>_held.Remove(key);
    public void Clear()=>_held.Clear();
}
