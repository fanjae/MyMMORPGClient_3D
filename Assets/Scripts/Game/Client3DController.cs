using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;

public sealed class Client3DController : MonoBehaviour
{
    private NetworkManager3D _network;
    private InventoryWindow _inventory;
    private TradeWindow3D _trade;
    private FieldVitalsHUD _hud;
    private FieldUI _ui;
    private ChannelData _channels;
    private double _nextUiAt;
    private readonly Dictionary<ulong,FieldDropView> _drops=new();
    private readonly HashSet<ulong> _presentDrops=new();
    private readonly List<ulong> _missingDrops=new();
    private ulong _dropRevision, _pendingPickupId;
    private readonly BasicAttack3D _pickupMotion=new();
    private readonly PickupRecovery3D _pickupRecovery = new();
    private string _pickupKey;
    private bool _combatPaused;
    private ulong _combatRevision;
    private bool _windowPickup;
    private int _loadCount;
    private int _onlineLoadCount;
    private bool _onlineLoadStarted;
    private string _loadDetail="all";
    private ushort _channelTarget;
    private bool _reviving;
    private bool TextFocused => _ui!=null && _ui.TextFocused;
    private readonly BasicAttack3D _attack = new();
    private bool _windowAttack;
    private InputSystemUIInputModule[] _inputModules;
    private bool _focusPending=true, _focusSettling;
    private int _focusFrames;
    public static bool UiInputAllowed { get; private set; }
    private bool WindowInputAllowed => Application.isFocused && !_focusPending && !_focusSettling;
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
    private readonly WindowButtonGate3D<KeyCode> _windowButtons = new();
    private bool _windowJump, _windowInventory, _windowEscape;
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
    private string _connectedHost;
    private bool _smoke, _smokeOnline, _smokeFinishing, _remoteJump, _remoteLand, _smokePeerReady;
    private bool _smokeFull, _smokeLogin, _smokeCharacters, _smokeTicket, _smokeEntered;
    private uint _smokeCharacterId;
    private ushort _testGamePort;
    private double _smokeStarted;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ConfigureWindowInput()
    {
        if (Application.platform!=RuntimePlatform.WindowsPlayer) return;
        // Windows 포커스 콜백의 장치 리셋은 피하고 일반 Update에서 입력과 UI를 함께 정리한다.
        Application.runInBackground=true;
        InputSystem.settings.backgroundBehavior=InputSettings.BackgroundBehavior.IgnoreFocus;
    }

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
        _inputModules=FindObjectsByType<InputSystemUIInputModule>(FindObjectsSortMode.None);
        _network = GetComponent<NetworkManager3D>();
        _inventory = FindFirstObjectByType<InventoryWindow>();
        _inputProbe = GetComponent<InputProbe3D>();
        _hud = FindFirstObjectByType<FieldVitalsHUD>(FindObjectsInactive.Include);
        _ui=FindFirstObjectByType<FieldUI>(FindObjectsInactive.Include);
        if(_ui!=null) { _trade=gameObject.AddComponent<TradeWindow3D>(); _trade.Initialize(_ui.GetComponentInParent<Canvas>().transform,_network,_inventory); }
        if (_ui!=null)
        {
            _ui.LoginRequested+=(host,port,account,password)=> { _host=host; _port=port; _login=account; _password=password; _ui.ClearFocus(); Login(); };
            _ui.CharacterRequested+=SelectCharacter; _ui.PreviewRequested+=StartPreview;
            _ui.InventoryRequested+=()=> { _ui.ClearFocus(); _inventory?.SetVisible(!_inventory.IsOpen); };
            _ui.MapRequested+=map=> { _map=map.ToString(); ChangeMap(); };
            _ui.ChatRequested+=text=> { _chat=text; SendChat(); };
            _ui.ChannelsRequested+=RequestChannels; _ui.ChannelRequested+=ChangeChannel; _ui.ReviveRequested+=Revive;
            _ui.PickupRequested+=RequestPickup; _ui.DisconnectRequested+=()=> { ++_attempt; _network.DisconnectAll(); Disconnected(); };
            _ui.QuitRequested+=()=> { _network.DisconnectAll(); Application.Quit(); };
        }
        _network.ChannelsReceived+=data=>
        {
            _channels=data; _ui?.ShowChannels(data);
            _inputProbe?.RecordChannel(data.Current);
            if (_channelTarget!=0 && (data.Current==_channelTarget || data.Result!=ChannelResult.Success))
            { _channelTarget=0; _busy=_changingMap=false; _status="Channel: "+data.Result; if (data.Result==ChannelResult.Success) { _chatLines.Clear(); _ui?.ShowChat(""); _ui?.CloseMenu(); } }
        };
        _network.VitalsReceived+=VitalsReceived; _network.DropsReceived+=DropsReceived;
        _network.PickupReceived += PickupReceived;
        _network.TradeReceived += state=>_inputProbe?.RecordTrade(state);
        if (_hud != null) _hud.AttackRequested += RequestAttack;
        _network.AttackReceived += AttackReceived;
        if (_inventory != null) _inventory.Requested += InventoryRequested;
        _network.InventoryReceived += data => { if (_reviving) { _reviving=_busy=false; _status="Revive: "+data.Result; } if (_inventory != null) _inventory.Apply(data); if (_hud != null) _hud.Apply(_inventory?.Data); if (_local!=null && _inventory?.Data!=null) _local.SetVitals(_inventory.Data.Hp,0); };
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
        _network.PlayerEntered += data => { _trade?.SetPeer(data.CharacterId,data.Name); if (_world != null && data.CharacterId != _localId) CreateRemote(data.CharacterId); };
        _network.PlayerLeft += id => { _trade?.RemovePeer(id); if (_players.Remove(id, out var player)) Destroy(player.gameObject); };
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
        foreach(var argument in arguments)
        {
            if(argument.StartsWith("--field-load-detail=",StringComparison.Ordinal)) _loadDetail=argument.Substring("--field-load-detail=".Length);
            if(argument.StartsWith("--field-online-load=",StringComparison.Ordinal))
            { if(!int.TryParse(argument.Substring("--field-online-load=".Length),out _onlineLoadCount) || _onlineLoadCount<1 || _onlineLoadCount>128) { Application.Quit(1); return; } }
        }
        if(_loadDetail is not ("all" or "none" or "names" or "damage" or "attack")) { Application.Quit(1); return; }
        _network.MeasureFieldReceive=_onlineLoadCount>0;
        foreach (var argument in arguments) if (argument.StartsWith("--field-load=",StringComparison.Ordinal))
        {
            if (!int.TryParse(argument.Substring("--field-load=".Length),out _loadCount) || (_loadCount!=3 && _loadCount!=32 && _loadCount!=128)) { Application.Quit(1); return; }
        }
        if (_loadCount>0) { StartPreview(); gameObject.AddComponent<FieldLoadProbe>().Initialize(_loadCount,_loadDetail); return; }
        _smokeFull = Array.Exists(arguments, arg => arg == "--smoke-full");
        _smoke = _smokeFull || Array.Exists(arguments, arg => arg == "--smoke-test" || arg == "--smoke-network");
        _smokeOnline = _smokeFull || Array.Exists(arguments, arg => arg == "--smoke-network");
        if (_smokeFull || Array.Exists(arguments, arg => arg == "--manual-full"))
        {
            string proxyPort=Environment.GetEnvironmentVariable("MMORPG_TEST_GAME_PORT");
            if (!string.IsNullOrEmpty(proxyPort) && (!ushort.TryParse(proxyPort,out _testGamePort) || _testGamePort==0))
            { Debug.LogError("Invalid test proxy port."); Application.Quit(1); return; }
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
        UpdateWindowInput();
        double maxBuffer=0.2,maxJitter=0;
        foreach (var player in _players.Values) { player.SetNetworkTiming(_network.Timing); maxBuffer=Math.Max(maxBuffer,player.PlaybackBufferSeconds); maxJitter=Math.Max(maxJitter,player.ArrivalJitterSeconds); }
        _network.PlaybackBufferSeconds=maxBuffer; _inputProbe?.RecordNetwork(_network.Timing,maxBuffer,maxJitter);
        Keyboard k = Keyboard.current;
        if (WindowInputAllowed && (_windowEscape || (k!=null && k.escapeKey.wasPressedThisFrame)))
        { _windowEscape=false; _ui?.ClearFocus(); _inventory?.SetVisible(false); _attack.Cancel(); _pickupMotion.Cancel(); _local?.StopAttack(); _ui?.ToggleMenu(); }
        if (_ui!=null && Time.unscaledTimeAsDouble>=_nextUiAt)
        { _nextUiAt=Time.unscaledTimeAsDouble+0.2; _ui.SetState(_status,_localId,_world?.MapId ?? 0,_channels?.Current ?? 1,_offline,_busy,_inventory?.Data,_characters); }
        if (_busy && Time.unscaledTimeAsDouble >= _deadline)
        { ++_attempt; _network.DisconnectAll(); _busy = false; _changingMap = false; _characters = Array.Empty<CharacterInfo>(); _status = "Server response timed out."; }
        if (!_motion.Ready || _local == null || _failed) return;
        if(_onlineLoadCount>0 && !_onlineLoadStarted && _monsters.Count>0 && _inventory?.Data!=null)
        { _onlineLoadStarted=true; gameObject.AddComponent<FieldLoadProbe>().Initialize(_onlineLoadCount,_loadDetail,_network); }
        if (!_offline && _pickupRecovery.TryQuery(Time.unscaledTimeAsDouble)) QueryPickup();
        if (!_offline && _pickupRecovery.NeedsManualQuery(Time.unscaledTimeAsDouble))
            _status="Pickup result pending. LOOT [Z] retries the result query.";
        // 오프라인 미리보기에서만 순찰을 직접 계산하고 온라인은 서버 상태를 사용한다.
        if (_offline)
            foreach (var monster in _monsters.Values)
            {
                var state = monster.State;
                if (state.Hp == 0) { if (!monster.PreviewRespawnReady) continue; state.Hp = state.MaxHp; ++state.Revision; }
                state.X = (_loadCount>0 ? -360+(state.Id-1)%16*22 : -160+(state.Id-1)*25) + Math.Sin((Time.unscaledTimeAsDouble - _smokeStarted) * 0.55 + state.Id) * (_loadCount>0 ? 4 : 35);
                state.Yaw = (ushort)(Math.Cos((Time.unscaledTimeAsDouble - _smokeStarted) * 0.55 + state.Id) > 0 ? 9000 : 27000); monster.Apply(state);
            }
        // 사망·복구 중에도 인벤토리 개폐는 허용하고 캐릭터 조작만 중단한다.
        if (WindowInputAllowed && (_windowInventory || (k != null && k.iKey.wasPressedThisFrame)) && !TextFocused && _ui?.MenuOpen!=true && _inventory != null)
        { _inventory.SetVisible(!_inventory.IsOpen); }
        _windowInventory = false;
        if (_inventory?.Data?.Hp==0 || _combatPaused)
        {
            if (_pickupRecovery.Pending && (_windowPickup || (k != null && k.zKey.wasPressedThisFrame))) RequestPickup();
            _inputGate.Filter(false, ReadHeldInput(k)); ClearWindowInput(false);
            _motion.StopInput(); _attack.Cancel(); _pickupMotion.Cancel(); _local.StopAttack();
            if (_inputProbe!=null) { _inputProbe.RecordField(_world,_inventory.Data,_players.Count,_monsters.Count,_remoteJump,_remoteLand,0,_smoke); _inputProbe.Observe(k,false,_inventory.IsOpen,_usingSceneTerrain,_motion); }
            return;
        }
        // 채팅·인벤토리·Map 변경·창 비활성화 중에는 캐릭터 조작을 멈춘다.
        bool allowed = WindowInputAllowed && !TextFocused && _trade?.IsOpen!=true && _trade?.Active!=true && _ui?.MenuOpen!=true && !_changingMap && k != null && (_inventory == null || !_inventory.IsOpen);
        int held = ReadHeldInput(k);
        int active = _inputGate.Filter(allowed, held);
        if (allowed && _inputGate.CanPress(GameplayInput3D.Attack) && (_windowAttack || k.jKey.wasPressedThisFrame || k.leftCtrlKey.wasPressedThisFrame)) RequestAttack();
        _windowAttack = false;
        if (allowed && _inputGate.CanPress(64) && (_windowPickup || k.zKey.wasPressedThisFrame)) RequestPickup();
        _windowPickup=false;
        if (!allowed) { _attack.Cancel(); _pickupMotion.Cancel(); _local.StopAttack(); }
        if (_inputProbe != null)
        {
            double firstX = _monsters.TryGetValue(1, out var first) ? first.State.X : 0;
            _inputProbe.RecordField(_world, _inventory?.Data, _players.Count, _monsters.Count, _remoteJump, _remoteLand, firstX, _smoke || _loadCount>0);
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
            _motion.SetInput(age < 0.7 ? (_smokeFull ? 1 : -1) : 0, age < 0.7 ? 1 : 0, _motion.Tick == 0);
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
        bool pickup=_pickupMotion.Take(batch,out ulong pickupSequence,out _); ulong dropId=_pendingPickupId; if (pickup) _pendingPickupId=0;
        try
        {
            await _network.SendMovementActionsAsync(batch);
            if (attack && ReferenceEquals(_world, world) && _attack.CanSend(attackSequence) && CanAttackInput())
                await _network.SendAttackAsync(world.MapId, world.Generation, attackSequence, actionSequence);
            if (pickup && ReferenceEquals(_world,world) && _pickupMotion.CanSend(pickupSequence) && CanAttackInput())
            {
                // 전송 이후 결과를 잃어도 재접속에서 조회할 수 있도록 요청한 드롭 번호만 보관한다.
                _pickupRecovery.Begin(dropId, Time.unscaledTimeAsDouble);
                if (_pickupKey != null) { PlayerPrefs.SetString(_pickupKey, dropId.ToString()); PlayerPrefs.Save(); }
                await _network.PickupAsync(world.MapId,world.Generation,pickupSequence,dropId);
            }
        }
        catch (Exception e) { if (ReferenceEquals(_world, world)) { _failed = true; _status = e.Message; } }
        finally { if (ReferenceEquals(_world, world)) _sending = false; }
    }
    // 포커스를 잃으면 눌린 키와 대기 입력을 비워 복귀 후 이동·점프가 남지 않도록 한다.
    private void OnApplicationFocus(bool focused)
    {
        _focusPending=true; UiInputAllowed=false;
        if (!focused) { _attack.Cancel(); _pickupMotion.Cancel(); _inputGate.Reset(); ClearWindowInput(); _windowButtons.Clear(); _motion.StopInput(); }
    }
    private void UpdateWindowInput()
    {
        if (_focusPending)
        {
            _focusPending=false; _local?.StopAttack();
            if (Application.platform==RuntimePlatform.WindowsPlayer)
            {
                foreach(var module in _inputModules) if(module!=null) module.enabled=false;
                if(Application.isFocused)
                {
                    // 동기화 이벤트가 다음 입력 갱신에서 반영될 때까지 이미 눌린 키를 새 입력으로 쓰지 않는다.
                    foreach(var device in new InputDevice[] { Keyboard.current, Mouse.current }) if(device!=null)
                    { if(!device.enabled) InputSystem.EnableDevice(device); if(!InputSystem.TrySyncDevice(device)) InputSystem.ResetDevice(device); }
                    _focusFrames=2;
                }
                else
                {
                    foreach(var device in new InputDevice[] { Keyboard.current, Mouse.current }) if(device!=null) InputSystem.ResetDevice(device);
                    _focusFrames=0;
                }
                _inputGate.Reset();
            }
        }
        _focusSettling=_focusFrames>0;
        if(_focusSettling) { --_focusFrames; _inputGate.Reset(); }
        if(Application.platform==RuntimePlatform.WindowsPlayer)
            foreach(var module in _inputModules) if(module!=null && module.enabled!=(Application.isFocused && !_focusSettling)) module.enabled=Application.isFocused && !_focusSettling;
        UiInputAllowed=WindowInputAllowed;
    }
    private int ReadHeldInput(Keyboard k)
    {
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
        if (k!=null && (k.zKey.isPressed || _windowButtons.Contains(KeyCode.Z))) held|=64;
        return held;
    }
    private void ClearWindowInput(bool releaseKeys = true) { if (releaseKeys) _windowKeys.Clear(); _tapX = _tapZ = _windowTapX = _windowTapZ = 0; _windowJump = _windowInventory = _windowEscape = _windowAttack = _windowPickup = false; }
    private void ReadWindowKeyEvent(Event input)
    {
        // InputSystem에 상태가 잡히지 않는 키보드 입력은 실제 창 이벤트로 보완한다.
        // 일반 키보드 경로와 함께 사용하되 고정 이동 단계가 실행될 때까지 입력을 보존한다.
        if (_smoke || !WindowInputAllowed) return;
        if (input.type == EventType.KeyUp) { _windowKeys.Remove(input.keyCode); _windowButtons.Remove(input.keyCode); return; }
        if (input.type != EventType.KeyDown) return;
        // 채팅 입력 중에도 Esc는 포커스 해제로 처리하고 일반 문자는 게임 조작에 쓰지 않는다.
        if (TextFocused && input.keyCode != KeyCode.Escape) return;
        Keyboard keyboard = Keyboard.current;
        KeyCode key = input.keyCode;
        // 키 코드 없이 문자만 전달된 창 이벤트도 이동·점프·인벤토리 입력으로 읽는다.
        if (key == KeyCode.None)
            key = char.ToLowerInvariant(input.character) switch { 'w' => KeyCode.W, 'a' => KeyCode.A, 's' => KeyCode.S, 'd' => KeyCode.D, 'i' => KeyCode.I, 'j' => KeyCode.J, 'z' => KeyCode.Z, ' ' => KeyCode.Space, _ => KeyCode.None };
        bool deviceHeld=keyboard!=null && (key switch { KeyCode.Space=>keyboard.spaceKey.isPressed, KeyCode.I=>keyboard.iKey.isPressed, KeyCode.Escape=>keyboard.escapeKey.isPressed, KeyCode.J=>keyboard.jKey.isPressed, KeyCode.LeftControl=>keyboard.leftCtrlKey.isPressed, KeyCode.Z=>keyboard.zKey.isPressed, _=>false });
        if ((key == KeyCode.Space || key == KeyCode.I || key == KeyCode.Escape || key == KeyCode.J || key == KeyCode.LeftControl || key==KeyCode.Z) && !_windowButtons.Press(key,input.keyCode!=KeyCode.None,deviceHeld))
        { input.Use(); return; } // 키를 누른 동안 발생하는 OS 반복 이벤트는 새 조작으로 처리하지 않는다.
        bool handled = true;
        // InputSystem에서 이미 읽은 버튼 입력은 창 이벤트에서 중복 처리하지 않는다.
        if (key == KeyCode.I) { if (keyboard == null || (!keyboard.iKey.wasPressedThisFrame && !keyboard.iKey.isPressed)) { _windowInventory = true; _inputProbe?.RecordWindowKey(key, false, false); } }
        else if (key == KeyCode.Escape) { if (keyboard == null || (!keyboard.escapeKey.wasPressedThisFrame && !keyboard.escapeKey.isPressed)) _windowEscape = true; }
        else if (_inventory != null && _inventory.IsOpen) handled = false;
        else if (_ui?.MenuOpen==true || !_motion.Ready) handled=false;
        else if (key==KeyCode.Z) { if (keyboard==null || !keyboard.zKey.wasPressedThisFrame) _windowPickup=true; }
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
        _trade?.SetLocal(_localId,$"MMORPG.PendingTrade.{_connectedHost}.{_port}.{_localId}");
        _pickupKey = $"MMORPG.PendingPickup.{_connectedHost}.{_port}.{_localId}";
        _pickupRecovery.Clear();
        if (ulong.TryParse(PlayerPrefs.GetString(_pickupKey, ""), out ulong pendingDrop) && pendingDrop != 0)
            _pickupRecovery.Restore(pendingDrop, Time.unscaledTimeAsDouble);
        if (_inventory != null) _inventory.RefreshFromServer();
        if (_smokeFull) { _smokeEntered = data.CharacterId == _smokeCharacterId; Debug.Log($"3D_FULL_TEST: EnterGame Success character={data.CharacterId} protocol={GameProtocol.ProtocolVersion}"); }
    }
    private void WorldReceived(World3DData world)
    {
        _busy=_changingMap=false;
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
            var previewWorld=_sceneWorld!=null ? _sceneWorld.ReadWorld() : World3DData.Preview();
            if (_loadCount>0) { previewWorld.SpawnX=-195; previewWorld.SpawnZ=-266; }
            ApplyWorld(previewWorld, _sceneWorld != null);
            if (_inventory != null) { _inventory.Clear(); _inventory.Apply(ItemCatalog.Preview()); if (_hud != null) _hud.Apply(_inventory.Data); }
            for (uint i=1;i<=(_loadCount>0 ? _loadCount : 3);++i) CreateMonster(i,(byte)((i-1)%3)).Apply(new() { Id=i,Kind=(byte)((i-1)%3),X=_loadCount>0 ? -360+(i-1)%16*22 : -160+(i-1)*25,Z=_loadCount>0 ? -350+(i-1)/16*24 : -100+(i-1)*55,Hp=60,MaxHp=60,Revision=1 });
            _status = _usingSceneTerrain ? "Placed scene preview: R resets position." : "Offline preview: R resets position.";
        }
        catch (Exception e) { ClearWorld(); _localId = 0; _status = e.Message; Debug.LogException(e); }
        _ui?.ClearFocus();
    }
    private void Disconnected()
    {
        _trade?.SetLocal(0);
        _pickupRecovery.Clear(); _pickupKey = null;
        ClearWorld(); _localId = 0; _offline = false; _busy = _changingMap = false;
        if (_inventory != null) _inventory.Clear();
        _characters = Array.Empty<CharacterInfo>(); _status = "Game connection closed.";
    }
    private void ClearWorld()
    {
        _trade?.ClearPeers();
        _world = null; _motion.Reset(); _tapX = _tapZ = 0; _sending = false; _failed = false;
        _attack.Cancel(); if (_hud != null) _hud.SetVisible(false);
        _pickupMotion.Cancel(); _pendingPickupId=_dropRevision=0;
        _combatPaused = false; _combatRevision = 0;
        foreach (var drop in _drops.Values) if (drop!=null) Destroy(drop.gameObject); _drops.Clear();
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
        // 명시한 검증 실행에서만 응답 유실 프록시를 거치고 일반 접속은 서버가 안내한 포트를 사용한다.
        try { await _network.ConnectGameServerAsync(_connectedHost, _testGamePort!=0 ? _testGamePort : ticket.GameServerPort, ticket.AuthKey); }
        catch (Exception e) { _busy = false; _status = e.Message; }
    }
    private async void ChangeMap()
    {
        if (_combatPaused) { _status = "Combat recovery is still in progress."; return; }
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
    private void AddChat(string text) { _chatLines.Add(text); if (_chatLines.Count > 6) _chatLines.RemoveAt(0); _ui?.ShowChat(string.Join("\n",_chatLines)); }
    private async void InventoryRequested(InventoryOperation operation, byte category, byte source, byte target, ulong requestId)
    {
        if (_inventory == null) return;
        if (_offline)
        {
            // 서버에 접속하지 않은 미리보기에서만 로컬 인벤토리 규칙을 실행한다.
            var preview = _inventory.Data ?? ItemCatalog.Preview();
            InventoryPreview.Apply(preview, operation, category, source, target); preview.RequestId=requestId; _inventory.Apply(preview); if (_hud != null) _hud.Apply(_inventory.Data); return;
        }
        int attempt = _attempt;
        // 온라인에서는 수량·HP·MP를 먼저 바꾸지 않고 서버가 반환한 전체 상태를 기다린다.
        try { await _network.SendInventoryAsync(operation, category, source, target, _inventory.Data?.Revision ?? 0,requestId); }
        catch (Exception e) { if (attempt == _attempt) _inventory.Fail(e.Message); }
    }
    private bool CanAttackInput() => !_combatPaused && WindowInputAllowed && !TextFocused && _trade?.IsOpen!=true && _trade?.Active!=true && _ui?.MenuOpen!=true && _motion.Ready && !_changingMap && !_failed &&
        (_inventory == null || !_inventory.IsOpen) && _inventory?.Data?.Hp > 0;
    private void RequestAttack()
    {
        bool allowed = CanAttackInput();
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
        _inputProbe?.RecordAttack(data,data.CharacterId==_localId);
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
        _inputProbe?.RecordMonsters(data);
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
    private async void RequestChannels()
    {
        if (_offline || _localId==0) return;
        try { await _network.RequestChannelsAsync(); } catch (Exception error) { _status=error.Message; }
    }
    private async void ChangeChannel(ushort channel)
    {
        if (_offline || _world==null || _busy) return;
        _channelTarget=channel; _busy=_changingMap=true; _deadline=Time.unscaledTimeAsDouble+10;
        _attack.Cancel(); _pickupMotion.Cancel(); _local?.StopAttack(); _motion.StopInput();
        try { await _network.ChangeChannelAsync(channel); }
        catch (Exception error) { _channelTarget=0; _busy=_changingMap=false; _status=error.Message; }
    }
    private async void Revive()
    {
        if (_offline || _inventory?.Data?.Hp!=0 || _busy) return;
        _busy=_reviving=true; _deadline=Time.unscaledTimeAsDouble+10;
        try { await _network.ReviveAsync(); } catch (Exception error) { _reviving=_busy=false; _status=error.Message; }
    }
    private void VitalsReceived(VitalsData data)
    {
        if (_world==null || data.MapId!=_world.MapId || data.Generation!=_world.Generation) return;
        if (data.CharacterId==_localId)
        {
            if (data.Revision < _combatRevision) return;
            _combatRevision = data.Revision;
            _inputProbe?.RecordCombat(data.Paused);
            if (_combatPaused != data.Paused)
            {
                _combatPaused = data.Paused; _inputGate.Filter(false, ReadHeldInput(Keyboard.current)); ClearWindowInput(false); _motion.StopInput();
                _attack.Cancel(); _pickupMotion.Cancel(); _local.StopAttack();
                _status = data.Paused ? "Combat paused. Restoring saved state..." : "Saved state restored. Combat resumed.";
            }
            if (_inventory!=null && _inventory.ApplyVitals(data)) { _hud?.Apply(_inventory.Data); _local.SetVitals(data.Hp,data.Damage); }
        }
        else if (_players.TryGetValue(data.CharacterId,out var player)) { player.PauseCombat(data.Paused); player.SetVitals(data.Hp,data.Damage); }
    }
    private void DropsReceived(DropsData data)
    {
        if (_world==null || data.MapId!=_world.MapId || data.Generation!=_world.Generation || data.Revision<=_dropRevision) return;
        _dropRevision=data.Revision; _presentDrops.Clear(); _missingDrops.Clear();
        _inputProbe?.RecordDrops(data.Drops.Count);
        foreach (var drop in data.Drops)
        {
            _presentDrops.Add(drop.Id);
            if (!_drops.TryGetValue(drop.Id,out var view)) { var obj=new GameObject("Field drop "+drop.Id); view=obj.AddComponent<FieldDropView>(); view.Initialize(drop); _drops.Add(drop.Id,view); }
            else view.Apply(drop);
        }
        foreach (var id in _drops.Keys) if (!_presentDrops.Contains(id)) _missingDrops.Add(id);
        foreach (var id in _missingDrops) { Destroy(_drops[id].gameObject); _drops.Remove(id); }
    }
    private void RequestPickup()
    {
        if (_offline || _world==null || _busy || !WindowInputAllowed || TextFocused || _ui?.MenuOpen==true || _inventory?.IsOpen==true) return;
        if (_pickupRecovery.Pending)
        {
            if (_pickupRecovery.Queries >= 3) _pickupRecovery.Restore(_pickupRecovery.DropId, Time.unscaledTimeAsDouble);
            _status = "Checking the previous pickup result..."; return;
        }
        if (_combatPaused || _inventory?.Data?.Hp == 0) return;
        ulong id=0; double closest=40*40; var pose=_motion.State;
        foreach (var entry in _drops) { var drop=entry.Value.State; double dx=drop.X-pose.X,dz=drop.Z-pose.Z,distance=dx*dx+dz*dz; if (distance<=closest && Math.Abs(drop.Y-pose.Y)<=30) { id=entry.Key; closest=distance; } }
        if (id==0) { _status="No item nearby."; return; }
        if (_pickupMotion.Queue(true,Time.unscaledTimeAsDouble,_motion)) _pendingPickupId=id;
    }
    private async void QueryPickup()
    {
        ulong id = _pickupRecovery.DropId;
        _status = "Checking pickup result...";
        try { await _network.PickupStatusAsync(id); }
        catch (Exception error) { _status = "Pickup result unavailable: " + error.Message; }
    }
    private void PickupReceived(ulong id, PickupResult result)
    {
        _inputProbe?.RecordPickup(result);
        if (!_pickupRecovery.Pending || id != _pickupRecovery.DropId) return;
        bool done = _pickupRecovery.Accept(id, result);
        _status = result switch
        {
            PickupResult.Success => "Item acquired. Inventory restored.",
            PickupResult.AlreadyTaken => "Another player acquired this item.",
            PickupResult.NoRecord => "No retained pickup record. Check the refreshed inventory.",
            PickupResult.Full => "Inventory is full.",
            PickupResult.Busy or PickupResult.StorageError => "Pickup result pending. LOOT [Z] retries the result query.",
            _ => "Pickup: " + result
        };
        if (done && _pickupKey != null) { PlayerPrefs.DeleteKey(_pickupKey); PlayerPrefs.Save(); }
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
        bool ok = _motion.State.Grounded && _motion.JumpId == 1 && (_smokeFull ? _motion.State.X > 0 : _motion.State.X < 0) && _motion.State.Z > 0 && _world.Boxes.Count == 5 && sceneSourceOk &&
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
    // 창 키 이벤트는 입력 보완에만 사용하고 모든 화면 조작은 UGUI에서 처리한다.
    private void OnGUI() { ReadWindowKeyEvent(Event.current); }
    private void OnDestroy() { ClearWorld(); }
}
