using System;
using System.Collections.Generic;
using System.IO;

public enum Action3DKind : byte { Input, Jump, Land, Checkpoint, Respawn, Fall }
public struct Motion3DState
{
    public double X, Y, Z, VelocityX, VelocityY, VelocityZ;
    public uint SupportId;
    public ushort Yaw;
    public bool Grounded;
}
public struct Motion3DAction
{
    public ulong Sequence, ClientTick, JumpId;
    public Motion3DState State;
    public short InputX, InputZ;
    public Action3DKind Kind;
}
public struct Relay3DAction
{
    public uint CharacterId;
    public ulong LatestClientTick;
    public Motion3DAction Action;
}
public struct Snapshot3DData
{
    public uint MapId;
    public ulong Generation;
    public Relay3DAction State;
}
public sealed class Motion3DBatch
{
    public uint MapId;
    public ulong Generation, BatchSequence, LatestClientTick;
    public readonly List<Motion3DAction> Actions = new();
}
public sealed class Motion3DBroadcast
{
    public uint MapId;
    public ulong Generation, ServerTick;
    public readonly List<Relay3DAction> Actions = new();
}
public struct Box3DData
{
    public uint Id;
    public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
}
public sealed class World3DData
{
    public uint MapId, Version;
    public ulong Generation;
    public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ, SpawnX, SpawnY, SpawnZ;
    public uint Radius, Height, Speed, JumpSpeed, Gravity, MaxFallSpeed;
    public ushort BoxCount;
    public readonly List<Box3DData> Boxes = new();

    public uint SpawnSupport()
    {
        foreach (var box in Boxes)
            if (SpawnY == box.MaxY && SpawnX >= box.MinX && SpawnX <= box.MaxX && SpawnZ >= box.MinZ && SpawnZ <= box.MaxZ)
                return box.Id;
        return 0;
    }
    public static World3DData Preview()
    {
        // 오프라인 예제이며 온라인에서는 서버가 보낸 지형으로 전체 교체한다.
        World3DData world = new() { MapId = 100000002, Generation = 1, Version = 1,
            MinX = -400, MinY = -200, MinZ = -400, MaxX = 400, MaxY = 400, MaxZ = 400,
            Radius = 6, Height = 36, Speed = 80, JumpSpeed = 160, Gravity = 400, MaxFallSpeed = 320, BoxCount = 5 };
        world.Boxes.Add(new() { Id = 1, MinX = -400, MinY = -40, MinZ = -400, MaxX = 400, MaxY = 0, MaxZ = 240 });
        world.Boxes.Add(new() { Id = 2, MinX = -400, MinY = -40, MinZ = 320, MaxX = 400, MaxY = 0, MaxZ = 400 });
        world.Boxes.Add(new() { Id = 3, MinX = 80, MinY = 0, MinZ = -140, MaxX = 100, MaxY = 80, MaxZ = 100 });
        world.Boxes.Add(new() { Id = 4, MinX = -140, MinY = 0, MinZ = 80, MaxX = -80, MaxY = 10, MaxZ = 140 });
        world.Boxes.Add(new() { Id = 5, MinX = -140, MinY = 0, MinZ = 180, MaxX = -60, MaxY = 30, MaxZ = 240 });
        return world;
    }
}

public static class Movement3DProtocol
{
    public const int ActionSize = 84, MaxActions = 32, MaxBroadcastActions = 42;
    public static byte[] CreateBatch(Motion3DBatch batch)
    {
        if (batch == null || batch.Actions.Count == 0 || batch.Actions.Count > MaxActions) throw new InvalidDataException("Invalid 3D batch count");
        using PacketWriter writer = new();
        writer.Write(batch.MapId); writer.Write(batch.Generation); writer.Write(batch.BatchSequence);
        writer.Write(batch.LatestClientTick); writer.Write((ushort)batch.Actions.Count);
        foreach (var action in batch.Actions) WriteAction(writer, action);
        return writer.ToArray();
    }
    public static void WriteAction(PacketWriter w, Motion3DAction a)
    {
        ValidateAction(a);
        w.Write(a.Sequence); w.Write(a.ClientTick); w.Write(a.JumpId);
        w.Write(Quantize(a.State.X)); w.Write(Quantize(a.State.Y)); w.Write(Quantize(a.State.Z));
        w.Write(Quantize(a.State.VelocityX)); w.Write(Quantize(a.State.VelocityY)); w.Write(Quantize(a.State.VelocityZ));
        w.Write(a.State.SupportId); w.Write(unchecked((ushort)a.InputX)); w.Write(unchecked((ushort)a.InputZ));
        w.Write(a.State.Yaw); w.Write(a.State.Grounded ? (byte)1 : (byte)0); w.Write((byte)a.Kind);
    }
    private static Motion3DAction ReadAction(PacketReader r)
    {
        Motion3DAction a = new() { Sequence = r.ReadUInt64(), ClientTick = r.ReadUInt64(), JumpId = r.ReadUInt64() };
        a.State.X = r.ReadInt64() / 1000.0; a.State.Y = r.ReadInt64() / 1000.0; a.State.Z = r.ReadInt64() / 1000.0;
        a.State.VelocityX = r.ReadInt64() / 1000.0; a.State.VelocityY = r.ReadInt64() / 1000.0; a.State.VelocityZ = r.ReadInt64() / 1000.0;
        a.State.SupportId = r.ReadUInt32(); a.InputX = unchecked((short)r.ReadUInt16()); a.InputZ = unchecked((short)r.ReadUInt16());
        a.State.Yaw = r.ReadUInt16(); byte grounded = r.ReadByte(); a.Kind = (Action3DKind)r.ReadByte();
        if (grounded > 1) throw new InvalidDataException("Invalid 3D grounded flag");
        a.State.Grounded = grounded != 0; ValidateAction(a); return a;
    }
    private static void ValidateAction(Motion3DAction a)
    {
        if (a.Kind > Action3DKind.Fall || a.State.Yaw >= 36000 || (long)a.InputX * a.InputX + (long)a.InputZ * a.InputZ > 1000000 ||
            (!a.State.Grounded && a.State.SupportId != 0)) throw new InvalidDataException("Invalid 3D action flags or input");
    }
    private static long Quantize(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > int.MaxValue) throw new InvalidDataException("Invalid 3D coordinate");
        return checked((long)Math.Round(value * 1000, MidpointRounding.AwayFromZero));
    }
    public static Motion3DBroadcast ReadBroadcast(byte[] payload)
    {
        if (payload == null || payload.Length < 22) throw new InvalidDataException("Truncated 3D broadcast");
        int count = payload[20] | payload[21] << 8;
        if (count < 1 || count > MaxBroadcastActions || payload.Length != 22 + count * 96) throw new InvalidDataException("Invalid 3D broadcast size");
        PacketReader r = new(payload, payload.Length);
        Motion3DBroadcast batch = new() { MapId = r.ReadUInt32(), Generation = r.ReadUInt64(), ServerTick = r.ReadUInt64() };
        r.ReadUInt16();
        for (int i = 0; i < count; ++i) batch.Actions.Add(new() { CharacterId = r.ReadUInt32(), LatestClientTick = r.ReadUInt64(), Action = ReadAction(r) });
        return batch;
    }
    public static Snapshot3DData ReadState(byte[] payload)
    {
        PacketReader r = new(payload, 108);
        uint map = r.ReadUInt32(); ulong generation = r.ReadUInt64();
        Relay3DAction state = new() { CharacterId = r.ReadUInt32(), LatestClientTick = r.ReadUInt64(), Action = ReadAction(r) };
        return new() { MapId = map, Generation = generation, State = state };
    }
    public static World3DData ReadWorld(byte[] payload)
    {
        PacketReader r = new(payload, 78);
        World3DData world = new() { MapId = r.ReadUInt32(), Generation = r.ReadUInt64(), Version = r.ReadUInt32(),
            MinX = r.ReadInt32(), MinY = r.ReadInt32(), MinZ = r.ReadInt32(), MaxX = r.ReadInt32(), MaxY = r.ReadInt32(), MaxZ = r.ReadInt32(),
            Radius = r.ReadUInt32(), Height = r.ReadUInt32(), Speed = r.ReadUInt32(), JumpSpeed = r.ReadUInt32(), Gravity = r.ReadUInt32(), MaxFallSpeed = r.ReadUInt32(),
            SpawnX = r.ReadInt32(), SpawnY = r.ReadInt32(), SpawnZ = r.ReadInt32(), BoxCount = r.ReadUInt16() };
        if (world.Version == 0 || world.Radius == 0 || world.Radius > 1000 || world.Height == 0 || world.Height > 2000 ||
            world.Speed == 0 || world.Speed > 10000 || world.JumpSpeed == 0 || world.JumpSpeed > 10000 || world.Gravity == 0 || world.Gravity > 10000 ||
            world.MaxFallSpeed == 0 || world.MaxFallSpeed > 10000 || world.BoxCount == 0 || world.BoxCount > 4096 ||
            world.MinX >= world.MaxX || world.MinY >= world.MaxY || world.MinZ >= world.MaxZ) throw new InvalidDataException("Invalid 3D world settings");
        return world;
    }
    public static void AddBox(World3DData world, byte[] payload)
    {
        PacketReader r = new(payload, 40);
        if (world == null || r.ReadUInt32() != world.MapId || r.ReadUInt64() != world.Generation || world.Boxes.Count >= world.BoxCount)
            throw new InvalidDataException("3D box without matching world");
        Box3DData b = new() { Id = r.ReadUInt32(), MinX = r.ReadInt32(), MinY = r.ReadInt32(), MinZ = r.ReadInt32(), MaxX = r.ReadInt32(), MaxY = r.ReadInt32(), MaxZ = r.ReadInt32() };
        if (b.Id == 0 || b.MinX >= b.MaxX || b.MinY >= b.MaxY || b.MinZ >= b.MaxZ || b.MinX < world.MinX || b.MaxX > world.MaxX ||
            b.MinY < world.MinY || b.MaxY > world.MaxY || b.MinZ < world.MinZ || b.MaxZ > world.MaxZ || world.Boxes.Exists(box => box.Id == b.Id))
            throw new InvalidDataException("Invalid 3D box bounds or identity");
        world.Boxes.Add(b);
    }
    public static void CompleteWorld(World3DData world, byte[] payload)
    {
        PacketReader r = new(payload, 12);
        if (world == null || r.ReadUInt32() != world.MapId || r.ReadUInt64() != world.Generation || world.Boxes.Count != world.BoxCount || world.SpawnSupport() == 0 ||
            world.SpawnX - (long)world.Radius < world.MinX || world.SpawnX + (long)world.Radius > world.MaxX ||
            world.SpawnZ - (long)world.Radius < world.MinZ || world.SpawnZ + (long)world.Radius > world.MaxZ ||
            world.SpawnY < world.MinY || world.SpawnY + (long)world.Height > world.MaxY) throw new InvalidDataException("Incomplete 3D world or invalid spawn");
        world.Boxes.Sort((a, b) => a.Id.CompareTo(b.Id));
    }
}
