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
        if (_smokeFull)
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
        Keyboard k = Keyboard.current;
        if (k != null && k.escapeKey.wasPressedThisFrame) GUI.FocusControl(null);
        bool allowed = Application.isFocused && GUIUtility.keyboardControl == 0 && !_changingMap && k != null;
        if (_smoke)
        {
            if (_smokeOnline && !_smokePeerReady)
            {
                if (_players.Count == 0) { _motion.StopInput(); return; }
                _smokePeerReady = true;
            }
            double age = _motion.Tick * Motion3DSimulation.FixedDelta;
            _motion.SetInput(age < 0.7 ? -1 : 0, age < 0.7 ? 1 : 0, _motion.Tick == 0);
            if (age > 3 && !_smokeFinishing) { _smokeFinishing = true; StartCoroutine(FinishSmoke()); }
        }
        else if (allowed)
        {
            int x = ((k.dKey.isPressed || k.rightArrowKey.isPressed) ? 1 : 0) - ((k.aKey.isPressed || k.leftArrowKey.isPressed) ? 1 : 0);
            int z = ((k.wKey.isPressed || k.upArrowKey.isPressed) ? 1 : 0) - ((k.sKey.isPressed || k.downArrowKey.isPressed) ? 1 : 0);
            _motion.SetInput(x, z, k.spaceKey.wasPressedThisFrame);
            if (_offline && k.rKey.wasPressedThisFrame) _motion.Configure(_world);
        }
        else _motion.StopInput();
        try
        {
            _motion.Advance(Time.unscaledDeltaTime);
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
        try { await _network.SendMovementActionsAsync(batch); }
        catch (Exception e) { if (ReferenceEquals(_world, world)) { _failed = true; _status = e.Message; } }
        finally { if (ReferenceEquals(_world, world)) _sending = false; }
    }
    private void OnApplicationFocus(bool focused) { if (!focused) _motion.StopInput(); }
    private void Entered(EnterGameData data)
    {
        _busy = false; _characters = Array.Empty<CharacterInfo>();
        if (data.Result != EnterGameResult.Success) { _status = $"Enter: {data.Result}"; return; }
        _offline = false; _localId = data.CharacterId; _status = $"Entered as {data.Name} ({data.CharacterId}).";
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
            _status = _usingSceneTerrain ? "Placed scene preview: R resets position." : "Offline preview: R resets position.";
        }
        catch (Exception e) { ClearWorld(); _localId = 0; _status = e.Message; Debug.LogException(e); }
        GUI.FocusControl(null);
    }
    private void Disconnected()
    {
        ClearWorld(); _localId = 0; _offline = false; _busy = _changingMap = false;
        _characters = Array.Empty<CharacterInfo>(); _status = "Game connection closed.";
    }
    private void ClearWorld()
    {
        _world = null; _motion.Reset(); _sending = false; _failed = false;
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
        if (!_smokeFull) return;
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
            (!_smokeFull || (_smokeLogin && _smokeCharacters && _smokeTicket && _smokeEntered));
        File.WriteAllText("Logs/3d-smoke-" + label + ".result", (ok ? "PASS" : "FAILED") +
            $"\ngrounded={_motion.State.Grounded} jumpId={_motion.JumpId} x={_motion.State.X} z={_motion.State.Z} remoteJump={_remoteJump} remoteLand={_remoteLand}" +
            $"\nlogin={_smokeLogin} characters={_smokeCharacters} ticket={_smokeTicket} entered={_smokeEntered} protocol={GameProtocol.ProtocolVersion}" +
            $"\nterrainSource={(_usingSceneTerrain ? "scene" : "server-or-fallback")} terrainSourceOk={sceneSourceOk}");
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
        Rect panel = new(10, 10, 370, Math.Max(200, Screen.height - 195));
        Rect chatPanel = new(10, Screen.height - 175, 450, 165);
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
            GUILayout.Label("WASD / Arrows: move · Space: jump · Esc: leave chat");
            GUILayout.Label($"Map: {_world?.MapId} · Local: {_localId} · Remote: {_players.Count}");
            var state = _motion.State;
            GUILayout.Label($"Position: {state.X / 20:F2}, {state.Y / 20:F2}, {state.Z / 20:F2} m");
            GUILayout.Label($"Grounded: {state.Grounded} · Jump ready: {_motion.CanJump} · Actions: {_motion.PendingActions}");
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
