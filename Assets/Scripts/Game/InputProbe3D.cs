using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

// --input-probe 실행에서 실제 키 이벤트와 이동 상태를 기록하며 입력을 생성하지 않는다.
public sealed class InputProbe3D : MonoBehaviour
{
    [Serializable] private sealed class Evidence
    {
        // 입력 관측 횟수는 실제 점프 횟수와 다를 수 있으므로 jumpId 변화와 함께 확인한다.
        public int movePresses, jumpPresses, acceptedJumps, blockedAirJumps, inventoryPresses, focusLosses, keyboardEvents, guiKeyDowns;
        public string lastGuiKey;
        public bool focused, inputAllowed, grounded, inventoryOpen, sceneTerrain;
        public double x, y, z; public ulong jumpId, tick; public string source = "Unity InputSystem and window key events; no scripted movement";
    }
    private readonly Evidence _evidence = new(); private bool _enabled; private double _writeAt;
    private void Awake() { _enabled = Array.Exists(Environment.GetCommandLineArgs(), arg => arg == "--input-probe"); if (_enabled) InputSystem.onEvent += RawEvent; }
    private void RawEvent(InputEventPtr eventPointer, InputDevice device) { if (device is Keyboard) ++_evidence.keyboardEvents; }
    private void OnGUI() { if (_enabled && Event.current.type == EventType.KeyDown) { ++_evidence.guiKeyDowns; _evidence.lastGuiKey = Event.current.keyCode.ToString(); } }
    private void OnDestroy() { if (_enabled) InputSystem.onEvent -= RawEvent; }
    public void RecordWindowKey(KeyCode key, bool acceptedJump, bool blockedAirJump)
    {
        if (!_enabled) return;
        if (key == KeyCode.Space) { ++_evidence.jumpPresses; if (acceptedJump) ++_evidence.acceptedJumps; if (blockedAirJump) ++_evidence.blockedAirJumps; }
        else if (key == KeyCode.I) ++_evidence.inventoryPresses;
        else ++_evidence.movePresses;
    }
    public void Observe(Keyboard keyboard, bool allowed, bool inventoryOpen, bool sceneTerrain, ClientMotion3D motion)
    {
        if (!_enabled) return;
        if (keyboard != null)
        {
            if (keyboard.wKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame || keyboard.sKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame ||
                keyboard.upArrowKey.wasPressedThisFrame || keyboard.downArrowKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame) ++_evidence.movePresses;
            if (keyboard.spaceKey.wasPressedThisFrame)
            { ++_evidence.jumpPresses; if (allowed && motion.CanJump) ++_evidence.acceptedJumps; else if (allowed && !motion.State.Grounded) ++_evidence.blockedAirJumps; }
            if (keyboard.iKey.wasPressedThisFrame) ++_evidence.inventoryPresses;
        }
        _evidence.focused = Application.isFocused; _evidence.inputAllowed = allowed; _evidence.inventoryOpen = inventoryOpen; _evidence.sceneTerrain = sceneTerrain;
        _evidence.grounded = motion.State.Grounded; _evidence.x = motion.State.X; _evidence.y = motion.State.Y; _evidence.z = motion.State.Z;
        _evidence.jumpId = motion.JumpId; _evidence.tick = motion.Tick;
        // 프레임마다 파일을 쓰지 않고 100ms 간격으로 최신 상태를 덮어쓴다.
        if (Time.unscaledTimeAsDouble >= _writeAt)
        { _writeAt = Time.unscaledTimeAsDouble + 0.1; Directory.CreateDirectory("Logs"); File.WriteAllText("Logs/input-probe.json", JsonUtility.ToJson(_evidence, true)); }
    }
    private void OnApplicationFocus(bool focused) { if (_enabled && !focused) ++_evidence.focusLosses; }
}
