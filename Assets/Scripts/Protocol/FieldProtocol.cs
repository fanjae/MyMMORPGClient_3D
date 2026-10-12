using System;
using System.Collections.Generic;
using System.IO;

public enum InventoryOperation : byte { Refresh, Move, Sort, Use }
public enum InventoryResult : byte { Success, InvalidSlot, StaleRevision, CannotUse, StorageError, RateLimited }
public struct InventoryEntry { public uint ItemId; public ushort Quantity; }
// 분류 번호 * 24 + 슬롯 번호 순서로 서버의 전체 인벤토리 상태를 보관한다.
public sealed class InventoryData
{
    public ulong Revision, Mesos;
    public ushort Hp, MaxHp, Mp, MaxMp;
    public InventoryResult Result;
    public readonly InventoryEntry[] Entries = new InventoryEntry[120];
}
public struct FieldMonsterData { public uint Id; public byte Kind; public double X, Y, Z; public ushort Yaw, Hp, MaxHp; public ulong Revision; }
public sealed class FieldMonstersData
{
    public uint MapId; public ulong Generation, Tick;
    public readonly List<FieldMonsterData> Monsters = new();
}
public static class FieldProtocol
{
    public static byte[] InventoryRequest(InventoryOperation operation, byte category, byte source, byte target, ulong revision)
    {
        // 인증된 캐릭터는 서버 세션에서 선택하므로 조작과 변경 번호만 직렬화한다.
        using PacketWriter writer = new(); writer.Write((byte)operation); writer.Write(category); writer.Write(source); writer.Write(target); writer.Write(revision); return writer.ToArray();
    }
    public static InventoryData ReadInventory(byte[] payload)
    {
        // 서버의 packed 구조체와 크기·읽기 순서를 맞추고 표시 전에 상태를 검증한다.
        PacketReader reader = new(payload, 746);
        InventoryData data = new() { Revision = reader.ReadUInt64(), Mesos = reader.ReadUInt64(), Hp = reader.ReadUInt16(), MaxHp = reader.ReadUInt16(),
            Mp = reader.ReadUInt16(), MaxMp = reader.ReadUInt16(), Result = (InventoryResult)reader.ReadByte() };
        if (reader.ReadByte() != 24 || data.Revision == 0 || data.Result > InventoryResult.RateLimited || data.Hp > data.MaxHp || data.Mp > data.MaxMp)
            throw new InvalidDataException("Invalid inventory snapshot");
        for (int i = 0; i < 120; ++i)
        {
            var entry = new InventoryEntry { ItemId = reader.ReadUInt32(), Quantity = reader.ReadUInt16() };
            if ((entry.ItemId == 0) != (entry.Quantity == 0) || entry.Quantity > ItemCatalog.StackLimit(entry.ItemId, i / 24))
                throw new InvalidDataException("Invalid inventory item");
            data.Entries[i] = entry;
        }
        return data;
    }
    public static FieldMonstersData ReadMonsters(byte[] payload)
    {
        // 22바이트 헤더 뒤에 31바이트 레코드가 count개 이어져야 한다.
        if (payload == null || payload.Length < 22 || (payload.Length - 22) % 31 != 0 || payload.Length > 4092)
            throw new InvalidDataException("Invalid monster snapshot size");
        PacketReader reader = new(payload, payload.Length);
        FieldMonstersData data = new() { MapId = reader.ReadUInt32(), Generation = reader.ReadUInt64(), Tick = reader.ReadUInt64() };
        int count = reader.ReadUInt16();
        if (count > 128 || count != (payload.Length - 22) / 31) throw new InvalidDataException("Invalid monster count");
        HashSet<uint> ids = new();
        for (int i = 0; i < count; ++i)
        {
            // 전송 좌표의 1/1000 정밀도를 서버 단위로 복원한다. Unity 거리 변환은 View에서 처리한다.
            FieldMonsterData monster = new() { Id = reader.ReadUInt32(), Kind = reader.ReadByte(), X = reader.ReadInt32() / 1000.0,
                Y = reader.ReadInt32() / 1000.0, Z = reader.ReadInt32() / 1000.0, Yaw = reader.ReadUInt16(), Hp = reader.ReadUInt16(), MaxHp = reader.ReadUInt16(), Revision = reader.ReadUInt64() };
            if (monster.Id == 0 || !ids.Add(monster.Id) || monster.Kind > 2 || monster.Yaw >= 36000 || monster.MaxHp == 0 || monster.Hp > monster.MaxHp || monster.Revision == 0) throw new InvalidDataException("Invalid monster record");
            data.Monsters.Add(monster);
        }
        return data;
    }
}

public enum BasicAttackResult : byte { Miss, Hit, Killed, Cooldown, InvalidState, Unavailable }
public sealed class BasicAttackData
{
    public uint MapId, CharacterId, MonsterId; public ulong Generation, Sequence, Revision;
    public BasicAttackResult Result; public ushort Hp, MaxHp, Damage, Yaw;
}
public static class CombatProtocol
{
    public static byte[] Request(uint map, ulong generation, ulong sequence, ulong actionSequence)
    {
        using PacketWriter writer = new(); writer.Write(map); writer.Write(generation); writer.Write(sequence); writer.Write(actionSequence); return writer.ToArray();
    }
    public static BasicAttackData Read(byte[] payload)
    {
        PacketReader reader = new(payload, 45);
        var data = new BasicAttackData { MapId = reader.ReadUInt32(), Generation = reader.ReadUInt64(), Sequence = reader.ReadUInt64(), CharacterId = reader.ReadUInt32(),
            Result = (BasicAttackResult)reader.ReadByte(), MonsterId = reader.ReadUInt32(), Hp = reader.ReadUInt16(), MaxHp = reader.ReadUInt16(),
            Revision = reader.ReadUInt64(), Damage = reader.ReadUInt16(), Yaw = reader.ReadUInt16() };
        bool hit = data.Result == BasicAttackResult.Hit || data.Result == BasicAttackResult.Killed;
        if (data.CharacterId == 0 || data.Result > BasicAttackResult.Unavailable || data.Yaw >= 36000 || data.Hp > data.MaxHp ||
            (hit && (data.MonsterId == 0 || data.MaxHp == 0 || data.Revision == 0 || data.Damage == 0)) ||
            (data.Result == BasicAttackResult.Killed && data.Hp != 0))
            throw new InvalidDataException("Invalid basic attack event");
        return data;
    }
}
