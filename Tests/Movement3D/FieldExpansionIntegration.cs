using System.Diagnostics;

internal static class FieldExpansionIntegration
{
    internal static async Task<int> RecoveryAsync(string marker)
    {
        using var owner = new FieldServerIntegration.Peer(); await owner.ConnectAsync("test", 1002);
        ushort initialHp = owner.Inventory.Hp; ulong initialRevision = owner.Inventory.Revision;
        await owner.PumpAsync(() => owner.CombatPaused);
        var generation = owner.World.Generation;
        await owner.SendAsync(GamePacketOpcode.BasicAttackRequest3D, CombatProtocol.Request(owner.World.MapId, generation, 999, 0));
        await owner.PumpAsync(() => owner.Attack?.Sequence == 999);
        Program.Check(owner.Attack.Result == BasicAttackResult.Unavailable, "Combat resumed before DB recovery");
        owner.ExpectMapReject = true;
        await owner.SendAsync(GamePacketOpcode.ChangeMapRequest, GameProtocol.CreateChangeMapRequest(100000003));
        await owner.PumpAsync(() => owner.LastMapResult != null);
        Program.Check(owner.LastMapResult == ChangeMapResult.MapEnterFailed && owner.World.Generation == generation, "Map transfer bypassed paused combat");
        owner.ExpectMapReject = false;
        File.WriteAllText(marker, "paused");
        await owner.PumpAsync(() => owner.SawCombatPause && !owner.CombatPaused && owner.Inventory.Hp < initialHp);
        Program.Check(owner.Inventory.Hp == initialHp - 10 && owner.Inventory.Revision == initialRevision + 1 && owner.World.Generation == generation,
            "Recovered monster damage lost/doubled or reset map");
        Console.WriteLine("[PASS] Actual DB failure pauses combat; same damage recovers once with exact HP/revision and resumes without reconnect");
        return 0;
    }
    private static byte[] Channel(ushort id) { using PacketWriter w=new(); w.Write(id); return w.ToArray(); }
    private static async Task PumpFor(FieldServerIntegration.Peer peer,int milliseconds)
    { var watch=Stopwatch.StartNew(); while (watch.ElapsedMilliseconds<milliseconds) await peer.PumpAsync(()=>true); }
    private static async Task<InventoryData> Operation(FieldServerIntegration.Peer peer,InventoryOperation op,byte category=0,byte source=0,byte target=0)
    {
        for (int attempt=0;attempt<8;++attempt)
        {
            var data=await peer.OperationAsync(op,category,source,target);
            if (data.Result!=InventoryResult.RateLimited && data.Result!=InventoryResult.StaleRevision) return data;
            await Task.Delay(200);
        }
        throw new InvalidDataException("Field operation stayed busy");
    }
    internal static async Task<int> RunAsync(bool death)
    {
        using var owner=new FieldServerIntegration.Peer(); await owner.ConnectAsync("test",1002);
        var initial=await Operation(owner,InventoryOperation.Refresh);
        if (death)
        {
            if (owner.Inventory.Hp!=0) await owner.PumpAsync(()=>owner.Inventory.Hp==0);
            await owner.SendAsync(GamePacketOpcode.BasicAttackRequest3D,CombatProtocol.Request(owner.World.MapId,owner.World.Generation,999,0));
            await owner.PumpAsync(()=>owner.Attack?.Sequence==999);
            Program.Check(owner.Attack.Result==BasicAttackResult.Unavailable,"Dead player attacked");
            await owner.SendAsync(GamePacketOpcode.ReviveRequest,Array.Empty<byte>());
            var before=owner.Inventory;
            await owner.PumpAsync(()=>owner.Inventory!=before);
            Program.Check(owner.Inventory.Hp==0 && owner.Inventory.Result==InventoryResult.CannotUse,"Early revive bypassed death wait");
            await Task.Delay(3300); ulong generation=owner.World.Generation;
            await owner.SendAsync(GamePacketOpcode.ReviveRequest,Array.Empty<byte>());
            await owner.PumpAsync(()=>owner.World.Generation!=generation && owner.Inventory.Hp==owner.Inventory.MaxHp);
            Console.WriteLine("[PASS] Actual server monster damage, death attack rejection, revive delay and new-generation respawn"); return 0;
        }
        using var observer=new FieldServerIntegration.Peer(); await observer.ConnectAsync("test2",2001); await Operation(observer,InventoryOperation.Refresh);
        await owner.SendAsync(GamePacketOpcode.ChangeChannelRequest,Channel(99)); await owner.PumpAsync(()=>owner.Channels?.Result==ChannelResult.InvalidChannel);
        ulong previous=owner.World.Generation; double x=owner.World.SpawnX,z=owner.World.SpawnZ;
        await owner.SendAsync(GamePacketOpcode.ChangeChannelRequest,Channel(2));
        await owner.PumpAsync(()=>owner.Channels?.Current==2 && owner.World.Generation!=previous && owner.Monsters!=null);
        Program.Check(owner.World.SpawnX==x && owner.World.SpawnZ==z,"Channel transfer moved field position");
        await observer.PumpAsync(()=>!observer.Remotes.Contains(1002));
        await owner.SendAsync(GamePacketOpcode.ChangeChannelRequest,Channel(3)); await owner.PumpAsync(()=>owner.Channels.Result==ChannelResult.Cooldown);
        int chats=observer.ChatCount;
        await owner.SendAsync(GamePacketOpcode.ChatRequest,GameProtocol.CreateChatRequest("channel isolation fixture"));
        await owner.PumpAsync(()=>owner.ChatCount>0); await PumpFor(observer,700);
        Program.Check(observer.ChatCount==chats,"Field chat crossed channels");
        await owner.ChangeMapAsync(100000003); await owner.SendAsync(GamePacketOpcode.ChannelListRequest,Array.Empty<byte>());
        await owner.PumpAsync(()=>owner.Channels.Result==ChannelResult.Success); Program.Check(owner.Channels.Current==2,"Map change returned to channel 1");
        await owner.ChangeMapAsync(100000002);
        Console.WriteLine("[PASS] Actual channels reject invalid IDs/cooldown, preserve position, isolate peers/chat and retain channel on map transfer");
        var worn=await Operation(owner,InventoryOperation.Equip,0,0,0);
        Program.Check(worn.Result==InventoryResult.Success && worn.Equipped[0].InstanceId==initial.Entries[0].InstanceId,"Actual equip lost owned instance");
        await observer.SendAsync(GamePacketOpcode.ChangeChannelRequest,Channel(2)); previous=observer.World.Generation;
        await observer.PumpAsync(()=>observer.Channels?.Current==2 && observer.World.Generation!=previous && observer.Monsters!=null);
        for (ulong sequence=1;sequence<=3;++sequence)
        {
            await owner.SendAsync(GamePacketOpcode.BasicAttackRequest3D,CombatProtocol.Request(owner.World.MapId,owner.World.Generation,sequence,0));
            await owner.PumpAsync(()=>owner.Attack?.Sequence==sequence);
            Program.Check(owner.Attack.Result==(sequence==3 ? BasicAttackResult.Killed : BasicAttackResult.Hit),"Actual basic attack missed fixture");
            Program.Check(owner.Attack.Damage==(sequence==3 ? 10 : 25),"Equipped weapon damage incorrect");
            await PumpFor(owner,550);
        }
        await owner.PumpAsync(()=>owner.Drops?.Drops.Count>0); await observer.PumpAsync(()=>observer.Drops?.Drops.Count>0 && observer.Attack?.Sequence==3);
        ulong drop=owner.Drops.Drops[0].Id; int ownQuantity=owner.Inventory.Entries[24].Quantity,peerQuantity=observer.Inventory.Entries[24].Quantity;
        await owner.SendAsync(GamePacketOpcode.PickupRequest3D,FieldExtensions.Pickup(owner.World.MapId,owner.World.Generation,1,drop));
        await observer.SendAsync(GamePacketOpcode.PickupRequest3D,FieldExtensions.Pickup(observer.World.MapId,observer.World.Generation,1,drop));
        await owner.PumpAsync(()=>owner.PickupResult!=null); await observer.PumpAsync(()=>observer.PickupResult!=null);
        Program.Check((owner.PickupResult==PickupResult.Success)!=(observer.PickupResult==PickupResult.Success),"Actual drop granted to both peers");
        await Operation(owner,InventoryOperation.Refresh); await Operation(observer,InventoryOperation.Refresh);
        Program.Check(owner.Inventory.Entries[24].Quantity+observer.Inventory.Entries[24].Quantity==ownQuantity+peerQuantity+1,"Actual drop quantity changed more than once");
        var acquired = owner.PickupResult == PickupResult.Success ? owner : observer;
        var other = ReferenceEquals(acquired, owner) ? observer : owner;
        await QueryPickup(acquired, drop); await QueryPickup(other, drop);
        Program.Check(acquired.PickupResult == PickupResult.Success && other.PickupResult == PickupResult.AlreadyTaken, "Actual lost pickup result query misidentified owner");
        await QueryPickup(acquired, ulong.MaxValue);
        Program.Check(acquired.PickupResult == PickupResult.NoRecord, "Actual missing pickup history was presented as a successful grant");
        await Operation(owner,InventoryOperation.Refresh); await Operation(observer,InventoryOperation.Refresh);
        Program.Check(owner.Inventory.Entries[24].Quantity+observer.Inventory.Entries[24].Quantity==ownQuantity+peerQuantity+1, "Result queries repeated the award");
        Console.WriteLine("[PASS] Actual pickup result queries recover owner/other/no record without repeating awards");
        var removed=await Operation(owner,InventoryOperation.Unequip,0,0,0);
        Program.Check(removed.Result==InventoryResult.Success && removed.Entries[0].InstanceId==initial.Entries[0].InstanceId,"Actual unequip lost identity");
        Console.WriteLine("[PASS] Actual equipped damage, shared death/drop and two-client single grant, with stable equipment identity");
        return 0;
    }
    private static async Task QueryPickup(FieldServerIntegration.Peer peer, ulong drop)
    {
        using PacketWriter payload = new(); payload.Write(drop);
        for (int attempt = 0; attempt < 5; ++attempt)
        {
            // 인벤토리 조회 직후의 100ms 제한·진행 중 피격은 지급 재시도가 아닌 결과 조회로 기다린다.
            await Task.Delay(120); peer.PickupResult = null;
            await peer.SendAsync(GamePacketOpcode.PickupStatusRequest3D, payload.ToArray());
            await peer.PumpAsync(() => peer.PickupResult != null);
            if (peer.PickupResult != PickupResult.Busy) return;
        }
        throw new InvalidDataException("Pickup result lookup remained busy");
    }
}
