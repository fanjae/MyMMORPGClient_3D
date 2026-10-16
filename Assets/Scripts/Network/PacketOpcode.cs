public enum LoginPacketOpcode : ushort
{
    LoginRequest = 1,
    LoginResponse = 2,
    CharacterListRequest = 3,
    CharacterListResponse = 4,
    CharacterSelectRequest = 5,
    CharacterSelectResponse = 6,
    RegistrationRequest = 7,
    RegistrationResponse = 8
}

public enum GamePacketOpcode : ushort
{
    EnterGameRequest = 1,
    EnterGameResponse = 2,
    PlayerEnterMap = 3,
    PlayerLeaveMap = 4,
    MoveRequest = 5,
    PlayerMove = 6,
    ChangeMapRequest = 7,
    ChangeMapResponse = 8,
    ChatRequest = 9,
    PlayerChat = 10,
    MonsterEnterMap = 11,
    MoveResponse = 12,
    MapInfo = 13,
    MovementInput = 14,
    MovementState = 15,
    MapGeometry = 16,
    Foothold = 17,
    Collider = 18,
    GeometryEnd = 19,
    WhisperRequest = 20,
    ChatResponse = 21,
    WhisperMessage = 22,
    MovementActions = 23,
    MovementActionsBroadcast = 24, World3D = 25, WorldBox3D = 26, WorldEnd3D = 27, MovementState3D = 28, MovementActions3D = 29, MovementActionsBroadcast3D = 30,
    // 서버 Protocol/FieldPacket.h의 인벤토리 상태와 3D 필드 몬스터 목록을 사용한다.
    InventoryRequest = 31, InventorySnapshot = 32, FieldMonsters3D = 33,
    BasicAttackRequest3D = 34, BasicAttackEvent3D = 35,
    ChannelListRequest = 36, ChannelState = 37, ChangeChannelRequest = 38,
    Vitals3D = 39, ReviveRequest = 40, FieldDrops3D = 41, PickupRequest3D = 42, PickupResponse3D = 43, PickupStatusRequest3D = 44,
    TimingPing3D = 45, TimingPong3D = 46, TradeRequest3D = 47, TradeState3D = 48
}
