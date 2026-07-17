using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Game.KnightShield
{
    public static class KnightShieldEquipmentSnapshotSynchronizer
    {
        public const byte SupportWeaponSlot = (byte)EquipmentType.SupportWeapon;

        public static void Apply(
            byte job,
            int growType,
            UserInfoAdditionSnapshot addition,
            KnightShieldDeckSnapshot deck)
        {
            if (addition == null
                || deck == null
                || !KnightShieldDataProvider.IsEligibleCharacter(job))
                return;

            // deck 是守护者 slot23 的唯一权威来源，先删除普通装备表中的陈旧条目。
            addition.EquippedEntries.RemoveAll(entry => entry.Slot == SupportWeaponSlot);

            var shieldItemId = deck.MainShieldItemId;
            if (shieldItemId == 0)
                return;
            if (!KnightShieldDataProvider.IsCatalogShield(growType, shieldItemId))
            {
                FileLogger.Log(
                    $"[KnightShield] skip invalid persisted main shield in subtype1: "
                    + $"item={shieldItemId} grow={growType}");
                return;
            }

            // 86JP 0x00D2B060 先读 equipmentCount，再按完整 InvenItem 装入条目自带的 slot。
            // slot23 无装扮/宠物变长块，默认结构固定 43B，与原生 list33 装备路径一致。
            var item = new InvenItem
            {
                Slot = SupportWeaponSlot,
                ItemId = shieldItemId,
            };
            addition.EquippedEntries.Add(new EquippedEntrySnapshot
            {
                Slot = SupportWeaponSlot,
                ItemId = shieldItemId,
                RawEntry = item.ToBytes(),
                Item = item,
            });
            addition.EquippedEntries.Sort((left, right) => left.Slot.CompareTo(right.Slot));
        }
    }
}
