using System;
using System.IO;
using System.Collections.Generic;

public enum TradeOperation : byte { Invite, Accept, Offer, Confirm, Cancel, Query }
public enum TradeStage : byte { Invited, Editing, Committing, Completed, Cancelled }
public enum TradeResult : byte { Success, Invalid, Busy, Unavailable, Stale, Full, StorageError, Expired }
public struct TradeSlotData { public byte Category, Slot; public ushort Quantity; }
public struct TradeItemData { public TradeSlotData Source; public uint ItemId; public ulong InstanceId; }
public sealed class TradeOfferData { public ulong InventoryRevision, Mesos; public readonly List<TradeItemData> Items = new(); }
public sealed class TradeStateData
{
    public ulong Id, Revision; public TradeStage Stage; public TradeResult Result;
    public uint[] Characters = new uint[2]; public bool[] Confirmed = new bool[2];
    public TradeOfferData[] Offers = {new(),new()};
    public int Side(uint character) => Characters[0]==character ? 0 : Characters[1]==character ? 1 : -1;
}
public static class TradeProtocol
{
    public const int RequestSize=62, StateSize=254, MaxItems=6;
    public static byte[] Request(TradeOperation operation,ulong id,ulong revision,uint target=0,ulong inventoryRevision=0,ulong mesos=0,IReadOnlyList<TradeSlotData> slots=null)
    {
        if(operation>TradeOperation.Query || (slots?.Count??0)>MaxItems) throw new ArgumentException("Invalid trade request.");
        using PacketWriter writer=new(); writer.Write((byte)operation); writer.Write(id); writer.Write(revision); writer.Write(target); writer.Write(inventoryRevision); writer.Write(mesos);
        writer.Write((byte)(slots?.Count??0));
        for(int i=0;i<MaxItems;++i) { var slot=slots!=null && i<slots.Count ? slots[i] : default; writer.Write(slot.Category); writer.Write(slot.Slot); writer.Write(slot.Quantity); }
        return writer.ToArray();
    }
    public static TradeStateData Read(byte[] payload)
    {
        var reader=new PacketReader(payload,StateSize); TradeStateData state=new() { Id=reader.ReadUInt64(),Revision=reader.ReadUInt64(),Stage=(TradeStage)reader.ReadByte(),Result=(TradeResult)reader.ReadByte() };
        if(state.Stage>TradeStage.Cancelled || state.Result>TradeResult.Expired) throw new InvalidDataException("Invalid trade state.");
        for(int i=0;i<2;++i) state.Characters[i]=reader.ReadUInt32();
        for(int i=0;i<2;++i) { byte value=reader.ReadByte(); if(value>1) throw new InvalidDataException("Invalid trade confirmation."); state.Confirmed[i]=value==1; }
        for(int side=0;side<2;++side)
        {
            var offer=state.Offers[side]; offer.InventoryRevision=reader.ReadUInt64(); offer.Mesos=reader.ReadUInt64(); int count=reader.ReadByte();
            if(count>MaxItems) throw new InvalidDataException("Invalid trade item count.");
            var used=new HashSet<int>();
            for(int i=0;i<MaxItems;++i)
            {
                TradeItemData item=new() { Source=new() { Category=reader.ReadByte(),Slot=reader.ReadByte(),Quantity=reader.ReadUInt16() },ItemId=reader.ReadUInt32(),InstanceId=reader.ReadUInt64() };
                if(i>=count) continue;
                if(item.Source.Category>=5 || item.Source.Slot>=24 || item.Source.Quantity==0 || item.ItemId==0 || !used.Add(item.Source.Category*24+item.Source.Slot)) throw new InvalidDataException("Invalid trade item.");
                offer.Items.Add(item);
            }
        }
        return state;
    }
}
