internal static class TradeTests
{
    public static void Run()
    {
        var payload=TradeProtocol.Request(TradeOperation.Offer,17,3,0,6,500,new[]{new TradeSlotData { Category=1,Slot=2,Quantity=3 }});
        Program.Check(payload.Length==62,"Trade request size mismatch");
        var reader=new PacketReader(payload,62);
        Program.Check(reader.ReadByte()==2 && reader.ReadUInt64()==17 && reader.ReadUInt64()==3 && reader.ReadUInt32()==0 && reader.ReadUInt64()==6 && reader.ReadUInt64()==500 && reader.ReadByte()==1,"Trade request layout mismatch");
        using PacketWriter writer=new(); writer.Write(17UL); writer.Write(3UL); writer.Write((byte)TradeStage.Editing); writer.Write((byte)TradeResult.Success);
        writer.Write(1002u); writer.Write(2001u); writer.Write((byte)1); writer.Write((byte)0);
        for(int side=0;side<2;++side)
        {
            writer.Write(6UL); writer.Write(500UL); writer.Write((byte)(side==0?1:0));
            for(int i=0;i<6;++i) { writer.Write((byte)1); writer.Write((byte)i); writer.Write((ushort)3); writer.Write(101u); writer.Write(0UL); }
        }
        var bytes=writer.ToArray(); var state=TradeProtocol.Read(bytes);
        Program.Check(bytes.Length==254 && state.Side(2001)==1 && state.Confirmed[0] && state.Offers[0].Items[0].Source.Quantity==3,"Trade response layout mismatch");
        bytes[44]=7;
        bool rejected=false; try { TradeProtocol.Read(bytes); } catch(InvalidDataException) { rejected=true; }
        Program.Check(rejected,"Oversized trade item count accepted");
        Console.WriteLine("[PASS] Trade wire layouts, participant identity and bounded item count");
    }
}
