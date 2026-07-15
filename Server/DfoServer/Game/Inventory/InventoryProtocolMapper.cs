namespace DfoServer.Game.Inventory
{
    // 协议 DTO 的集中映射入口；业务层不应该直接构造或修改这些 DTO。
    internal static class InventoryProtocolMapper
    {
        internal static CommonInventoryItem ToCommonItem(SqliteInventoryStore.ItemRecord record)
        {
            var view = InventoryItemView.ForCommon(record);
            var entry = view.Entry84;
            return new CommonInventoryItem
            {
                SlotIndex = entry.SlotIndex,
                ItemTemplateId = entry.ItemTemplateId,
                CountOrInstanceValue = entry.Value,
                ExtData0 = entry.Attr,
                Durability = entry.Durability,
                SealFlag = entry.SealFlag,
                PrefixData0E = entry.PrefixData0E,
                Marker16 = entry.Marker16,
                MiddleData1A = entry.MiddleData1A,
                ExpireTime = entry.ExpireTime,
                TailData2F = entry.TailData2F,
                JewelSocket = entry.JewelSocket,
                EquipmentLockId = record.EquipmentLockId,
            };
        }

        internal static AvatarInventoryItem ToAvatarItem(SqliteInventoryStore.ItemRecord record)
        {
            var view = InventoryItemView.ForAvatar(record);
            var entry = view.Entry84;
            var detail = view.AvatarDetail;
            return new AvatarInventoryItem
            {
                SlotIndex = entry.SlotIndex,
                AvatarItemId = entry.ItemTemplateId,
                RemainingSeconds = entry.Value,
                Attr = entry.Attr,
                AbilityNo = entry.AbilityNo,
                SealFlag = entry.SealFlag,
                PrefixData0E = entry.PrefixData0E,
                Marker16 = entry.Marker16,
                MiddleData1A = entry.MiddleData1A,
                ExpireTime = entry.ExpireTime,
                TailData2F = entry.TailData2F,
                AvatarSocketData = detail.AvatarSocketData,
                ColorDataLen = detail.ColorDataLen,
                Color1 = detail.Color1,
                Color2 = detail.Color2,
            };
        }

        internal static PetInventoryItem ToPetItem(SqliteInventoryStore.ItemRecord record)
        {
            var view = InventoryItemView.ForPet(record);
            var entry = view.Entry84;
            return new PetInventoryItem
            {
                SlotIndex = entry.SlotIndex,
                CreatureItemId = entry.ItemTemplateId,
                CreatureSerialOrHandle = entry.Value,
                Attr = entry.Attr,
                Durability = entry.Durability,
                SealFlag = entry.SealFlag,
                PrefixData0E = entry.PrefixData0E,
                Marker16 = entry.Marker16,
                MiddleData1A = entry.MiddleData1A,
                ExpireTime = entry.ExpireTime,
                TailData2F = entry.TailData2F,
            };
        }
    }
}
