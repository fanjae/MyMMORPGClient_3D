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
        public string mode = "manual"; public double time;
        public uint mapId; public ulong generation, inventoryRevision; public int remotePlayers, monsters;
        public bool remoteJump, remoteLand; public double firstMonsterX;
        public ushort hp, mp, redPotions, bluePotions; public ulong mesos;
        public ushort channel=1, lastMonsterHp;
        public int attackEvents, hits, kills, pickupEvents; public string pickupResult;
        public ulong lastAttackSequence;
        public bool focused, inputAllowed, grounded, inventoryOpen, sceneTerrain;
        public double x, y, z; public ulong jumpId, tick; public string source = "Unity InputSystem and window key events; no scripted movement";
    }
    private readonly Evidence _evidence = new(); private bool _enabled; private double _writeAt;
    private string _lastEvent = "";
    private void Awake() { _enabled = Array.Exists(Environment.GetCommandLineArgs(), arg => arg == "--input-probe"); if (_enabled) { Directory.CreateDirectory("Logs"); InputSystem.onEvent += RawEvent; } }
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
    public void RecordChannel(ushort channel) { if (_enabled) _evidence.channel=channel; }
    public void RecordAttack(BasicAttackData data,bool local)
    {
        if (!_enabled || !local) return;
        ++_evidence.attackEvents; _evidence.lastAttackSequence=data.Sequence; _evidence.lastMonsterHp=data.Hp;
        if (data.Result==BasicAttackResult.Hit) ++_evidence.hits;
        if (data.Result==BasicAttackResult.Killed) ++_evidence.kills;
    }
    public void RecordPickup(PickupResult result) { if (_enabled) { ++_evidence.pickupEvents; _evidence.pickupResult=result.ToString(); } }
    // 실제 입력 기록에 접속·인벤토리·상대 행동을 함께 남겨 자동 조작 결과와 구분한다.
    public void RecordField(World3DData world, InventoryData inventory, int remotes, int monsters, bool jump, bool land, double firstX, bool automatic)
    {
        if (!_enabled) return;
        _evidence.mode = automatic ? "automatic" : "manual"; _evidence.mapId = world.MapId; _evidence.generation = world.Generation;
        _evidence.remotePlayers = remotes; _evidence.monsters = monsters; _evidence.remoteJump = jump; _evidence.remoteLand = land; _evidence.firstMonsterX = firstX;
        if (inventory != null) { _evidence.inventoryRevision = inventory.Revision; _evidence.hp = inventory.Hp; _evidence.mp = inventory.Mp;
            _evidence.mesos = inventory.Mesos; _evidence.redPotions = inventory.Entries[24].Quantity; _evidence.bluePotions = inventory.Entries[25].Quantity; }
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
        {
            _writeAt = Time.unscaledTimeAsDouble + 0.1; _evidence.time = Time.unscaledTimeAsDouble;
            File.WriteAllText("Logs/input-probe.json", JsonUtility.ToJson(_evidence, true));
            string changed = $"{_evidence.jumpId}/{_evidence.jumpPresses}/{_evidence.grounded}/{_evidence.focused}/{_evidence.inputAllowed}/{_evidence.inventoryOpen}/{_evidence.inventoryRevision}/{_evidence.generation}/{_evidence.remoteJump}/{_evidence.remoteLand}";
            // 상태 전환만 이력에 추가하고 프레임별 기록은 최신 파일 하나로 유지한다.
            if (changed != _lastEvent) { _lastEvent = changed; File.AppendAllText("Logs/input-probe-events.jsonl", JsonUtility.ToJson(_evidence) + "\n"); }
        }
    }
    private void OnApplicationFocus(bool focused) { if (_enabled && !focused) ++_evidence.focusLosses; }
}
