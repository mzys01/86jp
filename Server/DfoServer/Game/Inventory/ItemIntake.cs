using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    // 标准入库核: 拾取/NPC购买/点券商城共用的"定容器 -> 合堆 -> 落新行"三步。
    // 字段编码定案(docs/db-architecture-atlas.md): 非堆叠 marker_16=-1、堆叠=0;
    // 装备 stack/instance=品质种子; 宠物页行 durability=0, petSerial 宠物=新序号/耗品=数量。
    // 支付、时装、名牌、金库扩容、契约、自动开箱等特殊分支留在各调用方。
    internal static class ItemIntake
    {
        internal sealed class Placement
        {
            internal InventoryListType ListType = InventoryListType.Main;
            internal string ItemKind;
            internal int SlotStart;
            internal int SlotEnd;
            internal bool IsCreature;
            internal bool IsPetArtifact;
            internal bool IsPetConsumable;
            internal bool IsPet => IsCreature || IsPetArtifact || IsPetConsumable;
        }

        // 定容器: 宠物三类进宠物页对应槽段(list 7), 其余进主背包按物品种类槽段。
        // 时装/名牌等特殊容器由调用方在进入本核之前分流。
        internal static Placement ResolvePlacement(int itemTemplateId, ItemMetadata metadata)
        {
            var p = new Placement { ItemKind = metadata.ItemKind };
            var isPetEquipment = string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal)
                && ItemMetadataResolver.IsPetInventoryEquipment(itemTemplateId);
            p.IsCreature = isPetEquipment && SqliteInventoryStore.IsCreatureItem(itemTemplateId);
            p.IsPetArtifact = isPetEquipment && !p.IsCreature;
            p.IsPetConsumable = ItemMetadataResolver.IsPetConsumableItem(metadata);

            if (p.IsCreature)
            {
                p.SlotStart = SqliteInventoryStore.PetInventorySlotStart;
                p.SlotEnd = SqliteInventoryStore.PetInventorySlotEnd;
            }
            else if (p.IsPetArtifact)
            {
                p.SlotStart = SqliteInventoryStore.PetEquipmentSlotStart;
                p.SlotEnd = SqliteInventoryStore.PetEquipmentSlotEnd;
            }
            else if (p.IsPetConsumable)
            {
                p.SlotStart = SqliteInventoryStore.PetConsumableSlotStart;
                p.SlotEnd = SqliteInventoryStore.PetConsumableSlotEnd;
            }
            else
            {
                metadata.GetSlotRange(out p.SlotStart, out p.SlotEnd);
            }

            if (p.IsPet)
            {
                p.ListType = InventoryListType.Pet;
                p.ItemKind = "pet";
            }

            return p;
        }

        // 合堆: 已有同物品行且不超上限时并入。stackLimit<=0 = 不限。
        // 超限返回 false, 由调用方落新行(点券商城 limit=1 礼盒可重复购买的既有约定)。
        internal static bool TryMergeStack(
            InventoryDbPrimitives db,
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            Placement placement,
            int itemTemplateId,
            int count,
            int stackLimit,
            out short slotIndex,
            out int newStackCount)
        {
            slotIndex = -1;
            newStackCount = 0;

            var existing = db.FindItemByTemplateId(connection, transaction, characterId, placement.ListType, itemTemplateId);
            if (existing == null)
                return false;
            if (stackLimit > 0 && existing.StackCount + count > stackLimit)
                return false;

            newStackCount = existing.StackCount + count;
            if (placement.IsPetConsumable)
                db.UpdatePetStackCount(connection, transaction, existing.ItemUid, newStackCount);
            else
                db.UpdateStackCount(connection, transaction, existing.ItemUid, newStackCount);
            slotIndex = existing.SlotIndex;
            return true;
        }

        // 落新行。装备写品质种子; 永久物品 expire_time=0(与掉落路径编码一致)。
        internal static bool TryInsertNewRow(
            InventoryDbPrimitives db,
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            Placement placement,
            int itemTemplateId,
            ItemMetadata metadata,
            int count,
            out short slotIndex,
            out int instanceValue,
            out ushort durability)
        {
            slotIndex = -1;
            instanceValue = 0;
            durability = 0;

            var slot = db.FindEmptySlot(connection, transaction, characterId, placement.ListType, placement.SlotStart, placement.SlotEnd);
            if (slot < 0)
                return false;

            var qualitySeed = placement.IsPet ? 0 : InventoryDbPrimitives.GenerateInstanceValue(itemTemplateId, slot);
            var stackCount = placement.IsCreature || placement.IsPetArtifact
                ? 0
                : metadata.IsStackable ? count : qualitySeed;
            instanceValue = metadata.IsStackable ? count : qualitySeed;
            durability = placement.IsPet ? (ushort)0 : metadata.Durability;
            var petSerial = placement.IsPetConsumable
                ? count
                : placement.IsCreature ? db.NextPetSerialOrHandle(connection, transaction, characterId) : 0;

            db.InsertCharacterItemRecord(connection, transaction, characterId, new SqliteInventoryStore.ItemRecord
            {
                ListType = placement.ListType,
                SlotIndex = (short)slot,
                ItemTemplateId = itemTemplateId,
                ItemKind = placement.ItemKind,
                StackCount = stackCount,
                InstanceValue = instanceValue,
                Durability = durability,
                SealFlag = metadata.IsSealed ? (byte)1 : (byte)0,
                OptionValue = 0,
                ExpireTime = 0,                                                  // 永久=0
                Marker16 = metadata.IsStackable || placement.IsPet ? 0 : -1,     // 非堆叠=-1
                PetSerialOrHandle = petSerial,
                ExtraJson = "{}",
            });
            slotIndex = (short)slot;
            return true;
        }
    }
}
