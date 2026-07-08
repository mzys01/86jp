using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Globalization;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        private ItemRecord NormalizePetInventoryEquipmentSourceFallback(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ItemRecord item)
        {
            if (item == null)
                return null;

            TryNormalizePetInventoryEquipmentFallback(connection, transaction, characterId, item);
            return item;
        }

        private bool TryNormalizePetInventoryEquipmentFallback(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ItemRecord item)
        {
            if (!NeedsPetInventoryEquipmentFallback(item))
                return false;

            if (!TryGetPetVisibleSlotRange(item.ItemTemplateId, out var slotStart, out var slotEnd))
                return false;

            var targetSlot = item.SlotIndex;
            if (targetSlot < slotStart
                || targetSlot > slotEnd
                || HasDifferentPetInventoryRowAtSlot(connection, transaction, characterId, targetSlot, item.ItemUid))
            {
                var emptySlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Pet, slotStart, slotEnd);
                if (emptySlot < 0)
                {
                    FileLogger.Log($"  [PetItemFallback] normalize skipped: no empty slot item=0x{item.ItemTemplateId:X8} oldSlot={item.SlotIndex} range={slotStart}-{slotEnd}");
                    return false;
                }

                targetSlot = (short)emptySlot;
            }

            var value = ResolvePetInventoryEquipmentFallbackValue(item);
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE character_items
SET list_type = @listType,
    slot_index = @slot,
    item_kind = 'pet',
    stack_count = 0,
    instance_value = 0,
    durability = 0,
    seal_flag = 0,
    option_value = 0,
    expire_time = 0,
    marker_16 = 0,
    pet_serial_or_handle = @value,
    extra_json = @extraJson,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @uid;";
                cmd.Parameters.AddWithValue("@listType", (int)InventoryListType.Pet);
                cmd.Parameters.AddWithValue("@slot", (int)targetSlot);
                cmd.Parameters.AddWithValue("@value", value);
                cmd.Parameters.AddWithValue("@extraJson", CreateDefaultPetExtraJson());
                cmd.Parameters.AddWithValue("@uid", item.ItemUid);
                cmd.ExecuteNonQuery();
            }

            FileLogger.Log($"  [PetItemFallback] normalized uid={item.ItemUid} item=0x{item.ItemTemplateId:X8} {item.ItemKind}/{item.SlotIndex} -> pet/{targetSlot} value=0x{value:X8}");
            item.ListType = InventoryListType.Pet;
            item.SlotIndex = targetSlot;
            item.ItemKind = "pet";
            item.StackCount = 0;
            item.InstanceValue = 0;
            item.Durability = 0;
            item.SealFlag = 0;
            item.OptionValue = 0;
            item.ExpireTime = 0;
            item.Marker16 = 0;
            item.PetSerialOrHandle = value;
            item.ExtraJson = CreateDefaultPetExtraJson();
            return true;
        }

        private static bool PetArtifactMoveValuesMatchSource(InventoryMoveRequest request, ItemRecord source)
        {
            if (request == null || source == null)
                return false;

            var value = request.SourceInstanceValue;
            return value == 0
                || value == source.ItemTemplateId
                || value == source.PetSerialOrHandle
                || value == source.InstanceValue
                || value == source.StackCount;
        }

        private static bool NeedsPetInventoryEquipmentFallback(ItemRecord item)
        {
            if (item == null || item.ListType != InventoryListType.Pet)
                return false;

            if (item.SlotIndex < PetInventorySlotStart || item.SlotIndex > PetConsumableSlotEnd)
                return false;

            try
            {
                if (!ItemMetadataResolver.IsPetInventoryEquipment(item.ItemTemplateId))
                    return false;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [PetItemFallback] item type check failed item=0x{item.ItemTemplateId:X8}: {ex.Message}");
                return false;
            }

            return !string.Equals(item.ItemKind, "pet", StringComparison.Ordinal)
                || item.StackCount != 0
                || item.InstanceValue != 0
                || item.Durability != 0
                || item.SealFlag != 0
                || item.OptionValue != 0
                || item.ExpireTime != 0
                || item.Marker16 != 0
                || string.IsNullOrWhiteSpace(item.ExtraJson)
                || item.ExtraJson.IndexOf("tailData0A", StringComparison.Ordinal) < 0;
        }

        private static int ResolvePetInventoryEquipmentFallbackValue(ItemRecord item)
        {
            if (item == null)
                return 0;

            if (IsCreatureItem(item.ItemTemplateId))
                return item.PetSerialOrHandle;

            if (item.PetSerialOrHandle != 0)
                return item.PetSerialOrHandle;
            if (item.InstanceValue != 0)
                return item.InstanceValue;
            if (item.StackCount != 0)
                return item.StackCount;
            return 0;
        }

        private static bool HasDifferentPetInventoryRowAtSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short slot,
            long itemUid)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT COUNT(1)
FROM character_items
WHERE character_id = @cid
  AND list_type = @lt
  AND slot_index = @slot
  AND item_uid <> @uid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lt", (int)InventoryListType.Pet);
                cmd.Parameters.AddWithValue("@slot", (int)slot);
                cmd.Parameters.AddWithValue("@uid", itemUid);
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
        }
    }
}
