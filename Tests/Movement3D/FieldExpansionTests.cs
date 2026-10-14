internal static class FieldExpansionTests
{
    internal static void Run()
    {
        var data=ItemCatalog.Preview(); ulong sword=data.Entries[0].InstanceId;
        InventoryPreview.Apply(data,InventoryOperation.Equip,0,0,0);
        Program.Check(data.Equipped[0].InstanceId==sword && data.Entries[0].ItemId==0,"Preview equipment identity lost");
        InventoryPreview.Apply(data,InventoryOperation.Unequip,0,0,1);
        Program.Check(data.Result==InventoryResult.Full && data.Equipped[0].InstanceId==sword,"Preview unequip overwrote cap");
        InventoryPreview.Apply(data,InventoryOperation.Unequip,0,0,0);
        Program.Check(data.Entries[0].InstanceId==sword && data.Equipped[0].ItemId==0,"Preview unequip identity lost");
        data.Hp=0; InventoryPreview.Apply(data,InventoryOperation.Use,1,0,0);
        Program.Check(data.Hp==0 && data.Entries[24].Quantity==10,"Potion revived dead preview");
        Console.WriteLine("[PASS] Equipment preview preserves instance/slot identity and death restrictions");
        using PacketWriter channels=new(); channels.Write((ushort)2); channels.Write((ushort)3); channels.Write((byte)ChannelResult.Success);
        for (ushort i=1;i<=3;++i) { channels.Write(i); channels.Write((uint)(i*2)); }
        Program.Check(FieldExtensions.ReadChannels(channels.ToArray()).Channels[1].Players==4 && channels.ToArray().Length==23,"Channel record layout wrong");
        using PacketWriter vitals=new(); vitals.Write((uint)1); vitals.Write((ulong)2); vitals.Write((uint)1002); vitals.Write((ulong)7);
        foreach (ushort value in new ushort[]{40,100,25,60,10}) vitals.Write(value);
        vitals.Write((byte)0);
        Program.Check(vitals.ToArray().Length==35 && FieldExtensions.ReadVitals(vitals.ToArray()).Damage==10,"Compact vitals layout wrong");
        using PacketWriter drops=new(); drops.Write((uint)1); drops.Write((ulong)2); drops.Write((ulong)3); drops.Write((ushort)1);
        drops.Write((ulong)55); drops.Write((uint)101); drops.Write((ushort)1); drops.Write(1000); drops.Write(0); drops.Write(-1000);
        Program.Check(FieldExtensions.ReadDrops(drops.ToArray()).Drops[0].Z==-1 && FieldExtensions.Pickup(1,2,3,55).Length==28,"Drop/pickup layout wrong");
        bool malformed=false; try { FieldExtensions.ReadDrops(drops.ToArray()[..^1]); } catch (InvalidDataException) { malformed=true; }
        Program.Check(malformed,"Truncated drop record accepted");
        Console.WriteLine("[PASS] Channels, compact vitals, drop and pickup wire layouts reject truncated state");
        CheckMonsterParts();
        var recovery = new PickupRecovery3D(); recovery.Begin(55, 0);
        Program.Check(!recovery.TryQuery(4.9) && recovery.TryQuery(5), "Lost pickup did not wait before querying");
        Program.Check(!recovery.Accept(99, PickupResult.Success) && !recovery.Accept(55, PickupResult.StorageError) && recovery.Pending, "Unrelated/error pickup cleared recovery");
        Program.Check(recovery.TryQuery(10) && recovery.TryQuery(15) && !recovery.TryQuery(20), "Pickup query retries were not bounded");
        Program.Check(!recovery.NeedsManualQuery(19.9) && recovery.NeedsManualQuery(20), "Exhausted pickup queries did not expose manual retry after the last response deadline");
        recovery.Restore(55, 21); Program.Check(recovery.TryQuery(21) && recovery.Accept(55, PickupResult.NoRecord) && !recovery.Pending, "Reconnected pickup result did not resolve");
        Console.WriteLine("[PASS] Lost pickup uses bounded result queries, preserves ambiguous errors and resolves after reconnect");
    }
    private static byte[] Part(ushort part, ulong tick = 8, bool duplicateId = false)
    {
        using PacketWriter writer = new(); writer.Write((uint)1); writer.Write((ulong)2); writer.Write(tick);
        writer.Write((ushort)(part == 0 ? 32 : 1)); writer.Write(part); writer.Write((ushort)2); writer.Write((ushort)33);
        for (int i = 0; i < (part == 0 ? 32 : 1); ++i)
        {
            writer.Write((uint)(duplicateId ? 1 : part * 32 + i + 1)); writer.Write((byte)0);
            writer.Write(i * 1000); writer.Write(0); writer.Write(0); writer.Write((ushort)0);
            writer.Write((ushort)60); writer.Write((ushort)60); writer.Write((ulong)1);
        }
        return writer.ToArray();
    }
    private static void CheckMonsterParts()
    {
        var assembler = new FieldMonsterAssembler();
        Program.Check(assembler.Accept(FieldProtocol.ReadMonsters(Part(1))) == null, "Partial list replaced visible monsters");
        var assembled = assembler.Accept(FieldProtocol.ReadMonsters(Part(0)));
        Program.Check(assembled.Monsters.Count == 33 && assembled.Monsters[32].Id == 33, "Out-of-order parts were not assembled");
        Program.Check(assembler.Accept(FieldProtocol.ReadMonsters(Part(0, 7))) == null, "Old split snapshot was accepted");
        assembler.Clear(); assembler.Accept(FieldProtocol.ReadMonsters(Part(0)));
        Program.Check(assembler.Accept(FieldProtocol.ReadMonsters(Part(1, 9))) == null, "Parts from different ticks were combined");
        assembler.Accept(FieldProtocol.ReadMonsters(Part(0, 9)));
        assembler.Clear(); assembler.Accept(FieldProtocol.ReadMonsters(Part(0)));
        bool rejected = false;
        try { assembler.Accept(FieldProtocol.ReadMonsters(Part(1, 8, true))); } catch (InvalidDataException) { rejected = true; }
        Program.Check(rejected, "Repeated monster ID across parts accepted");
        Console.WriteLine("[PASS] Monster parts assemble atomically, tolerate ordering and reject old ticks/cross-part duplicate IDs");
    }
}
