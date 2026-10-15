using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

// --input-probe 실행에서 실제 키 이벤트와 이동 상태를 기록하며 입력을 생성하지 않는다.
public sealed class InputProbe3D : MonoBehaviour
{
    [Serializable] private sealed class MonsterEvidence { public uint id; public double x, y, z; public ushort hp; public ulong revision; }
    [Serializable] private sealed class AttackEvidence
    {
        public double time; public uint characterId, monsterId; public ulong sequence, revision; public string result;
        public ushort hp, damage; public bool local;
    }
    [Serializable] private sealed class Evidence
    {
        // 입력 관측 횟수는 실제 점프 횟수와 다를 수 있으므로 jumpId 변화와 함께 확인한다.
        public int movePresses, jumpPresses, acceptedJumps, blockedAirJumps, inventoryPresses, focusLosses, keyboardEvents, guiKeyDowns;
        public string lastGuiKey;
        public string mode = "manual"; public double time;
        public uint mapId; public ulong generation, inventoryRevision; public int remotePlayers, monsters;
        public bool remoteJump, remoteLand; public double firstMonsterX;
        public ushort hp, mp, redPotions, bluePotions; public ulong mesos;
        public ulong weaponInstance, hatInstance;
        public ushort channel=1, lastMonsterHp;
        public int attackEvents, hits, kills, pickupEvents; public string pickupResult;
        public ulong lastAttackSequence;
        public bool focused, inputAllowed, uiInputAllowed, grounded, inventoryOpen, sceneTerrain;
        public double x, y, z; public ushort yaw; public ulong jumpId, tick; public string source = "Unity InputSystem and window key events; no scripted movement";
        public bool combatPaused; public int drops; public MonsterEvidence[] monsterStates;
        public double rttMs, rttVariationMs, playbackBufferMs, arrivalJitterMs;
    }
    [Serializable] private sealed class TradeEvidence
    {
        public double time; public ulong id,revision,firstMesos,secondMesos;
        public uint firstCharacter,secondCharacter; public string stage,result;
        public bool firstConfirmed,secondConfirmed; public int firstItems,secondItems;
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
    public void RecordNetwork(NetworkTiming3D timing,double buffer,double jitter)
    {
        if(!_enabled) return;
        _evidence.rttMs=timing.RttSeconds*1000; _evidence.rttVariationMs=timing.VariationSeconds*1000;
        _evidence.playbackBufferMs=buffer*1000; _evidence.arrivalJitterMs=jitter*1000;
    }
    public void RecordTrade(TradeStateData state)
    {
        if(!_enabled) return;
        File.AppendAllText("Logs/trade-events.jsonl",JsonUtility.ToJson(new TradeEvidence {
            time=Time.unscaledTimeAsDouble,id=state.Id,revision=state.Revision,stage=state.Stage.ToString(),result=state.Result.ToString(),
            firstCharacter=state.Characters[0],secondCharacter=state.Characters[1],firstConfirmed=state.Confirmed[0],secondConfirmed=state.Confirmed[1],
            firstMesos=state.Offers[0].Mesos,secondMesos=state.Offers[1].Mesos,firstItems=state.Offers[0].Items.Count,secondItems=state.Offers[1].Items.Count })+"\n");
    }
    public void RecordAttack(BasicAttackData data,bool local)
    {
        if (!_enabled) return;
        // 공격자와 상대가 받은 결과를 각각 기록해 피해·사망 표시를 같은 처리 번호로 비교한다.
        File.AppendAllText("Logs/attack-events.jsonl", JsonUtility.ToJson(new AttackEvidence {
            time=Time.unscaledTimeAsDouble, characterId=data.CharacterId, monsterId=data.MonsterId, sequence=data.Sequence,
            revision=data.Revision, result=data.Result.ToString(), hp=data.Hp, damage=data.Damage, local=local }) + "\n");
        if (!local) return;
        ++_evidence.attackEvents; _evidence.lastAttackSequence=data.Sequence; _evidence.lastMonsterHp=data.Hp;
        if (data.Result==BasicAttackResult.Hit) ++_evidence.hits;
        if (data.Result==BasicAttackResult.Killed) ++_evidence.kills;
    }
    public void RecordPickup(PickupResult result) { if (_enabled) { ++_evidence.pickupEvents; _evidence.pickupResult=result.ToString(); } }
    public void RecordMonsters(FieldMonstersData data)
    {
        if (!_enabled) return;
        _evidence.monsterStates = new MonsterEvidence[data.Monsters.Count];
        for (int i=0;i<data.Monsters.Count;++i) { var state=data.Monsters[i]; _evidence.monsterStates[i]=new() { id=state.Id,x=state.X,y=state.Y,z=state.Z,hp=state.Hp,revision=state.Revision }; }
    }
    public void RecordCombat(bool paused) { if (_enabled) _evidence.combatPaused=paused; }
    public void RecordDrops(int count) { if (_enabled) _evidence.drops=count; }
    // 실제 입력 기록에 접속·인벤토리·상대 행동을 함께 남겨 자동 조작 결과와 구분한다.
    public void RecordField(World3DData world, InventoryData inventory, int remotes, int monsters, bool jump, bool land, double firstX, bool automatic)
    {
        if (!_enabled) return;
        _evidence.mode = automatic ? "automatic" : "manual"; _evidence.mapId = world.MapId; _evidence.generation = world.Generation;
        _evidence.remotePlayers = remotes; _evidence.monsters = monsters; _evidence.remoteJump = jump; _evidence.remoteLand = land; _evidence.firstMonsterX = firstX;
        if (inventory != null) { _evidence.inventoryRevision = inventory.Revision; _evidence.hp = inventory.Hp; _evidence.mp = inventory.Mp;
            _evidence.mesos = inventory.Mesos; _evidence.redPotions = _evidence.bluePotions = 0;
            // 이동·교환 뒤에도 특정 슬롯을 물약 수량으로 오인하지 않고 소비 탭 전체를 합산한다.
            for (int slot=24;slot<48;++slot) { var entry=inventory.Entries[slot]; if(entry.ItemId==101) _evidence.redPotions+=entry.Quantity; if(entry.ItemId==102) _evidence.bluePotions+=entry.Quantity; }
            _evidence.weaponInstance=inventory.Equipped[0].InstanceId; _evidence.hatInstance=inventory.Equipped[1].InstanceId; }
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
        _evidence.uiInputAllowed=Client3DController.UiInputAllowed;
        _evidence.grounded = motion.State.Grounded; _evidence.x = motion.State.X; _evidence.y = motion.State.Y; _evidence.z = motion.State.Z;
        _evidence.jumpId = motion.JumpId; _evidence.tick = motion.Tick; _evidence.yaw = motion.State.Yaw;
        // 프레임마다 파일을 쓰지 않고 100ms 간격으로 최신 상태를 덮어쓴다.
        if (Time.unscaledTimeAsDouble >= _writeAt)
        {
            _writeAt = Time.unscaledTimeAsDouble + 0.1; _evidence.time = Time.unscaledTimeAsDouble;
            File.WriteAllText("Logs/input-probe.json", JsonUtility.ToJson(_evidence, true));
            string changed = $"{_evidence.jumpId}/{_evidence.jumpPresses}/{_evidence.grounded}/{_evidence.focused}/{_evidence.inputAllowed}/{_evidence.uiInputAllowed}/{_evidence.inventoryOpen}/{_evidence.inventoryRevision}/{_evidence.generation}/{_evidence.remoteJump}/{_evidence.remoteLand}/{_evidence.attackEvents}/{_evidence.pickupEvents}/{_evidence.combatPaused}/{_evidence.drops}";
            // 상태 전환만 이력에 추가하고 프레임별 기록은 최신 파일 하나로 유지한다.
            if (changed != _lastEvent) { _lastEvent = changed; File.AppendAllText("Logs/input-probe-events.jsonl", JsonUtility.ToJson(_evidence) + "\n"); }
        }
    }
    private void OnApplicationFocus(bool focused) { if (_enabled && !focused) ++_evidence.focusLosses; }
}
