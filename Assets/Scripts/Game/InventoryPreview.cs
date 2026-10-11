using System;
using System.Collections.Generic;
public static class InventoryPreview
{
    // 오프라인 미리보기 전용 규칙이며 온라인 변경은 항상 서버에 요청한다.
    public static void Apply(InventoryData data, InventoryOperation operation, int category, int source, int target)
    {
        data.Result = InventoryResult.Success;
        int offset = category * 24;
        if (operation == InventoryOperation.Move)
        {
            var from = data.Entries[offset + source]; var to = data.Entries[offset + target];
            if (source == target || from.ItemId == 0) { data.Result = InventoryResult.InvalidSlot; return; }
            if (from.ItemId == to.ItemId && ItemCatalog.StackLimit(from.ItemId, category) > 1)
            {
                ushort amount = (ushort)Math.Min(from.Quantity, ItemCatalog.StackLimit(from.ItemId, category) - to.Quantity);
                from.Quantity -= amount; to.Quantity += amount;
                data.Entries[offset + source] = from.Quantity == 0 ? default : from; data.Entries[offset + target] = to;
            }
            else { data.Entries[offset + source] = to; data.Entries[offset + target] = from; }
        }
        else if (operation == InventoryOperation.Sort)
        {
            List<InventoryEntry> entries = new(); for (int i = 0; i < 24; ++i) if (data.Entries[offset + i].ItemId != 0) entries.Add(data.Entries[offset + i]);
            entries.Sort((a, b) => a.ItemId.CompareTo(b.ItemId)); Array.Clear(data.Entries, offset, 24); int destination = 0;
            foreach (var current in entries)
            {
                var item = current;
                if (destination > 0 && data.Entries[offset + destination - 1].ItemId == item.ItemId)
                {
                    var previous = data.Entries[offset + destination - 1]; ushort amount = (ushort)Math.Min(item.Quantity, ItemCatalog.StackLimit(item.ItemId, category) - previous.Quantity);
                    previous.Quantity += amount; item.Quantity -= amount; data.Entries[offset + destination - 1] = previous;
                }
                if (item.Quantity > 0) data.Entries[offset + destination++] = item;
            }
        }
        else if (operation == InventoryOperation.Use)
        {
            var item = data.Entries[offset + source];
            if (category != 1 || item.Quantity == 0 || !((item.ItemId == 101 && data.Hp < data.MaxHp) || (item.ItemId == 102 && data.Mp < data.MaxMp)))
            { data.Result = InventoryResult.CannotUse; return; }
            if (item.ItemId == 101) data.Hp = (ushort)Math.Min(data.MaxHp, data.Hp + 30); else data.Mp = (ushort)Math.Min(data.MaxMp, data.Mp + 20);
            --item.Quantity; data.Entries[offset + source] = item.Quantity == 0 ? default : item;
        }
        if (operation != InventoryOperation.Refresh) ++data.Revision;
    }
}
