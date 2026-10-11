// 샘플 아이템 표시 정보와 수량 검증 기준. 번호·분류·중첩 한도는 서버 정의와 맞춘다.
public static class ItemCatalog
{
    public static string Name(uint id) => id switch
    {
        1 => "Wooden Sword", 2 => "Cloth Cap", 101 => "Red Potion", 102 => "Blue Potion", 201 => "Wooden Chair",
        301 => "Copper Ore", 302 => "Soft Feather", 401 => "Lucky Charm", _ => "Empty slot"
    };
    public static string Description(uint id) => id switch
    {
        1 => "A light practice sword.\nEquipment / Max stack: 1",
        2 => "A comfortable beginner's cap.\nEquipment / Max stack: 1",
        101 => "Restores 30 HP.\nDouble-click or select Use.\nCannot use at full HP.",
        102 => "Restores 20 MP.\nDouble-click or select Use.\nCannot use at full MP.",
        201 => "A simple wooden chair.\nSetup / Max stack: 1",
        301 => "Material for future crafting.\nEtc / Max stack: 200",
        302 => "A soft crafting material.\nEtc / Max stack: 200",
        401 => "A small decorative charm.\nCash / Max stack: 1", _ => "Select an item to inspect it."
    };
    public static ushort StackLimit(uint id, int category) => id switch
    {
        1 or 2 => (ushort)(category == 0 ? 1 : 0), 101 or 102 => (ushort)(category == 1 ? 100 : 0),
        201 => (ushort)(category == 2 ? 1 : 0), 301 or 302 => (ushort)(category == 3 ? 200 : 0),
        401 => (ushort)(category == 4 ? 1 : 0), _ => 0
    };
    // 오프라인 미리보기만 사용할 기본 아이템이며 온라인 상태는 서버 응답에서 읽는다.
    public static InventoryData Preview()
    {
        InventoryData data = new() { Revision = 1, Mesos = 1250, Hp = 50, MaxHp = 100, Mp = 25, MaxMp = 60 };
        data.Entries[0] = new() { ItemId = 1, Quantity = 1 }; data.Entries[1] = new() { ItemId = 2, Quantity = 1 };
        data.Entries[24] = new() { ItemId = 101, Quantity = 10 }; data.Entries[25] = new() { ItemId = 102, Quantity = 8 };
        data.Entries[28] = new() { ItemId = 101, Quantity = 5 }; data.Entries[48] = new() { ItemId = 201, Quantity = 1 };
        data.Entries[72] = new() { ItemId = 301, Quantity = 25 }; data.Entries[73] = new() { ItemId = 302, Quantity = 12 };
        data.Entries[96] = new() { ItemId = 401, Quantity = 1 }; return data;
    }
}
