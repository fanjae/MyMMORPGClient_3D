using System;
using System.Collections.Generic;
using System.IO;

public enum ChannelResult : byte { Success, InvalidChannel, Busy, Cooldown, Unavailable }
public sealed class ChannelData
{
    public ushort Current; public ChannelResult Result; public readonly List<(ushort Id,uint Players)> Channels=new();
}
public sealed class VitalsData
{
    public uint MapId,CharacterId; public ulong Generation,Revision; public ushort Hp,MaxHp,Mp,MaxMp,Damage;
    public bool Paused;
}
public struct DropData { public ulong Id; public uint ItemId; public ushort Quantity; public double X,Y,Z; }
public sealed class DropsData
{
    public uint MapId; public ulong Generation,Revision; public readonly List<DropData> Drops=new();
}
public enum PickupResult : byte { Success, NotFound, OutOfRange, Busy, Full, AlreadyTaken, StorageError, NoRecord }
public static class FieldExtensions
{
    public static ChannelData ReadChannels(byte[] payload)
    {
        if (payload==null || payload.Length<5 || (payload.Length-5)%6!=0) throw new InvalidDataException("Invalid channels size");
        PacketReader reader=new(payload,payload.Length); var data=new ChannelData { Current=reader.ReadUInt16() };
        int count=reader.ReadUInt16(); data.Result=(ChannelResult)reader.ReadByte(); HashSet<ushort> ids=new();
        if (count!=3 || payload.Length!=5+count*6 || data.Current<1 || data.Current>3 || data.Result>ChannelResult.Unavailable) throw new InvalidDataException("Invalid channels");
        for (int i=0;i<count;++i) { ushort id=reader.ReadUInt16(); uint players=reader.ReadUInt32(); if (id<1 || id>3 || !ids.Add(id)) throw new InvalidDataException("Invalid channel ID"); data.Channels.Add((id,players)); }
        return data;
    }
    public static VitalsData ReadVitals(byte[] payload)
    {
        PacketReader r=new(payload,35); var d=new VitalsData { MapId=r.ReadUInt32(),Generation=r.ReadUInt64(),CharacterId=r.ReadUInt32(),Revision=r.ReadUInt64(),
            Hp=r.ReadUInt16(),MaxHp=r.ReadUInt16(),Mp=r.ReadUInt16(),MaxMp=r.ReadUInt16(),Damage=r.ReadUInt16() };
        byte paused = r.ReadByte(); if (paused > 1) throw new InvalidDataException("Invalid combat state"); d.Paused = paused != 0;
        if (d.CharacterId==0 || d.Revision==0 || d.MaxHp==0 || d.Hp>d.MaxHp || d.Mp>d.MaxMp) throw new InvalidDataException("Invalid vitals"); return d;
    }
    public static DropsData ReadDrops(byte[] payload)
    {
        if (payload==null || payload.Length<22 || (payload.Length-22)%26!=0) throw new InvalidDataException("Invalid drops size");
        PacketReader r=new(payload,payload.Length); var d=new DropsData { MapId=r.ReadUInt32(),Generation=r.ReadUInt64(),Revision=r.ReadUInt64() }; int count=r.ReadUInt16();
        if (count>64 || payload.Length!=22+count*26 || d.Revision==0) throw new InvalidDataException("Invalid drop count"); HashSet<ulong> ids=new();
        for (int i=0;i<count;++i)
        {
            var drop=new DropData { Id=r.ReadUInt64(),ItemId=r.ReadUInt32(),Quantity=r.ReadUInt16(),X=r.ReadInt32()/1000.0,Y=r.ReadInt32()/1000.0,Z=r.ReadInt32()/1000.0 };
            if (drop.Id==0 || !ids.Add(drop.Id) || drop.Quantity==0 || drop.Quantity>100 || (drop.ItemId!=101 && drop.ItemId!=102 && drop.ItemId!=301 && drop.ItemId!=302)) throw new InvalidDataException("Invalid drop"); d.Drops.Add(drop);
        }
        return d;
    }
    public static byte[] Pickup(uint map,ulong generation,ulong sequence,ulong drop)
    { using PacketWriter w=new(); w.Write(map); w.Write(generation); w.Write(sequence); w.Write(drop); return w.ToArray(); }
    public static (ulong Id,PickupResult Result) ReadPickup(byte[] payload)
    { PacketReader r=new(payload,9); ulong id=r.ReadUInt64(); var result=(PickupResult)r.ReadByte(); if (result>PickupResult.NoRecord) throw new InvalidDataException("Invalid pickup result"); return (id,result); }
}
