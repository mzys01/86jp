namespace DfoServer.Game.Inventory
{
    // 协议 DTO 的集中映射入口；业务层不应该直接构造或修改这些 DTO。
    internal static class InventoryProtocolMapper
    {
        internal static CommonInventoryItem ToCommonItem(SqliteInventoryStore.ItemRecord record, ItemExtraView extra)
        {
            var view = extra ?? ItemExtraView.Parse(record?.ExtraJson);
            var raw = view.Raw84;
            return new CommonInventoryItem
            {
                SlotIndex = record.SlotIndex,
                ItemTemplateId = record.ItemTemplateId,
                CountOrInstanceValue = record.StackCount,
                ExtData0 = raw.Attr,
                Durability = record.Durability,
                SealFlag = record.SealFlag,
                PrefixData0E = raw.PrefixData0E,
                Marker16 = record.Marker16,
                MiddleData1A = raw.MiddleData1A,
                ExpireTime = record.ExpireTime,
                TailData2F = raw.TailData2F,
                JewelSocket = raw.JewelSocket,
                EquipmentLockId = record.EquipmentLockId,
            };
        }

        internal static AvatarInventoryItem ToAvatarItem(SqliteInventoryStore.ItemRecord record, ItemExtraView extra)
        {
            var view = extra ?? ItemExtraView.Parse(record?.ExtraJson);
            var avatar = view.Avatar;
            return new AvatarInventoryItem
            {
                SlotIndex = record.SlotIndex,
                AvatarItemId = record.ItemTemplateId,
                Reserved0 = avatar.Reserved0,
                OptionValue = record.OptionValue,
                Reserved1 = avatar.Reserved1,
                UnknownFixed30 = record.Marker16,
                Reserved2 = avatar.Reserved2,
                UnknownFixed4 = avatar.UnknownFixed4,
                TailData = avatar.TailData,
            };
        }

        internal static PetInventoryItem ToPetItem(SqliteInventoryStore.ItemRecord record, ItemExtraView extra)
        {
            var view = extra ?? ItemExtraView.Parse(record?.ExtraJson);
            return new PetInventoryItem
            {
                SlotIndex = record.SlotIndex,
                CreatureItemId = record.ItemTemplateId,
                CreatureSerialOrHandle = record.PetSerialOrHandle,
                TailData0A = view.Pet.TailData0A,
            };
        }
    }
}
