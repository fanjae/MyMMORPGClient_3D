using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

public sealed class Client3DController : MonoBehaviour
{
    private NetworkManager3D _network;
    private InventoryWindow _inventory;
    private FieldVitalsHUD _hud;
    private readonly BasicAttack3D _attack = new();
    private bool _windowAttack;
    private InputProbe3D _inputProbe;
    private readonly Dictionary<uint, Monster3DView> _monsters = new();
    private ulong _monsterTick;
    private bool _hasMonsterTick;
    private int _monsterUpdates;
    private double _firstMonsterX;
    private bool _monsterMoved;
    // 화면 갱신 사이의 짧은 입력도 다음 고정 이동 단계까지 보존한다.
    private int _tapX, _tapZ;
    private readonly GameplayInput3D _inputGate = new();
    private readonly HashSet<uint> _presentMonsters = new();
    private readonly List<uint> _missingMonsters = new();
    private readonly HashSet<KeyCode> _windowKeys = new();
    private readonly HashSet<KeyCode> _windowButtons = new();
    private bool _windowJump, _windowInventory, _windowEscape, _clearGuiFocus;
    private int _windowTapX, _windowTapZ;
    private readonly ClientMotion3D _motion = new();
    private readonly Dictionary<uint, Player3DView> _players = new();
    private readonly List<GameObject> _terrain = new();
    private readonly List<string> _chatLines = new();
    private World3DData _world;
    private SceneWorld3D _sceneWorld;
    private bool _usingSceneTerrain;
    private Player3DView _local;
    private uint _localId;
    private bool _offline, _busy, _sending, _changingMap, _failed, _sendingChat;
    private int _attempt;
    private double _deadline;
    private string _host = "127.0.0.1", _port = "7776", _login = "test", _password = "", _chat = "", _map = "100000003";
    private string _status = "Login or choose Offline preview.";
    private CharacterInfo[] _characters = Array.Empty<CharacterInfo>();
    private Vector2 _panelScroll, _chatScroll;
    private string _connectedHost;
    private bool _smoke, _smokeOnline, _smokeFinishing, _remoteJump, _remoteLand, _smokePeerReady;
    private bool _smokeFull, _smokeLogin, _smokeCharacters, _smokeTicket, _smokeEntered;
    private uint _smokeCharacterId;
    private double _smokeStarted;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindFirstObjectByType<Client3DController>() != null) return;
        GameObject root = new("MyMMORPG 3D Client");
        root.AddComponent<NetworkManager3D>(); root.AddComponent<Client3DController>();
    }
    private void Awake()
    {
        Application.runInBackground = true; Application.targetFrameRate = 60;
        _network = GetComponent<NetworkManager3D>();
        _inventory = FindFirstObjectByType<InventoryWindow>();
        _inputProbe = GetComponent<InputProbe3D>();
        _hud = FindFirstObjectByType<FieldVitalsHUD>(FindObjectsInactive.Include);
        if (_hud != null) _hud.AttackRequested += RequestAttack;
        _network.AttackReceived += AttackReceived;
        if (_inventory != null) _inventory.Requested += InventoryRequested;
        _network.InventoryReceived += data => { if (_inventory != null) _inventory.Apply(data); if (_hud != null) _hud.Apply(_inventory?.Data); };
        _network.FieldMonstersReceived += MonstersReceived;
        _sceneWorld = FindFirstObjectByType<SceneWorld3D>();
        if (_sceneWorld != null) _sceneWorld.SetVisible(false);
        _network.LoginCompleted += LoginCompleted;
        _network.CharacterListReceived += CharactersReceived;
        _network.CharacterSelected += CharacterSelected;
        _network.EnterGameReceived += Entered;
        _network.GeometryReceived += WorldReceived;
        _network.MovementReceived += SnapshotReceived;
        _network.MovementActionsReceived += ActionsReceived;
        _network.PlayerEntered += data => { if (_world != null && data.CharacterId != _localId) CreateRemote(data.CharacterId); };
        _network.PlayerLeft += id => { if (_players.Remove(id, out var player)) Destroy(player.gameObject); };
        _network.MapChanged += data =>
        {
            _changingMap = false; _busy = false; _status = $"Map change: {data.Result}";
            if (data.Result == ChangeMapResult.Success) { ClearWorld(); _chatLines.Clear(); }
        };
        _network.GameDisconnected += Disconnected;
        _network.LoginDisconnected += () => { if (_localId == 0) { _busy = false; _status = "Login connection closed."; } };
        _network.PlayerChatReceived += data => AddChat($"{data.CharacterId}: {data.Message}");
        _network.WhisperReceived += data => AddChat($"Whisper {data.SenderName} → {data.TargetName}: {data.Message}");
        _network.ChatRejected += data => { _status = $"Chat: {data.Result}"; AddChat(_status); };
    }
    private async void Start()
    {
        var arguments = Environment.GetCommandLineArgs();
        _smokeFull = Array.Exists(arguments, arg => arg == "--smoke-full");
        _smoke = _smokeFull || Array.Exists(arguments, arg => arg == "--smoke-test" || arg == "--smoke-network");
        _smokeOnline = _smokeFull || Array.Exists(arguments, arg => arg == "--smoke-network");
        if (_smokeFull || Array.Exists(arguments, arg => arg == "--manual-full"))
        {
            // 전체 연결 검증 계정은 실행 인자가 아닌 해당 테스트 프로세스의 환경 변수로 전달한다.
            _login = Environment.GetEnvironmentVariable("MMORPG_TEST_LOGIN");
            _password = Environment.GetEnvironmentVariable("MMORPG_TEST_PASSWORD");
            if (string.IsNullOrEmpty(_login) || string.IsNullOrEmpty(_password) ||
                !uint.TryParse(Environment.GetEnvironmentVariable("MMORPG_TEST_CHARACTER"), out _smokeCharacterId))
            { Debug.LogError("3D_FULL_TEST: Missing test account environment."); Application.Quit(1); return; }
            Login();
            return;
        }
        if (_smoke && !_smokeOnline) StartPreview();
        // loopback fixture 연결은 명시한 실행 인자에서만 사용한다. 실서버 인증 흐름과 구분한다.
        foreach (string argument in arguments)
        {
            if (argument == "--offline-preview") StartPreview();
            if (argument.StartsWith("--relay-fixture-port=", StringComparison.Ordinal))
            {
                if (!ushort.TryParse(argument.Substring("--relay-fixture-port=".Length), out ushort port) || port == 0) { _status = "Invalid fixture port."; return; }
                try { await _network.ConnectGameServerAsync("127.0.0.1", port, 0); }
                catch (Exception e) { _status = e.Message; }
            }
        }
    }
    private void Update()
    {
        if (_busy && Time.unscaledTimeAsDouble >= _deadline)
        { ++_attempt; _network.DisconnectAll(); _busy = false; _changingMap = false; _characters = Array.Empty<CharacterInfo>(); _status = "Server response timed out."; }
        if (!_motion.Ready || _local == null || _failed) return;
        // 오프라인 미리보기에서만 순찰을 직접 계산하고 온라인은 서버 상태를 사용한다.
        if (_offline)
            foreach (var monster in _monsters.Values)
            {
                var state = monster.State;
                if (state.Hp == 0) { if (!monster.PreviewRespawnReady) continue; state.Hp = state.MaxHp; ++state.Revision; }
                state.X = -160 + (state.Id - 1) * 25 + Math.Sin((Time.unscaledTimeAsDouble - _smokeStarted) * 0.55 + state.Id) * 35;
                state.Yaw = (ushort)(Math.Cos((Time.unscaledTimeAsDouble - _smokeStarted) * 0.55 + state.Id) > 0 ? 9000 : 27000); monster.Apply(state);
            }
        Keyboard k = Keyboard.current;
        if (_windowEscape || (k != null && k.escapeKey.wasPressedThisFrame)) { _windowEscape = false; _clearGuiFocus = true; if (_inventory != null) _inventory.SetVisible(false); }
        if ((_windowInventory || (k != null && k.iKey.wasPressedThisFrame)) && GUIUtility.keyboardControl == 0 && _inventory != null)
        { _inventory.SetVisible(!_inventory.IsOpen); }
        _windowInventory = false;
        // 채팅·인벤토리·Map 변경·창 비활성화 중에는 캐릭터 조작을 멈춘다.
        bool allowed = Application.isFocused && GUIUtility.keyboardControl == 0 && !_changingMap && k != null && (_inventory == null || !_inventory.IsOpen);
        int held = 0;
        if (k != null)
        {
            if (k.dKey.isPressed || k.rightArrowKey.isPressed || _windowKeys.Contains(KeyCode.D) || _windowKeys.Contains(KeyCode.RightArrow)) held |= GameplayInput3D.Right;
            if (k.aKey.isPressed || k.leftArrowKey.isPressed || _windowKeys.Contains(KeyCode.A) || _windowKeys.Contains(KeyCode.LeftArrow)) held |= GameplayInput3D.Left;
            if (k.wKey.isPressed || k.upArrowKey.isPressed || _windowKeys.Contains(KeyCode.W) || _windowKeys.Contains(KeyCode.UpArrow)) held |= GameplayInput3D.Forward;
            if (k.sKey.isPressed || k.downArrowKey.isPressed || _windowKeys.Contains(KeyCode.S) || _windowKeys.Contains(KeyCode.DownArrow)) held |= GameplayInput3D.Back;
            if (k.spaceKey.isPressed || _windowButtons.Contains(KeyCode.Space)) held |= GameplayInput3D.Jump;
        }
        if (k != null && (k.jKey.isPressed || k.leftCtrlKey.isPressed || _windowButtons.Contains(KeyCode.J) || _windowButtons.Contains(KeyCode.LeftControl))) held |= GameplayInput3D.Attack;
        int active = _inputGate.Filter(allowed, held);
        if (allowed && _inputGate.CanPress(GameplayInput3D.Attack) && (_windowAttack || k.jKey.wasPressedThisFrame || k.leftCtrlKey.wasPressedThisFrame)) RequestAttack();
        _windowAttack = false;
        if (!allowed) _attack.Cancel();
        if (_inputProbe != null)
        {
            double firstX = _monsters.TryGetValue(1, out var first) ? first.State.X : 0;
            _inputProbe.RecordField(_world, _inventory?.Data, _players.Count, _monsters.Count, _remoteJump, _remoteLand, firstX, _smoke);
            _inputProbe.Observe(k, allowed, _inventory != null && _inventory.IsOpen, _usingSceneTerrain, _motion);
        }
        if (_smoke)
        {
            if (_smokeOnline && !_smokePeerReady)
            {
                if (_players.Count == 0) { _motion.StopInput(); return; }
                _smokePeerReady = true;
            }
            double age = _motion.Tick * Motion3DSimulation.FixedDelta;
            _motion.SetInput(age < 0.7 ? -1 : 0, age < 0.7 ? 1 : 0, _motion.Tick == 0);
            if (age > (_smokeFull ? 5 : 3) && !_smokeFinishing) { _smokeFinishing = true; StartCoroutine(FinishSmoke()); }
        }
        else if (allowed)
        {
            int x = ((active & GameplayInput3D.Right) != 0 ? 1 : 0) - ((active & GameplayInput3D.Left) != 0 ? 1 : 0);
            int z = ((active & GameplayInput3D.Forward) != 0 ? 1 : 0) - ((active & GameplayInput3D.Back) != 0 ? 1 : 0);
            // 한 프레임 안에 눌렀다 뗀 입력도 최소 한 번의 고정 이동 단계에 반영한다.
            int tapX = ((k.dKey.wasPressedThisFrame || k.rightArrowKey.wasPressedThisFrame) ? 1 : 0) - ((k.aKey.wasPressedThisFrame || k.leftArrowKey.wasPressedThisFrame) ? 1 : 0);
            int tapZ = ((k.wKey.wasPressedThisFrame || k.upArrowKey.wasPressedThisFrame) ? 1 : 0) - ((k.sKey.wasPressedThisFrame || k.downArrowKey.wasPressedThisFrame) ? 1 : 0);
            if (tapX != 0) _tapX = tapX; else if (_windowTapX != 0) _tapX = _windowTapX;
            if (tapZ != 0) _tapZ = tapZ; else if (_windowTapZ != 0) _tapZ = _windowTapZ;
            _windowTapX = _windowTapZ = 0;
            if (x == 0 && _inputGate.CanPress(_tapX > 0 ? GameplayInput3D.Right : GameplayInput3D.Left)) x = _tapX;
            if (z == 0 && _inputGate.CanPress(_tapZ > 0 ? GameplayInput3D.Forward : GameplayInput3D.Back)) z = _tapZ;
            _motion.SetInput(x, z, (k.spaceKey.wasPressedThisFrame || _windowJump) && _inputGate.CanPress(GameplayInput3D.Jump)); _windowJump = false;
            if (_offline && k.rKey.wasPressedThisFrame) _motion.Configure(_world);
        }
        else { ClearWindowInput(false); _motion.StopInput(); }
        try
        {
            ulong beforeTick = _motion.Tick; _motion.Advance(Time.unscaledDeltaTime);
            if (_motion.Tick != beforeTick) _tapX = _tapZ = 0;
            if (!_sending && _motion.TryCreateBatch(Time.unscaledTimeAsDouble, out var batch))
            { if (!_offline) SendMovement(batch, _world); }
            _local.SetLocal(_motion.State);
        }
        catch (Exception e) { _failed = true; _status = e.Message; Debug.LogException(e); }
    }
    private void LateUpdate()
    {
        if (_local == null || Camera.main == null) return;
        Vector3 feet = _local.transform.position;
        Camera.main.transform.position = feet + new Vector3(9, 9, -12);
        Camera.main.transform.LookAt(feet + Vector3.up);
    }
    private async void SendMovement(Motion3DBatch batch, World3DData world)
    {
        _sending = true;
        bool attack = _attack.Take(batch, out ulong attackSequence, out ulong actionSequence);
        try
        {
            await _network.SendMovementActionsAsync(batch);
            if (attack && ReferenceEquals(_world, world))
                await _network.SendAttackAsync(world.MapId, world.Generation, attackSequence, actionSequence);
        }
        catch (Exception e) { if (ReferenceEquals(_world, world)) { _failed = true; _status = e.Message; } }
        finally { if (ReferenceEquals(_world, world)) _sending = false; }
    }
    // 포커스를 잃으면 눌린 키와 대기 입력을 비워 복귀 후 이동·점프가 남지 않도록 한다.
    private void OnApplicationFocus(bool focused) { if (!focused) { _attack.Cancel(); _inputGate.Reset(); ClearWindowInput(); _windowButtons.Clear(); _motion.StopInput(); } }
    private void ClearWindowInput(bool releaseKeys = true) { if (releaseKeys) _windowKeys.Clear(); _tapX = _tapZ = _windowTapX = _windowTapZ = 0; _windowJump = _windowInventory = _windowEscape = _windowAttack = false; }
    private void ReadWindowKeyEvent(Event input)
    {
        // InputSystem에 상태가 잡히지 않는 키보드 입력은 실제 창 이벤트로 보완한다.
        // 일반 키보드 경로와 함께 사용하되 고정 이동 단계가 실행될 때까지 입력을 보존한다.
        if (_smoke || !Application.isFocused || !_motion.Ready) return;
        if (input.type == EventType.KeyUp) { _windowKeys.Remove(input.keyCode); _windowButtons.Remove(input.keyCode); return; }
        if (input.type != EventType.KeyDown) return;
        // 채팅 입력 중에도 Esc는 포커스 해제로 처리하고 일반 문자는 게임 조작에 쓰지 않는다.
        if (GUIUtility.keyboardControl != 0 && input.keyCode != KeyCode.Escape) return;
        Keyboard keyboard = Keyboard.current;
        KeyCode key = input.keyCode;
        // 키 코드 없이 문자만 전달된 창 이벤트도 이동·점프·인벤토리 입력으로 읽는다.
        if (key == KeyCode.None)
            key = char.ToLowerInvariant(input.character) switch { 'w' => KeyCode.W, 'a' => KeyCode.A, 's' => KeyCode.S, 'd' => KeyCode.D, 'i' => KeyCode.I, 'j' => KeyCode.J, ' ' => KeyCode.Space, _ => KeyCode.None };
        if (input.keyCode != KeyCode.None && (key == KeyCode.Space || key == KeyCode.I || key == KeyCode.Escape || key == KeyCode.J || key == KeyCode.LeftControl) && !_windowButtons.Add(key))
        { input.Use(); return; } // 키를 누른 동안 발생하는 OS 반복 이벤트는 새 조작으로 처리하지 않는다.
        bool handled = true;
        // InputSystem에서 이미 읽은 버튼 입력은 창 이벤트에서 중복 처리하지 않는다.
        if (key == KeyCode.I) { if (keyboard == null || (!keyboard.iKey.wasPressedThisFrame && !keyboard.iKey.isPressed)) { _windowInventory = true; _inputProbe?.RecordWindowKey(key, false, false); } }
        else if (key == KeyCode.Escape) { if (keyboard == null || (!keyboard.escapeKey.wasPressedThisFrame && !keyboard.escapeKey.isPressed)) _windowEscape = true; }
        else if (_inventory != null && _inventory.IsOpen) handled = false;
        else if (key == KeyCode.J || key == KeyCode.LeftControl)
        { if (keyboard == null || !(keyboard.jKey.wasPressedThisFrame || keyboard.leftCtrlKey.wasPressedThisFrame)) _windowAttack = true; }
        else if (key == KeyCode.Space)
        {
            if (keyboard == null || (!keyboard.spaceKey.wasPressedThisFrame && !keyboard.spaceKey.isPressed))
            { _windowJump = true; _inputProbe?.RecordWindowKey(key, _motion.CanJump, !_motion.State.Grounded); }
        }
        else if (key == KeyCode.W || key == KeyCode.UpArrow || key == KeyCode.S || key == KeyCode.DownArrow || key == KeyCode.A || key == KeyCode.LeftArrow || key == KeyCode.D || key == KeyCode.RightArrow)
        {
            if (input.keyCode != KeyCode.None) _windowKeys.Add(key);
            if (key == KeyCode.W || key == KeyCode.UpArrow) _windowTapZ = 1;
            if (key == KeyCode.S || key == KeyCode.DownArrow) _windowTapZ = -1;
            if (key == KeyCode.A || key == KeyCode.LeftArrow) _windowTapX = -1;
            if (key == KeyCode.D || key == KeyCode.RightArrow) _windowTapX = 1;
            _inputProbe?.RecordWindowKey(key, false, false);
        }
        else handled = false;
        if (handled) input.Use();
    }
    private void Entered(EnterGameData data)
    {
        _busy = false; _characters = Array.Empty<CharacterInfo>();
        if (data.Result != EnterGameResult.Success) { _status = $"Enter: {data.Result}"; return; }
        _offline = false; _localId = data.CharacterId; _status = $"Entered as {data.Name} ({data.CharacterId}).";
        if (_inventory != null) _inventory.RefreshFromServer();
        if (_smokeFull) { _smokeEntered = data.CharacterId == _smokeCharacterId; Debug.Log($"3D_FULL_TEST: EnterGame Success character={data.CharacterId} protocol={GameProtocol.ProtocolVersion}"); }
    }
    private void WorldReceived(World3DData world)
    {
        ApplyWorld(world, false);
    }
    private void ApplyWorld(World3DData world, bool useSceneTerrain)
    {
        ClearWorld(); _world = world; _motion.Configure(world); _failed = false;
        _usingSceneTerrain = useSceneTerrain;
        if (_hud != null) { _hud.SetVisible(true); _hud.Apply(_inventory?.Data); }
        if (_usingSceneTerrain) _sceneWorld.SetVisible(true);
        else foreach (var box in world.Boxes)
        {
            GameObject terrain = GameObject.CreatePrimitive(PrimitiveType.Cube); terrain.name = $"World box {box.Id}";
            terrain.transform.position = new Vector3((box.MinX + box.MaxX) * 0.025f, (box.MinY + box.MaxY) * 0.025f, (box.MinZ + box.MaxZ) * 0.025f);
            terrain.transform.localScale = new Vector3((box.MaxX - box.MinX) * 0.05f, (box.MaxY - box.MinY) * 0.05f, (box.MaxZ - box.MinZ) * 0.05f);
            var material = Player3DView.CreateMaterial(box.MaxY == 0 ? new Color(0.25f, 0.4f, 0.3f) : new Color(0.4f, 0.5f, 0.65f));
            terrain.GetComponent<Renderer>().material = material; _terrain.Add(terrain);
        }
        GameObject playerObject = new($"Local {_localId}"); _local = playerObject.AddComponent<Player3DView>(); _local.Initialize(_localId, world, true);
        _smokeStarted = Time.unscaledTimeAsDouble;
    }
    private void SnapshotReceived(Snapshot3DData snapshot)
    {
        if (_world == null || snapshot.MapId != _world.MapId || snapshot.Generation != _world.Generation) return;
        if (snapshot.State.CharacterId != _localId)
        {
            _remoteJump |= !snapshot.State.Action.State.Grounded && snapshot.State.Action.JumpId > 0;
            CreateRemote(snapshot.State.CharacterId).ConfigureRemote(_world, snapshot.State, Time.unscaledTimeAsDouble);
        }
    }
    private Player3DView CreateRemote(uint id)
    {
        if (!_players.TryGetValue(id, out var player))
        {
            GameObject remote = new($"Remote {id}"); player = remote.AddComponent<Player3DView>(); player.Initialize(id, _world, false); _players.Add(id, player);
        }
        return player;
    }
    private void ActionsReceived(Motion3DBroadcast batch)
    {
        if (_world == null || batch.MapId != _world.MapId || batch.Generation != _world.Generation) return;
        foreach (var relay in batch.Actions)
            if (relay.CharacterId != _localId && _players.TryGetValue(relay.CharacterId, out var player))
            {
                _remoteJump |= relay.Action.Kind == Action3DKind.Jump; _remoteLand |= relay.Action.Kind == Action3DKind.Land;
                player.Add(relay, Time.unscaledTimeAsDouble);
            }
    }
    private void StartPreview()
    {
        ++_attempt; _network.DisconnectAll(); _busy = false; _changingMap = false; _characters = Array.Empty<CharacterInfo>();
        _offline = true; _localId = 1001;
        try
        {
            ApplyWorld(_sceneWorld != null ? _sceneWorld.ReadWorld() : World3DData.Preview(), _sceneWorld != null);
            if (_inventory != null) { _inventory.Clear(); _inventory.Apply(ItemCatalog.Preview()); if (_hud != null) _hud.Apply(_inventory.Data); }
            for (uint i = 1; i <= 3; ++i) CreateMonster(i, (byte)(i - 1)).Apply(new() { Id = i, Kind = (byte)(i - 1), X = -160 + (i - 1) * 25, Z = -100 + (i - 1) * 55, Hp = 60, MaxHp = 60, Revision = 1 });
            _status = _usingSceneTerrain ? "Placed scene preview: R resets position." : "Offline preview: R resets position.";
        }
        catch (Exception e) { ClearWorld(); _localId = 0; _status = e.Message; Debug.LogException(e); }
        GUI.FocusControl(null);
    }
    private void Disconnected()
    {
        ClearWorld(); _localId = 0; _offline = false; _busy = _changingMap = false;
        if (_inventory != null) _inventory.Clear();
        _characters = Array.Empty<CharacterInfo>(); _status = "Game connection closed.";
    }
    private void ClearWorld()
    {
        _world = null; _motion.Reset(); _tapX = _tapZ = 0; _sending = false; _failed = false;
        _attack.Cancel(); if (_hud != null) _hud.SetVisible(false);
        _inputGate.Reset(); ClearWindowInput();
        _windowButtons.Clear();
        _hasMonsterTick = false; _monsterTick = 0; _monsterUpdates = 0; _monsterMoved = false;
        foreach (var monster in _monsters.Values) if (monster != null) Destroy(monster.gameObject); _monsters.Clear();
        // 인벤토리는 캐릭터 소유 상태이므로 같은 연결의 Map 변경에서도 유지한다.
        _usingSceneTerrain = false;
        if (_sceneWorld != null) _sceneWorld.SetVisible(false);
        foreach (var player in _players.Values) if (player != null) Destroy(player.gameObject); _players.Clear();
        if (_local != null) Destroy(_local.gameObject); _local = null;
        foreach (var terrain in _terrain)
        {
            if (terrain == null) continue;
            var renderer = terrain.GetComponent<Renderer>(); if (renderer != null) Destroy(renderer.sharedMaterial);
            Destroy(terrain);
        }
        _terrain.Clear();
    }
    private async void Login()
    {
        if (!int.TryParse(_port, out int port) || port < 1 || port > 65535) { _status = "Invalid login port."; return; }
        ++_attempt; int attempt = _attempt; _network.DisconnectAll(); ClearWorld(); _localId = 0; _offline = false;
        _characters = Array.Empty<CharacterInfo>(); _busy = true; _deadline = Time.unscaledTimeAsDouble + 10; _connectedHost = _host.Trim();
        if (_inventory != null) _inventory.Clear();
        try { await _network.ConnectLoginServerAsync(_connectedHost, port); if (attempt == _attempt) await _network.SendLoginAsync(_login, _password); }
        catch (Exception e) { if (attempt == _attempt) { _busy = false; _status = ConnectionError.Describe(e, _connectedHost, port, "LoginServer"); } }
    }
    private async void LoginCompleted(LoginResult result)
    {
        _status = $"Login: {result}";
        if (result != LoginResult.Success) { _busy = false; return; }
        if (_smokeFull) { _smokeLogin = true; Debug.Log("3D_FULL_TEST: Login Success"); }
        try { await _network.RequestCharacterListAsync(); }
        catch (Exception e) { _busy = false; _status = e.Message; }
    }
    private void CharactersReceived(CharacterListData data)
    {
        _busy = false; _characters = data.Result == CharacterListResult.Success ? data.Characters : Array.Empty<CharacterInfo>();
        _status = $"Characters: {data.Result}";
        if (!_smokeFull && !Array.Exists(Environment.GetCommandLineArgs(), arg => arg == "--manual-full")) return;
        _smokeCharacters = Array.Exists(_characters, character => character.CharacterId == _smokeCharacterId);
        if (!_smokeCharacters) { Debug.LogError("3D_FULL_TEST: Test character missing."); Application.Quit(1); return; }
        Debug.Log("3D_FULL_TEST: CharacterList Success"); SelectCharacter(_smokeCharacterId);
    }
    private async void SelectCharacter(uint id)
    {
        _busy = true; _deadline = Time.unscaledTimeAsDouble + 10;
        try { await _network.SelectCharacterAsync(id); }
        catch (Exception e) { _busy = false; _status = e.Message; }
    }
    private async void CharacterSelected(CharacterSelectData ticket)
    {
        if (ticket.Result != CharacterSelectResult.Success) { _busy = false; _status = $"Select: {ticket.Result}"; return; }
        if (_smokeFull) { _smokeTicket = ticket.AuthKey != 0; Debug.Log("3D_FULL_TEST: CharacterSelect Success"); }
        try { await _network.ConnectGameServerAsync(_connectedHost, ticket.GameServerPort, ticket.AuthKey); }
        catch (Exception e) { _busy = false; _status = e.Message; }
    }
    private async void ChangeMap()
    {
        if (!uint.TryParse(_map, out uint map)) { _status = "Invalid Map ID."; return; }
        _changingMap = _busy = true; _deadline = Time.unscaledTimeAsDouble + 10; _motion.StopInput();
        try { await _network.ChangeMapAsync(map); }
        catch (Exception e) { _changingMap = _busy = false; _status = e.Message; }
    }
    private async void SendChat()
    {
        if (_sendingChat) return;
        if (!ChatCommand.TryParse(_chat, out var command, out string error)) { _status = error; return; }
        _sendingChat = true; string text = _chat; int attempt = _attempt;
        try
        {
            if (command.Operation == ChatOperation.Whisper) await _network.SendWhisperAsync(command.TargetCharacterId, command.Message);
            else await _network.SendChatAsync(command.Message);
            if (attempt == _attempt && _chat == text) _chat = "";
        }
        catch (Exception e) { if (attempt == _attempt) _status = e.Message; }
        finally { _sendingChat = false; }
    }
    private void AddChat(string text) { _chatLines.Add(text); if (_chatLines.Count > 100) _chatLines.RemoveAt(0); _chatScroll.y = float.MaxValue; }
    private async void InventoryRequested(InventoryOperation operation, byte category, byte source, byte target)
    {
        if (_inventory == null) return;
        if (_offline)
        {
            // 서버에 접속하지 않은 미리보기에서만 로컬 인벤토리 규칙을 실행한다.
            var preview = _inventory.Data ?? ItemCatalog.Preview();
            InventoryPreview.Apply(preview, operation, category, source, target); _inventory.Apply(preview); if (_hud != null) _hud.Apply(_inventory.Data); return;
        }
        int attempt = _attempt;
        // 온라인에서는 수량·HP·MP를 먼저 바꾸지 않고 서버가 반환한 전체 상태를 기다린다.
        try { await _network.SendInventoryAsync(operation, category, source, target, _inventory.Data?.Revision ?? 0); }
        catch (Exception e) { if (attempt == _attempt) _inventory.Fail(e.Message); }
    }
    private void RequestAttack()
    {
        bool allowed = Application.isFocused && GUIUtility.keyboardControl == 0 && _motion.Ready && !_changingMap && !_failed &&
            (_inventory == null || !_inventory.IsOpen) && _inventory?.Data?.Hp > 0;
        if (!_attack.Queue(allowed, Time.unscaledTimeAsDouble, _motion)) return;
        // 검 표시는 입력 즉시 재생하고 실제 피해와 몬스터 HP는 서버 결과를 기다린다.
        _local.PlayAttack();
        if (_offline)
        {
            _attack.Cancel();
            Monster3DView target = null; double best = 60 * 60;
            var pose = _motion.State; double radians = pose.Yaw / 100.0 * Math.PI / 180;
            foreach (var monster in _monsters.Values)
            {
                var state = monster.State; double dx = state.X - pose.X, dz = state.Z - pose.Z, distance = dx * dx + dz * dz;
                if (state.Hp == 0 || distance > best || Math.Abs(state.Y - pose.Y) > 30 ||
                    (distance > 0.00001 && (dx * Math.Sin(radians) + dz * Math.Cos(radians)) / Math.Sqrt(distance) < 0.35)) continue;
                best = distance; target = monster;
            }
            if (target != null) { var state = target.State; ushort damage = (ushort)Math.Min(20, (int)state.Hp); state.Hp -= damage; ++state.Revision;
                target.Apply(state); target.ShowDamage(damage); if (_hud != null) _hud.Report(state.Hp == 0 ? BasicAttackResult.Killed : BasicAttackResult.Hit); }
            else if (_hud != null) _hud.Report(BasicAttackResult.Miss);
        }
    }
    private void AttackReceived(BasicAttackData data)
    {
        if (_world == null || data.MapId != _world.MapId || data.Generation != _world.Generation) return;
        bool swing = data.Result <= BasicAttackResult.Killed;
        if (data.CharacterId == _localId) { if (_hud != null) _hud.Report(data.Result); }
        else if (swing && _players.TryGetValue(data.CharacterId, out var player)) player.PlayAttack();
        if (data.MonsterId != 0 && _monsters.TryGetValue(data.MonsterId, out var monster) && data.Revision >= monster.State.Revision)
        { var state = monster.State; state.Hp = data.Hp; state.MaxHp = data.MaxHp; state.Revision = data.Revision; monster.Apply(state); monster.ShowDamage(data.Damage); }
    }
    private Monster3DView CreateMonster(uint id, byte kind)
    {
        if (!_monsters.TryGetValue(id, out var monster))
        { var obj = new GameObject("Field Slime " + id); monster = obj.AddComponent<Monster3DView>(); monster.Initialize(id, kind); _monsters.Add(id, monster); }
        return monster;
    }
    private void MonstersReceived(FieldMonstersData data)
    {
        // 이전 Map·재입장 번호·이미 반영한 tick의 몬스터 상태는 버린다.
        if (_world == null || data.MapId != _world.MapId || data.Generation != _world.Generation || (_hasMonsterTick && data.Tick <= _monsterTick)) return;
        _hasMonsterTick = true; _monsterTick = data.Tick; ++_monsterUpdates;
        _presentMonsters.Clear(); _missingMonsters.Clear();
        foreach (var state in data.Monsters)
        {
            _presentMonsters.Add(state.Id); CreateMonster(state.Id, state.Kind).Apply(state, data.Tick);
            if (state.Id == 1) { if (_monsterUpdates == 1) _firstMonsterX = state.X; else _monsterMoved |= Math.Abs(state.X - _firstMonsterX) > 0.1; }
        }
        // 전체 목록에 없는 몬스터는 제거해 서버와 화면의 개체 목록을 맞춘다.
        foreach (var id in _monsters.Keys) if (!_presentMonsters.Contains(id)) _missingMonsters.Add(id);
        foreach (var id in _missingMonsters) { Destroy(_monsters[id].gameObject); _monsters.Remove(id); }
    }
    private IEnumerator FinishSmoke()
    {
        // 명시한 자동 검증 인자에서만 실행하며 실제 키 입력 검증과 구분한다.
        Directory.CreateDirectory("Logs");
        string label = _smokeFull ? "full-" + _localId : (_smokeOnline ? "online-" + _localId : "offline");
        yield return null;
        string screenshot = Path.GetFullPath("Logs/3d-smoke-" + label + ".png");
        bool rendered = false;
        try { rendered = CaptureSmokeScene(screenshot); }
        catch (Exception e) { Debug.LogException(e); }
        bool sceneSourceOk = _usingSceneTerrain ? _sceneWorld != null && _sceneWorld.TerrainRoot.gameObject.activeSelf && _terrain.Count == 0 :
            _terrain.Count == _world.Boxes.Count && (_sceneWorld == null || !_sceneWorld.TerrainRoot.gameObject.activeSelf);
        bool ok = _motion.State.Grounded && _motion.JumpId == 1 && _motion.State.X < 0 && _motion.State.Z > 0 && _world.Boxes.Count == 5 && sceneSourceOk &&
            (!_smokeOnline || (_remoteJump && _remoteLand)) && rendered &&
            (!_smokeFull || (_smokeLogin && _smokeCharacters && _smokeTicket && _smokeEntered && _inventory?.Data != null && _inventory.Data.Result == InventoryResult.Success && _monsters.Count == 3 && _monsterUpdates >= 2 && _monsterMoved));
        File.WriteAllText("Logs/3d-smoke-" + label + ".result", (ok ? "PASS" : "FAILED") +
            $"\ngrounded={_motion.State.Grounded} jumpId={_motion.JumpId} x={_motion.State.X} z={_motion.State.Z} remoteJump={_remoteJump} remoteLand={_remoteLand}" +
            $"\nlogin={_smokeLogin} characters={_smokeCharacters} ticket={_smokeTicket} entered={_smokeEntered} protocol={GameProtocol.ProtocolVersion}" +
            $"\nterrainSource={(_usingSceneTerrain ? "scene" : "server-or-fallback")} terrainSourceOk={sceneSourceOk}" +
            $"\ninventory={(_inventory?.Data != null)} inventoryRevision={_inventory?.Data?.Revision} monsters={_monsters.Count} monsterUpdates={_monsterUpdates} monsterMoved={_monsterMoved}");
        Debug.Log("3D_PLAYER_SMOKE: " + (ok ? "PASS " : "FAILED ") + label);
        Application.Quit(ok ? 0 : 1);
    }
    private static bool CaptureSmokeScene(string path)
    {
        Camera camera = Camera.main; if (camera == null) return false;
        RenderTexture target = new(1100, 720, 24, RenderTextureFormat.ARGB32);
        Texture2D pixels = new(1100, 720, TextureFormat.RGB24, false);
        RenderTexture previous = RenderTexture.active;
        try
        {
            target.Create();
            // 숨겨진 자동 검증 창에서도 화면 가시성에 의존하지 않고 실제 3D 카메라를 렌더한다.
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (!RenderPipeline.SupportsRenderRequest(camera, request)) return false;
            RenderPipeline.SubmitRenderRequest(camera, request);
            RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 1100, 720), 0, 0); pixels.Apply();
            File.WriteAllBytes(path, pixels.EncodeToPNG());
            Color32[] colors = pixels.GetPixels32(); var first = colors[0];
            for (int i = 0; i < colors.Length; i += 113)
                if (colors[i].r != first.r || colors[i].g != first.g || colors[i].b != first.b) return true;
            return false;
        }
        finally { RenderTexture.active = previous; target.Release(); Destroy(target); Destroy(pixels); }
    }
    private void OnGUI()
    {
        // IMGUI 포커스 변경은 Update가 아닌 GUI 처리 안에서 적용한다.
        if (_clearGuiFocus) { GUI.FocusControl(null); GUIUtility.keyboardControl = 0; _clearGuiFocus = false; }
        ReadWindowKeyEvent(Event.current);
        Rect panel = new(10, 10, 370, Math.Max(120, Screen.height - 295));
        Rect chatPanel = new(10, Screen.height - 275, 450, 165);
        if (Event.current.type == EventType.MouseDown && !panel.Contains(Event.current.mousePosition) && !chatPanel.Contains(Event.current.mousePosition)) GUI.FocusControl(null);
        GUILayout.BeginArea(panel, GUI.skin.box); _panelScroll = GUILayout.BeginScrollView(_panelScroll);
        GUILayout.Label($"MyMMORPG 3D · Game protocol {GameProtocol.ProtocolVersion}"); GUILayout.Label(_status);
        if (_localId == 0)
        {
            GUILayout.Label("Host / Login port"); _host = GUILayout.TextField(_host); _port = GUILayout.TextField(_port);
            GUILayout.Label("Account / Password"); _login = GUILayout.TextField(_login); _password = GUILayout.PasswordField(_password, '*');
            GUI.enabled = !_busy;
            if (_characters.Length == 0) { if (GUILayout.Button("Connect and Login")) { GUI.FocusControl(null); Login(); } }
            foreach (var character in _characters) if (GUILayout.Button($"Enter {character.Name} ({character.CharacterId})")) SelectCharacter(character.CharacterId);
            GUI.enabled = true; if (GUILayout.Button("Offline preview")) StartPreview();
        }
        else
        {
            GUILayout.Label("WASD / Arrows: move · Space: jump · J / Ctrl: attack · I: inventory");
            GUILayout.Label($"Map: {_world?.MapId} · Local: {_localId} · Remote: {_players.Count}");
            var state = _motion.State;
            GUILayout.Label($"Position: {state.X / 20:F2}, {state.Y / 20:F2}, {state.Z / 20:F2} m");
            GUILayout.Label($"Grounded: {state.Grounded} · Jump ready: {_motion.CanJump} · Actions: {_motion.PendingActions}");
            GUILayout.Label($"Jump ID: {_motion.JumpId} · Field monsters: {_monsters.Count}");
            if (GUILayout.Button("Inventory [I]")) { GUI.FocusControl(null); if (_inventory != null) _inventory.SetVisible(!_inventory.IsOpen); }
            var sent = _network.MovementSendTraffic; var received = _network.MovementReceiveTraffic; var traffic = _network.GameTraffic;
            GUILayout.Label($"Movement TX: {sent.SentPackets} / {sent.SentBytes} B"); GUILayout.Label($"Movement RX: {received.ReceivedPackets} / {received.ReceivedBytes} B");
            GUILayout.Label($"TCP pending: {traffic.QueuedSendBytes} B · Peak: {traffic.PeakQueuedSendBytes} B");
            foreach (var player in _players.Values) GUILayout.Label($"Remote {player.CharacterId}: display gap {player.LastGap:F3} m · Snaps {player.SnapCount}");
            if (!_offline)
            {
                GUILayout.Label("3D Map ID: 100000002 / 100000003"); _map = GUILayout.TextField(_map); GUI.enabled = !_changingMap;
                if (GUILayout.Button("Change Map")) { GUI.FocusControl(null); ChangeMap(); } GUI.enabled = true;
            }
            if (GUILayout.Button(_offline ? "Back to Login" : "Disconnect")) { ++_attempt; _network.DisconnectAll(); Disconnected(); }
        }
        GUILayout.EndScrollView(); GUILayout.EndArea();
        if (_localId != 0 && !_offline)
        {
            GUILayout.BeginArea(chatPanel, GUI.skin.box); GUILayout.Label("Chat · /m characterId message"); _chatScroll = GUILayout.BeginScrollView(_chatScroll, GUILayout.Height(80));
            foreach (string text in _chatLines) GUILayout.Label(text); GUILayout.EndScrollView();
            GUILayout.BeginHorizontal(); GUI.SetNextControlName("Chat3D"); _chat = GUILayout.TextField(_chat); GUI.enabled = !_sendingChat;
            if (GUILayout.Button("Send", GUILayout.Width(60))) SendChat(); GUI.enabled = true; GUILayout.EndHorizontal(); GUILayout.EndArea();
            if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "Chat3D") { Event.current.Use(); SendChat(); }
        }
    }
    private void OnDestroy() { ClearWorld(); }
}
