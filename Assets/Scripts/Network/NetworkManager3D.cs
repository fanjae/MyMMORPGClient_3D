using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;

public sealed class NetworkManager3D : MonoBehaviour
{
    private readonly MainThreadPacketQueue _mainThreadQueue = new();
    private readonly ConcurrentQueue<Action> _disconnectQueue = new();
    private const int MaxPacketsPerFrame = 256;

    private TcpSession _loginSession;
    private TcpSession _gameSession;
    public NetworkTiming3D Timing { get; } = new();
    public double PlaybackBufferSeconds { get; set; } = 0.2;
    private static double Clock => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    private async void SendTimingProbe(TcpSession session, ulong sequence)
    {
        try { using PacketWriter writer = new(); writer.Write(sequence); await session.SendAsync((ushort)GamePacketOpcode.TimingPing3D, writer.ToArray()); }
        catch (Exception) { if (ReferenceEquals(_gameSession, session)) DisconnectSession(session, true); }
    }
    private World3DData _pendingGeometry;
    public bool MeasureFieldReceive { get; set; }
    public long FieldParts { get; private set; }
    public double FieldReceiveMilliseconds { get; private set; }
    public double FieldApplyMilliseconds { get; private set; }
    private MapInfoData _mapBounds;

    public TcpSession.TrafficSnapshot GameTraffic => _gameSession?.GetTraffic() ?? default;
    public TcpSession.TrafficSnapshot MovementSendTraffic => _gameSession?.GetTraffic((ushort)GamePacketOpcode.MovementActions3D) ?? default;
    public TcpSession.TrafficSnapshot MovementReceiveTraffic => _gameSession?.GetTraffic((ushort)GamePacketOpcode.MovementActionsBroadcast3D) ?? default;

    public event Action<LoginResult> LoginCompleted;
    public event Action<RegistrationResult> RegistrationCompleted;
    public event Action<CharacterListData> CharacterListReceived;
    public event Action<CharacterSelectData> CharacterSelected;

    public event Action<EnterGameData> EnterGameReceived;
    public event Action<PlayerEnterData> PlayerEntered;
    public event Action<uint> PlayerLeft;
    public event Action<PlayerMoveData> PlayerMoved;
    public event Action<MoveResponseData> MoveCompleted;
    public event Action<MapInfoData> MapInfoReceived;
    public event Action<World3DData> GeometryReceived;
    public event Action<Snapshot3DData> MovementReceived;
    public event Action<Motion3DBroadcast> MovementActionsReceived;
    public event Action<PlayerChatData> PlayerChatReceived;
    public event Action<WhisperData> WhisperReceived;
    public event Action<ChatResponseData> ChatRejected;
    public event Action<MonsterEnterData> MonsterEntered;
    public event Action<InventoryData> InventoryReceived;
    public event Action<FieldMonstersData> FieldMonstersReceived;
    public event Action<BasicAttackData> AttackReceived;
    public event Action<ChannelData> ChannelsReceived;
    public event Action<VitalsData> VitalsReceived;
    public event Action<DropsData> DropsReceived;
    public event Action<ulong,PickupResult> PickupReceived;
    public event Action<TradeStateData> TradeReceived;
    public Task SendTradeAsync(byte[] request) => _gameSession.SendAsync((ushort)GamePacketOpcode.TradeRequest3D,request);
    private readonly FieldMonsterAssembler _monsterAssembler = new();
    private uint _activeFieldMap;
    private ulong _activeFieldGeneration;
    public event Action<ChangeMapData> MapChanged;
    public event Action LoginDisconnected;
    public event Action GameDisconnected;

    private void Update()
    {
        if (_activeFieldGeneration != 0 && _gameSession != null && Timing.TryBegin(Clock, out ulong sequence)) SendTimingProbe(_gameSession, sequence);
        while (_disconnectQueue.TryDequeue(out Action disconnected))
            disconnected();

        // 한 프레임에 처리할 패킷 수를 제한해 누적 수신이 화면 갱신을 막지 않도록 한다.
        for (int count = 0; count < MaxPacketsPerFrame && _mainThreadQueue.TryDequeue(out Action action); ++count)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }
    }

    public async Task ConnectLoginServerAsync(string host, int port)
    {
        // 로그인 서버 전용 세션 생성 및 패킷 수신 콜백 등록
        _loginSession?.Dispose();
        TcpSession session = new();
        _loginSession = session;
        session.PacketReceived += (opcode, payload) => OnLoginPacketReceived(session, opcode, payload);
        session.Disconnected += () => _disconnectQueue.Enqueue(() => DisconnectSession(session, false));

        await session.ConnectAsync(host, port);
        if (!ReferenceEquals(_loginSession, session))
            throw new OperationCanceledException();
    }

    public Task SendLoginAsync(string loginId, string password)
    {
        byte[] payload = LoginProtocol.CreateLoginRequest(loginId, password);
        return _loginSession.SendAsync((ushort)LoginPacketOpcode.LoginRequest, payload);
    }

    public Task RequestCharacterListAsync()
    {
        return _loginSession.SendAsync((ushort)LoginPacketOpcode.CharacterListRequest, Array.Empty<byte>());
    }

    public Task RegisterAccountAsync(string id,string password)=>
        _loginSession.SendAsync((ushort)LoginPacketOpcode.RegistrationRequest,LoginProtocol.CreateRegistrationRequest(id,password));

    public Task SelectCharacterAsync(uint characterId)
    {
        byte[] payload = LoginProtocol.CreateCharacterSelectRequest(characterId);
        return _loginSession.SendAsync((ushort)LoginPacketOpcode.CharacterSelectRequest, payload);
    }

    public async Task ConnectGameServerAsync(string host, ushort port, ulong authKey)
    {
        // 게임 서버 전용 세션 생성 및 연결
        _gameSession?.Dispose();
        TcpSession session = new();
        _gameSession = session;
        Timing.Reset();
        _pendingGeometry = null;
        _monsterAssembler.Clear(); _activeFieldMap = 0; _activeFieldGeneration = 0;
        session.PacketReceived += (opcode, payload) => OnGamePacketReceived(session, opcode, payload);
        session.Disconnected += () => _disconnectQueue.Enqueue(() => DisconnectSession(session, true));

        await session.ConnectAsync(host, port);
        if (!ReferenceEquals(_gameSession, session))
            throw new OperationCanceledException();

        // 로그인 서버에서 발급받은 인증 키로 게임 서버 입장 요청
        byte[] payload = GameProtocol.CreateEnterGameRequest(authKey);
        await session.SendAsync((ushort)GamePacketOpcode.EnterGameRequest, payload);
    }

    public Task SendMoveAsync(MoveRequestData data)
    {
        byte[] payload = GameProtocol.CreateMoveRequest(data);
        return _gameSession.SendAsync((ushort)GamePacketOpcode.MoveRequest, payload);
    }

    public Task ChangeMapAsync(uint mapId)
    {
        byte[] payload = GameProtocol.CreateChangeMapRequest(mapId);
        return _gameSession.SendAsync((ushort)GamePacketOpcode.ChangeMapRequest, payload);
    }

    public Task SendMovementActionsAsync(Motion3DBatch batch)
    {
        return _gameSession.SendAsync((ushort)GamePacketOpcode.MovementActions3D, Movement3DProtocol.CreateBatch(batch));
    }

    // 현재 화면의 변경 번호를 함께 보내 서버가 오래된 슬롯 조작을 거절할 수 있도록 한다.
    public Task SendInventoryAsync(InventoryOperation operation, byte category, byte source, byte target, ulong revision, ulong requestId)
    {
        return _gameSession.SendAsync((ushort)GamePacketOpcode.InventoryRequest, FieldProtocol.InventoryRequest(operation, category, source, target, revision,requestId));
    }

    public Task SendAttackAsync(uint map, ulong generation, ulong sequence, ulong actionSequence) =>
        _gameSession.SendAsync((ushort)GamePacketOpcode.BasicAttackRequest3D, CombatProtocol.Request(map, generation, sequence, actionSequence));
    public Task RequestChannelsAsync() => _gameSession.SendAsync((ushort)GamePacketOpcode.ChannelListRequest,Array.Empty<byte>());
    public Task ChangeChannelAsync(ushort channel)
    { using PacketWriter writer=new(); writer.Write(channel); return _gameSession.SendAsync((ushort)GamePacketOpcode.ChangeChannelRequest,writer.ToArray()); }
    public Task ReviveAsync() => _gameSession.SendAsync((ushort)GamePacketOpcode.ReviveRequest,Array.Empty<byte>());
    public Task PickupAsync(uint map,ulong generation,ulong sequence,ulong drop) => _gameSession.SendAsync((ushort)GamePacketOpcode.PickupRequest3D,FieldExtensions.Pickup(map,generation,sequence,drop));
    public Task PickupStatusAsync(ulong drop)
    { using PacketWriter writer = new(); writer.Write(drop); return _gameSession.SendAsync((ushort)GamePacketOpcode.PickupStatusRequest3D, writer.ToArray()); }

    public Task SendChatAsync(string message)
    {
        byte[] payload = GameProtocol.CreateChatRequest(message);
        return _gameSession.SendAsync((ushort)GamePacketOpcode.ChatRequest, payload);
    }

    public Task SendWhisperAsync(uint targetCharacterId, string message)
    {
        return _gameSession.SendAsync((ushort)GamePacketOpcode.WhisperRequest, GameProtocol.CreateWhisperRequest(targetCharacterId, message));
    }

    private void OnLoginPacketReceived(TcpSession session, ushort opcode, byte[] payload)
    {
        // Unity API와 이벤트 구독자가 메인 스레드에서 실행되도록 큐에 등록
        EnqueuePacket(session, false, payload.Length + TcpSession.HeaderSize, () =>
        {
            if (!ReferenceEquals(_loginSession, session))
                return;

            switch ((LoginPacketOpcode)opcode)
            {
                case LoginPacketOpcode.RegistrationResponse:
                    RegistrationCompleted?.Invoke(LoginProtocol.ReadRegistrationResponse(payload));
                    break;
                case LoginPacketOpcode.LoginResponse:
                    LoginCompleted?.Invoke(LoginProtocol.ReadLoginResponse(payload));
                    break;

                case LoginPacketOpcode.CharacterListResponse:
                    CharacterListReceived?.Invoke(LoginProtocol.ReadCharacterListResponse(payload));
                    break;

                case LoginPacketOpcode.CharacterSelectResponse:
                    CharacterSelected?.Invoke(LoginProtocol.ReadCharacterSelectResponse(payload));
                    break;
            }
        });
    }

    private void OnGamePacketReceived(TcpSession session, ushort opcode, byte[] payload)
    {
        double receivedAt = Clock;
        // 게임 서버 패킷도 메인 스레드에서 역직렬화 후 이벤트로 전달
        EnqueuePacket(session, true, payload.Length + TcpSession.HeaderSize, () =>
        {
            if (!ReferenceEquals(_gameSession, session))
                return;

            switch ((GamePacketOpcode)opcode)
            {
                case GamePacketOpcode.TimingPong3D:
                    if (payload.Length != 8) throw new System.IO.InvalidDataException("Invalid timing response.");
                    Timing.Accept(new PacketReader(payload, 8).ReadUInt64(), receivedAt);
                    break;
                case GamePacketOpcode.TradeState3D: TradeReceived?.Invoke(TradeProtocol.Read(payload)); break;
                case GamePacketOpcode.ChannelState: ChannelsReceived?.Invoke(FieldExtensions.ReadChannels(payload)); break;
                case GamePacketOpcode.Vitals3D: VitalsReceived?.Invoke(FieldExtensions.ReadVitals(payload)); break;
                case GamePacketOpcode.FieldDrops3D: DropsReceived?.Invoke(FieldExtensions.ReadDrops(payload)); break;
                case GamePacketOpcode.PickupResponse3D: var pickup=FieldExtensions.ReadPickup(payload); PickupReceived?.Invoke(pickup.Id,pickup.Result); break;
                case GamePacketOpcode.EnterGameResponse:
                    EnterGameReceived?.Invoke(GameProtocol.ReadEnterGameResponse(payload));
                    break;

                case GamePacketOpcode.PlayerEnterMap:
                    PlayerEntered?.Invoke(GameProtocol.ReadPlayerEnterMap(payload));
                    break;

                case GamePacketOpcode.PlayerLeaveMap:
                    PlayerLeft?.Invoke(GameProtocol.ReadPlayerLeaveMap(payload));
                    break;

                case GamePacketOpcode.PlayerMove:
                    PlayerMoved?.Invoke(GameProtocol.ReadPlayerMove(payload));
                    break;

                case GamePacketOpcode.MoveResponse:
                    MoveCompleted?.Invoke(GameProtocol.ReadMoveResponse(payload));
                    break;

                case GamePacketOpcode.MapInfo:
                    _mapBounds = GameProtocol.ReadMapInfo(payload);
                    _pendingGeometry = null;
                    _monsterAssembler.Clear(); _activeFieldMap = 0; _activeFieldGeneration = 0;
                    MapInfoReceived?.Invoke(_mapBounds);
                    break;

                case GamePacketOpcode.World3D:
                    _monsterAssembler.Clear();
                    _pendingGeometry = Movement3DProtocol.ReadWorld(payload);
                    if (_pendingGeometry.MapId != _mapBounds.MapId)
                        throw new System.IO.InvalidDataException("3D world without matching MapInfo");
                    break;
                case GamePacketOpcode.WorldBox3D:
                    Movement3DProtocol.AddBox(_pendingGeometry, payload);
                    break;
                case GamePacketOpcode.WorldEnd3D:
                    Movement3DProtocol.CompleteWorld(_pendingGeometry, payload);
                    _activeFieldMap = _pendingGeometry.MapId; _activeFieldGeneration = _pendingGeometry.Generation;
                    GeometryReceived?.Invoke(_pendingGeometry);
                    _pendingGeometry = null;
                    break;
                case GamePacketOpcode.MovementState3D:
                    MovementReceived?.Invoke(Movement3DProtocol.ReadState(payload));
                    break;
                case GamePacketOpcode.MovementActionsBroadcast3D:
                    MovementActionsReceived?.Invoke(Movement3DProtocol.ReadBroadcast(payload));
                    break;
                case GamePacketOpcode.PlayerChat:
                    PlayerChatReceived?.Invoke(GameProtocol.ReadPlayerChat(payload));
                    break;
                case GamePacketOpcode.WhisperMessage:
                    WhisperReceived?.Invoke(GameProtocol.ReadWhisper(payload));
                    break;
                case GamePacketOpcode.ChatResponse:
                    ChatRejected?.Invoke(GameProtocol.ReadChatResponse(payload));
                    break;

                case GamePacketOpcode.ChangeMapResponse:
                    MapChanged?.Invoke(GameProtocol.ReadChangeMapResponse(payload));
                    break;

                case GamePacketOpcode.MonsterEnterMap:
                    MonsterEntered?.Invoke(GameProtocol.ReadMonsterEnterMap(payload));
                    break;
                // 큐에서 실행되므로 인벤토리 UI와 몬스터 표시도 Unity 메인 스레드에서 갱신한다.
                case GamePacketOpcode.InventorySnapshot:
                    InventoryReceived?.Invoke(FieldProtocol.ReadInventory(payload));
                    break;
                case GamePacketOpcode.BasicAttackEvent3D:
                    AttackReceived?.Invoke(CombatProtocol.Read(payload)); break;
                case GamePacketOpcode.FieldMonsters3D:
                    long receiveStart=MeasureFieldReceive ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    var part = FieldProtocol.ReadMonsters(payload);
                    if (part.MapId != _activeFieldMap || part.Generation != _activeFieldGeneration) break;
                    var monsters = _monsterAssembler.Accept(part);
                    long applyStart=MeasureFieldReceive ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    if(MeasureFieldReceive) { ++FieldParts; FieldReceiveMilliseconds+=(applyStart-receiveStart)*1000.0/System.Diagnostics.Stopwatch.Frequency; }
                    if (monsters != null) FieldMonstersReceived?.Invoke(monsters);
                    if(MeasureFieldReceive) FieldApplyMilliseconds+=(System.Diagnostics.Stopwatch.GetTimestamp()-applyStart)*1000.0/System.Diagnostics.Stopwatch.Frequency;
                    break;
            }
        });
    }

    private void EnqueuePacket(TcpSession session, bool game, int bytes, Action action)
    {
        if (!_mainThreadQueue.TryEnqueue(() =>
        {
            try { action(); }
            catch (System.IO.InvalidDataException exception)
            {
                DisconnectSession(session, game);
                Debug.LogException(exception);
            }
        }, bytes))
        {
            // 큐에 넣지 못한 연결만 종료하고 이전에 누적된 패킷은 세션 검사로 무시한다.
            session.Dispose();
            _disconnectQueue.Enqueue(() => DisconnectSession(session, game));
        }
    }

    private void DisconnectSession(TcpSession session, bool game)
    {
        if (game && ReferenceEquals(_gameSession, session))
        {
            _gameSession = null;
            Timing.Reset(); _activeFieldGeneration = 0;
            _pendingGeometry = null;
            session.Dispose();
            GameDisconnected?.Invoke();
        }
        else if (!game && ReferenceEquals(_loginSession, session))
        {
            _loginSession = null;
            session.Dispose();
            LoginDisconnected?.Invoke();
        }
    }

    public void DisconnectAll()
    {
        if (_loginSession != null)
            DisconnectSession(_loginSession, false);
        if (_gameSession != null)
            DisconnectSession(_gameSession, true);
    }

    private void OnDestroy()
    {
        // NetworkManager 파괴 시 열려 있는 세션 정리
        _loginSession?.Dispose();
        _gameSession?.Dispose();
    }
}
