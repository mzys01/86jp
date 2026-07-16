using DfoServer.Game.Currency;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryDbPrimitives
    {
        // ── Query ──────────────────────────────────────────────

        internal int FindEmptySlot(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, int slotStart = 0, int slotEnd = -1)
        {
            var occupiedSlots = new HashSet<int>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT slot_index
FROM character_items
WHERE character_id = @characterId AND list_type = @listType
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        occupiedSlots.Add(reader.GetInt32(0));
                }
            }

            var maxSlot = slotEnd >= 0 ? slotEnd : (listType == InventoryListType.Main ? 353 : 199);
            for (var slot = slotStart; slot <= maxSlot; slot++)
            {
                // 晶块固定 slot 354-359 保留给账号级晶块, 普通物品不得占用
                if (listType == InventoryListType.Main
                    && slot >= CurrencyService.CubeFragmentSlotStart
                    && slot <= CurrencyService.CubeFragmentSlotEnd)
                    continue;

                if (!occupiedSlots.Contains(slot))
                    return slot;
            }

            return -1;
        }

        internal int FindEmptySlotPreferOther(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, int slotStart, int slotEnd, short? useLastSlot)
        {
            if (!useLastSlot.HasValue)
                return FindEmptySlot(connection, transaction, characterId, listType, slotStart, slotEnd);

            var preferred = FindEmptySlotExcept(connection, transaction, characterId, listType, slotStart, slotEnd, useLastSlot.Value);
            if (preferred >= 0)
                return preferred;

            return FindEmptySlot(connection, transaction, characterId, listType, slotStart, slotEnd);
        }

        internal int FindEmptySlotExcept(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, int slotStart, int slotEnd, short excludedSlot)
        {
            var occupiedSlots = new HashSet<int>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT slot_index
FROM character_items
WHERE character_id = @characterId AND list_type = @listType
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        occupiedSlots.Add(reader.GetInt32(0));
                }
            }

            var maxSlot = slotEnd >= 0 ? slotEnd : (listType == InventoryListType.Main ? 353 : 199);
            for (var slot = slotStart; slot <= maxSlot; slot++)
            {
                if (slot == excludedSlot)
                    continue;

                if (listType == InventoryListType.Main
                    && slot >= CurrencyService.CubeFragmentSlotStart
                    && slot <= CurrencyService.CubeFragmentSlotEnd)
                    continue;

                if (!occupiedSlots.Contains(slot))
                    return slot;
            }

            return -1;
        }

        internal int FindEmptyPetCreatureInventorySlot(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            return FindEmptySlotExcept(
                connection,
                transaction,
                characterId,
                InventoryListType.Pet,
                SqliteInventoryStore.PetInventorySlotStart,
                SqliteInventoryStore.PetInventorySlotEnd,
                SqliteInventoryStore.PetCreatureEquipSlot);
        }

        internal SqliteInventoryStore.ItemRecord FindItemByTemplateId(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, int templateId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value, durability
FROM character_items
WHERE character_id = @characterId AND list_type = @listType AND item_template_id = @templateId
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@templateId", templateId);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new SqliteInventoryStore.ItemRecord
                        {
                            ItemUid = reader.GetInt64(0),
                            ListType = (InventoryListType)reader.GetInt32(1),
                            SlotIndex = (short)reader.GetInt32(2),
                            ItemTemplateId = reader.GetInt32(3),
                            ItemKind = reader.GetString(4),
                            StackCount = reader.GetInt32(5),
                            InstanceValue = reader.GetInt32(6),
                            Durability = (ushort)reader.GetInt32(7),
                        };
                    }
                }
            }
            return null;
        }

        internal SqliteInventoryStore.ItemRecord FindItemByTemplateIdInRange(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, int templateId, int slotStart, int slotEnd)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value, durability
FROM character_items
WHERE character_id = @characterId AND list_type = @listType AND item_template_id = @templateId
  AND slot_index >= @slotStart AND slot_index <= @slotEnd
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@templateId", templateId);
                command.Parameters.AddWithValue("@slotStart", slotStart);
                command.Parameters.AddWithValue("@slotEnd", slotEnd);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new SqliteInventoryStore.ItemRecord
                        {
                            ItemUid = reader.GetInt64(0),
                            ListType = (InventoryListType)reader.GetInt32(1),
                            SlotIndex = (short)reader.GetInt32(2),
                            ItemTemplateId = reader.GetInt32(3),
                            ItemKind = reader.GetString(4),
                            StackCount = reader.GetInt32(5),
                            InstanceValue = reader.GetInt32(6),
                            Durability = (ushort)reader.GetInt32(7),
                        };
                    }
                }
            }
            return null;
        }

        internal SqliteInventoryStore.ItemRecord FindStackableItemByTemplateIdAndExpireTime(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, int templateId, int expireTime, int stackLimit, int slotStart = int.MinValue, int slotEnd = int.MaxValue, int requiredCapacity = 0)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json
FROM character_items
WHERE character_id = @characterId AND list_type = @listType AND item_template_id = @templateId AND expire_time = @expireTime
  AND slot_index >= @slotStart AND slot_index <= @slotEnd
  AND (@stackLimit <= 0 OR stack_count < @stackLimit)
  AND (@requiredCapacity <= 0 OR @stackLimit <= 0 OR stack_count + @requiredCapacity <= @stackLimit)
ORDER BY stack_count DESC, slot_index ASC
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@templateId", templateId);
                command.Parameters.AddWithValue("@expireTime", expireTime);
                command.Parameters.AddWithValue("@stackLimit", stackLimit);
                command.Parameters.AddWithValue("@slotStart", slotStart);
                command.Parameters.AddWithValue("@slotEnd", slotEnd);
                command.Parameters.AddWithValue("@requiredCapacity", requiredCapacity);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                        return SqliteInventoryStore.ReadItemRecord(reader);
                }
            }
            return null;
        }

        internal SqliteInventoryStore.ItemRecord FindAccountCargoStackableItemByTemplateIdAndExpireTime(SqliteConnection connection, SqliteTransaction transaction, int accountId, int templateId, int expireTime, int stackLimit, int requiredCapacity = 0)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, 12 AS list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, 0 AS pet_serial_or_handle, extra_json
FROM account_cargo_items
WHERE account_id = @accountId AND item_template_id = @templateId AND expire_time = @expireTime
  AND (@stackLimit <= 0 OR stack_count < @stackLimit)
  AND (@requiredCapacity <= 0 OR @stackLimit <= 0 OR stack_count + @requiredCapacity <= @stackLimit)
ORDER BY stack_count DESC, slot_index ASC
LIMIT 1;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@templateId", templateId);
                command.Parameters.AddWithValue("@expireTime", expireTime);
                command.Parameters.AddWithValue("@stackLimit", stackLimit);
                command.Parameters.AddWithValue("@requiredCapacity", requiredCapacity);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return SqliteInventoryStore.ReadItemRecord(reader);
                }
            }
        }

        internal SqliteInventoryStore.ItemRecord LoadItemRecord(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, equipment_lock_id, extra_json
FROM character_items
WHERE character_id = @characterId AND list_type = @listType AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return SqliteInventoryStore.ReadItemRecord(reader);
                }
            }
        }

        internal CommonInventoryItem LoadCommonItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, equipment_lock_id, extra_json
FROM character_items
WHERE character_id = @characterId AND list_type = @listType AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return InventoryItemCodec.ReadCommonItem(reader, reader.IsDBNull(13) ? "{}" : reader.GetString(13));
                }
            }
        }

        internal AvatarInventoryItem LoadAvatarItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, equipment_lock_id, extra_json
FROM character_items
WHERE character_id = @characterId AND list_type = @listType AND slot_index = @slotIndex
ORDER BY CASE item_kind
    WHEN 'equipment' THEN 0
    WHEN 'avatar' THEN 1
    WHEN 'pet' THEN 2
    WHEN 'stackable' THEN 3
    ELSE 4
END, item_uid DESC
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.Avatar);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    var itemKind = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    var extraJson = reader.IsDBNull(13) ? "{}" : reader.GetString(13);
                    return itemKind == "avatar"
                        ? InventoryItemCodec.ReadAvatarItem(reader, extraJson)
                        : InventoryItemCodec.ReadEquipmentAsAvatarItem(reader, extraJson);
                }
            }
        }

        internal PetInventoryItem LoadPetItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, equipment_lock_id, extra_json
FROM character_items
WHERE character_id = @characterId AND list_type = @listType AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.Pet);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return InventoryItemCodec.ReadPetItem(reader, reader.IsDBNull(13) ? "{}" : reader.GetString(13));
                }
            }
        }

        internal CommonInventoryItem LoadAccountCargoCommonItem(SqliteConnection connection, SqliteTransaction transaction, int accountId, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT 12 AS list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, 0 AS pet_serial_or_handle, 0 AS equipment_lock_id, extra_json
FROM account_cargo_items
WHERE account_id = @accountId AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return InventoryItemCodec.ReadCommonItem(reader, reader.IsDBNull(13) ? "{}" : reader.GetString(13));
                }
            }
        }

        internal List<SqliteInventoryStore.ItemRecord> LoadItemsByListType(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType)
        {
            var items = new List<SqliteInventoryStore.ItemRecord>();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, equipment_lock_id, extra_json
FROM character_items
WHERE character_id = @characterId AND list_type = @listType
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        items.Add(SqliteInventoryStore.ReadItemRecord(reader));
                }
            }

            return items;
        }

        // ── Write ──────────────────────────────────────────────

        internal void InsertCharacterItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, short slotIndex, int templateId, string itemKind, int stackCount, int instanceValue, ushort durability, byte sealFlag, byte optionValue, int expireTime, int marker16, int petSerialOrHandle, string extraJson, byte equipmentLockId = 0)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, equipment_lock_id, extra_json)
VALUES (
    'character', @ownerId, @characterId, @listType, @slotIndex, @templateId, @itemKind,
    @stackCount, @instanceValue, @durability, @sealFlag, @optionValue, @expireTime, @marker16,
    @petSerialOrHandle, @equipmentLockId, @extraJson);";
                command.Parameters.AddWithValue("@ownerId", characterId);
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.Parameters.AddWithValue("@templateId", templateId);
                command.Parameters.AddWithValue("@itemKind", itemKind);
                command.Parameters.AddWithValue("@stackCount", stackCount);
                command.Parameters.AddWithValue("@instanceValue", instanceValue);
                command.Parameters.AddWithValue("@durability", durability);
                command.Parameters.AddWithValue("@sealFlag", sealFlag);
                command.Parameters.AddWithValue("@optionValue", optionValue);
                command.Parameters.AddWithValue("@expireTime", expireTime);
                command.Parameters.AddWithValue("@marker16", marker16);
                command.Parameters.AddWithValue("@petSerialOrHandle", petSerialOrHandle);
                command.Parameters.AddWithValue("@equipmentLockId", (int)equipmentLockId);
                command.Parameters.AddWithValue("@extraJson", extraJson);
                command.ExecuteNonQuery();
            }
        }

        internal void InsertCharacterItemRecord(SqliteConnection connection, SqliteTransaction transaction, int characterId, SqliteInventoryStore.ItemRecord item)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            InsertCharacterItem(
                connection,
                transaction,
                characterId,
                item.ListType,
                item.SlotIndex,
                item.ItemTemplateId,
                item.ItemKind,
                item.StackCount,
                item.InstanceValue,
                item.Durability,
                item.SealFlag,
                item.OptionValue,
                item.ExpireTime,
                item.Marker16,
                item.PetSerialOrHandle,
                item.ExtraJson,
                item.EquipmentLockId);
        }

        internal void InsertAccountCargoItem(SqliteConnection connection, SqliteTransaction transaction, int accountId, short slotIndex, int templateId, string itemKind, int stackCount, int instanceValue, ushort durability, byte sealFlag, byte optionValue, int expireTime, int marker16, string extraJson)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO account_cargo_items (
    account_id, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    extra_json)
VALUES (
    @accountId, @slotIndex, @templateId, @itemKind,
    @stackCount, @instanceValue, @durability, @sealFlag, @optionValue, @expireTime, @marker16,
    @extraJson);";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.Parameters.AddWithValue("@templateId", templateId);
                command.Parameters.AddWithValue("@itemKind", itemKind);
                command.Parameters.AddWithValue("@stackCount", stackCount);
                command.Parameters.AddWithValue("@instanceValue", instanceValue);
                command.Parameters.AddWithValue("@durability", durability);
                command.Parameters.AddWithValue("@sealFlag", sealFlag);
                command.Parameters.AddWithValue("@optionValue", optionValue);
                command.Parameters.AddWithValue("@expireTime", expireTime);
                command.Parameters.AddWithValue("@marker16", marker16);
                command.Parameters.AddWithValue("@extraJson", string.IsNullOrWhiteSpace(extraJson) ? "{}" : extraJson);
                command.ExecuteNonQuery();
            }
        }

        internal SqliteInventoryStore.ItemRecord LoadAccountCargoItemRecord(SqliteConnection connection, SqliteTransaction transaction, int accountId, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, 12 AS list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, 0 AS pet_serial_or_handle, 0 AS equipment_lock_id, extra_json
FROM account_cargo_items
WHERE account_id = @accountId AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    return SqliteInventoryStore.ReadItemRecord(reader);
                }
            }
        }

        internal void DeleteAccountCargoItem(SqliteConnection connection, SqliteTransaction transaction, long itemUid)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM account_cargo_items WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@itemUid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        internal void UpdateAccountCargoItemSlot(SqliteConnection connection, SqliteTransaction transaction, long itemUid, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE account_cargo_items SET slot_index = @slot, updated_at = CURRENT_TIMESTAMP WHERE item_uid = @uid;";
                command.Parameters.AddWithValue("@slot", slotIndex);
                command.Parameters.AddWithValue("@uid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        internal void UpdateAccountCargoStackCount(SqliteConnection connection, SqliteTransaction transaction, long itemUid, int newCount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE account_cargo_items SET stack_count = @count, instance_value = @count, updated_at = CURRENT_TIMESTAMP WHERE item_uid = @uid;";
                command.Parameters.AddWithValue("@count", newCount);
                command.Parameters.AddWithValue("@uid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        internal void MoveItemToAccountCargo(SqliteConnection connection, SqliteTransaction transaction, int accountId, SqliteInventoryStore.ItemRecord source, short destSlot)
        {
            InsertAccountCargoItemRecord(connection, transaction, accountId, source, destSlot, source.StackCount);
            DeleteItem(connection, transaction, source.ItemUid);
        }

        internal void InsertAccountCargoItemRecord(SqliteConnection connection, SqliteTransaction transaction, int accountId, SqliteInventoryStore.ItemRecord source, short destSlot, int stackCount)
        {
            InsertAccountCargoItem(
                connection,
                transaction,
                accountId,
                destSlot,
                source.ItemTemplateId,
                source.ItemKind,
                stackCount,
                LoadStackableItem(source.ItemTemplateId) != null ? stackCount : source.InstanceValue,
                source.Durability,
                source.SealFlag,
                source.OptionValue,
                source.ExpireTime,
                source.Marker16,
                source.ExtraJson);
        }

        internal void MoveItemFromAccountCargo(SqliteConnection connection, SqliteTransaction transaction, int characterId, SqliteInventoryStore.ItemRecord source, InventoryListType destList, short destSlot)
        {
            InsertCharacterItem(connection, transaction, characterId, destList, destSlot,
                source.ItemTemplateId, source.ItemKind,
                source.StackCount, source.InstanceValue,
                source.Durability, source.SealFlag, 0,
                source.ExpireTime, source.Marker16, 0, source.ExtraJson ?? "{}");
            DeleteAccountCargoItem(connection, transaction, source.ItemUid);
        }

        internal void InsertSplitItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, SqliteInventoryStore.ItemRecord source, InventoryListType listType, short slotIndex, int moveCount)
        {
            InsertCharacterItem(
                connection,
                transaction,
                characterId,
                listType,
                slotIndex,
                source.ItemTemplateId,
                source.ItemKind,
                moveCount,
                moveCount,
                source.Durability,
                source.SealFlag,
                source.OptionValue,
                source.ExpireTime,
                source.Marker16,
                source.PetSerialOrHandle,
                source.ExtraJson);
        }

        internal void UpdateStackCount(SqliteConnection connection, SqliteTransaction transaction, long itemUid, int stackCount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_items
SET stack_count = @stackCount,
    instance_value = @stackCount,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@stackCount", stackCount);
                command.Parameters.AddWithValue("@itemUid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        // Pet list packets use pet_serial_or_handle as the third entry field, so stack counts must mirror there.
        internal void UpdatePetStackCount(SqliteConnection connection, SqliteTransaction transaction, long itemUid, int stackCount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_items
SET stack_count = @stackCount,
    instance_value = @stackCount,
    pet_serial_or_handle = @stackCount,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@stackCount", stackCount);
                command.Parameters.AddWithValue("@itemUid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        internal void UpdateItemPosition(SqliteConnection connection, SqliteTransaction transaction, long itemUid, InventoryListType listType, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_items
SET list_type = @listType,
    slot_index = @slotIndex,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.Parameters.AddWithValue("@itemUid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        internal void UpdateItemExtraJson(SqliteConnection connection, SqliteTransaction transaction, long itemUid, string extraJson)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_items
SET extra_json = @extraJson,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@extraJson", string.IsNullOrWhiteSpace(extraJson) ? "{}" : extraJson);
                command.Parameters.AddWithValue("@itemUid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        internal void DeleteItem(SqliteConnection connection, SqliteTransaction transaction, long itemUid)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM character_sort_item_locks
WHERE EXISTS (
    SELECT 1
    FROM character_items
    WHERE item_uid = @itemUid
      AND character_id = character_sort_item_locks.character_id
      AND list_type = character_sort_item_locks.list_type
      AND slot_index = character_sort_item_locks.slot_index
);

DELETE FROM character_items WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@itemUid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        internal void DeleteCharacterItemSlot(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM character_items WHERE character_id = @cid AND list_type = @listType AND slot_index = @slot;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slot", slotIndex);
                command.ExecuteNonQuery();
            }
        }

        internal void SwapItems(SqliteConnection connection, SqliteTransaction transaction, SqliteInventoryStore.ItemRecord source, SqliteInventoryStore.ItemRecord destination)
        {
            UpdateItemPosition(connection, transaction, source.ItemUid, source.ListType, short.MinValue);
            UpdateItemPosition(connection, transaction, destination.ItemUid, source.ListType, source.SlotIndex);
            UpdateItemPosition(connection, transaction, source.ItemUid, destination.ListType, destination.SlotIndex);
        }

        // ── Wallet ─────────────────────────────────────────────

        internal WalletSnapshot LoadWallet(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var w = CurrencyService.LoadWallet(connection, transaction, characterId);
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT stack_count FROM character_items WHERE character_id = @cid AND list_type = 0 AND slot_index = 2;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    w.Sp = Convert.ToInt32(result);
            }
            return w;
        }

        // ── Tool ───────────────────────────────────────────────

        internal int NextPetSerialOrHandle(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COALESCE(MAX(pet_serial_or_handle), 0) + 1
FROM character_items
WHERE character_id = @characterId AND list_type = @listType;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.Pet);
                var next = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                return next < 1 ? 1 : next;
            }
        }

        internal static int GenerateInstanceValue(int itemTemplateId, int slotIndex)
        {
            return (int)ItemQuality.TopQualitySeed;
        }

        // ── Package / Booster shared ───────────────────────────

        internal bool ConsumeOneStackable(SqliteConnection connection, SqliteTransaction transaction, SqliteInventoryStore.ItemRecord source)
        {
            if (source == null || LoadStackableItem(source.ItemTemplateId) == null)
                return false;

            return ConsumePackageItem(connection, transaction, source);
        }

        internal bool ConsumePackageItem(SqliteConnection connection, SqliteTransaction transaction, SqliteInventoryStore.ItemRecord source)
        {
            if (source == null || source.StackCount <= 0)
                return false;

            if (source.StackCount > 1)
                UpdateStackCount(connection, transaction, source.ItemUid, source.StackCount - 1);
            else
                DeleteItem(connection, transaction, source.ItemUid);

            return true;
        }

        internal SqliteInventoryStore.ItemRecord FindFirstPackageItem(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            foreach (var item in LoadItemsByListType(connection, transaction, characterId, InventoryListType.Main))
            {
                var stackable = LoadStackableItem(item.ItemTemplateId);
                if (stackable == null)
                    continue;

                if (InventoryPackageStore.IsSupportedPackageType(InventoryPackageStore.NormalizeStackableType(stackable.StackableType)))
                    return item;
            }

            return null;
        }

        internal bool TryAddBoosterRewardItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, int itemTemplateId, int stackCount, out BoosterRewardResult result)
        {
            result = null;
            if (!TryAddBoosterRewardItems(connection, transaction, characterId, accountId, itemTemplateId, stackCount, out var results) ||
                results.Count == 0)
            {
                return false;
            }

            result = results[0];
            if (results.Count == 1)
                return true;

            var grantedCount = 0;
            foreach (var item in results)
                grantedCount += Math.Max(1, item.GrantedCount);

            result = new BoosterRewardResult
            {
                ListType = results[0].ListType,
                SlotIndex = results[0].SlotIndex,
                ItemTemplateId = itemTemplateId,
                StackCount = results[0].StackCount,
                GrantedCount = grantedCount,
            };
            return true;
        }

        internal bool TryAddBoosterRewardItems(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, int itemTemplateId, int stackCount, out List<BoosterRewardResult> results)
        {
            results = new List<BoosterRewardResult>();

            if (SpecialRewardRouter.TryRoute(connection, transaction, characterId, accountId, itemTemplateId, stackCount, out var specialOutcome))
            {
                results.Add(BoosterRewardResult.FromSpecialOutcome(specialOutcome));
                return true;
            }

            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata.ItemKind == "special")
                return false;

            if ((CharmInventoryPolicy.IsCharmItem(itemTemplateId) && Math.Max(1, stackCount) > 1)
                || !CharmInventoryPolicy.CanEnterMain(connection, transaction, characterId, itemTemplateId))
                return false;

            if (CurrencyService.IsCubeFragment(itemTemplateId))
            {
                var count = Math.Max(1, stackCount);
                CurrencyService.AddCubeFragment(connection, transaction, accountId, itemTemplateId, count);
                var rewardResult = new BoosterRewardResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = (short)CurrencyService.GetCubeFragmentSlot(itemTemplateId),
                    ItemTemplateId = itemTemplateId,
                    StackCount = count,
                    GrantedCount = count,
                };
                results.Add(rewardResult);
                return true;
            }

            var effectiveCount = Math.Max(1, stackCount);
            var isAvatarReward = ItemMetadataResolver.IsAvatarItem(metadata);
            var isPetConsumable = ItemMetadataResolver.IsPetConsumableItem(metadata);

            int slotStart;
            int slotEnd;
            var insertListType = InventoryListType.Main;
            var expireTime = ResolveBoosterRewardExpireTime(itemTemplateId, metadata);
            var insertKind = metadata.IsStackable && expireTime > 0 ? "special" : metadata.ItemKind;
            var marker16 = metadata.IsStackable ? 0 : -1;
            var petSerial = 0;
            var isPetEquipment = string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal) &&
                ItemMetadataResolver.IsPetInventoryEquipment(itemTemplateId);
            var isCreature = isPetEquipment && SqliteInventoryStore.IsCreatureItem(itemTemplateId);
            var isPetArtifactEquipment = isPetEquipment && !isCreature;
            if (isAvatarReward)
            {
                insertListType = InventoryListType.Avatar;
                insertKind = "avatar";
                slotStart = 0;
                slotEnd = 500;
                expireTime = 0;
                marker16 = SqliteInventoryStore.DefaultAvatarUnknownFixed30;
            }
            else if (isCreature)
            {
                insertListType = InventoryListType.Pet;
                insertKind = "pet";
                slotStart = SqliteInventoryStore.PetInventorySlotStart;
                slotEnd = SqliteInventoryStore.PetInventorySlotEnd;
                expireTime = 0;
                marker16 = 0;
            }
            else if (isPetArtifactEquipment)
            {
                insertListType = InventoryListType.Pet;
                insertKind = "pet";
                slotStart = SqliteInventoryStore.PetEquipmentSlotStart;
                slotEnd = SqliteInventoryStore.PetEquipmentSlotEnd;
                expireTime = 0;
                marker16 = 0;
            }
            else if (isPetConsumable)
            {
                insertListType = InventoryListType.Pet;
                insertKind = "pet";
                slotStart = SqliteInventoryStore.PetConsumableSlotStart;
                slotEnd = SqliteInventoryStore.PetConsumableSlotEnd;
                expireTime = 0;
                marker16 = 0;
                petSerial = effectiveCount;
            }
            else
            {
                metadata.GetSlotRange(out slotStart, out slotEnd);
            }

            if (metadata.IsStackable && !isAvatarReward)
            {
                var remaining = effectiveCount;
                if (insertListType == InventoryListType.Main)
                {
                    remaining = FillBoosterRewardExistingStacks(
                        connection,
                        transaction,
                        characterId,
                        insertListType,
                        itemTemplateId,
                        expireTime,
                        metadata.StackLimit,
                        SqliteInventoryStore.QuickSlotStart,
                        SqliteInventoryStore.QuickSlotEnd,
                        false,
                        remaining,
                        results);
                }

                remaining = FillBoosterRewardExistingStacks(
                    connection,
                    transaction,
                    characterId,
                    insertListType,
                    itemTemplateId,
                    expireTime,
                    metadata.StackLimit,
                    slotStart,
                    slotEnd,
                    isPetConsumable,
                    remaining,
                    results);

                while (remaining > 0)
                {
                    var insertCount = metadata.StackLimit > 0
                        ? Math.Min(metadata.StackLimit, remaining)
                        : remaining;
                    var targetSlot = FindEmptySlot(connection, transaction, characterId, insertListType, slotStart, slotEnd);
                    if (targetSlot < 0)
                    {
                        FileLogger.Log($"  [Booster] no empty slot item=0x{itemTemplateId:X8} list={insertListType} range={slotStart}-{slotEnd}");
                        return false;
                    }

                    InsertCharacterItem(
                        connection,
                        transaction,
                        characterId,
                        insertListType,
                        (short)targetSlot,
                        itemTemplateId,
                        insertKind,
                        insertCount,
                        insertCount,
                        insertListType == InventoryListType.Pet ? (ushort)0 : metadata.Durability,
                        metadata.IsSealed ? (byte)1 : (byte)0,
                        0,
                        expireTime,
                        marker16,
                        isPetConsumable ? insertCount : 0,
                        "{}");
                    results.Add(new BoosterRewardResult
                    {
                        ListType = insertListType,
                        SlotIndex = (short)targetSlot,
                        ItemTemplateId = itemTemplateId,
                        StackCount = insertCount,
                        GrantedCount = insertCount,
                    });
                    remaining -= insertCount;
                }

                return results.Count > 0;
            }

            var insertRows = metadata.IsStackable && !isAvatarReward ? 1 : effectiveCount;
            for (var rowIndex = 0; rowIndex < insertRows; rowIndex++)
            {
                var targetSlot = FindEmptyBoosterRewardSlot(
                    connection,
                    transaction,
                    characterId,
                    insertListType,
                    slotStart,
                    slotEnd,
                    isCreature);
                if (targetSlot < 0)
                {
                    FileLogger.Log($"  [Booster] no empty slot item=0x{itemTemplateId:X8} list={insertListType} range={slotStart}-{slotEnd}");
                    return false;
                }

                var petSerialForInsert = isCreature
                    ? NextPetSerialOrHandle(connection, transaction, characterId)
                    : petSerial;
                var petNonStackable = isCreature || isPetArtifactEquipment;
                var grantedCount = metadata.IsStackable && !isAvatarReward ? effectiveCount : 1;
                var instanceValue = metadata.IsStackable && !isAvatarReward
                    ? effectiveCount
                    : insertListType == InventoryListType.Pet || insertListType == InventoryListType.Avatar
                        ? 0
                        : GenerateInstanceValue(itemTemplateId, targetSlot);
                var storedStackCount = petNonStackable || insertListType == InventoryListType.Avatar
                    ? 0
                    : metadata.IsStackable && !isAvatarReward ? effectiveCount : instanceValue;
                var durability = insertListType == InventoryListType.Pet || insertListType == InventoryListType.Avatar
                    ? (ushort)0
                    : metadata.Durability;
                var optionValue = (byte)0;
                var extraJson = "{}";
                if (insertListType == InventoryListType.Avatar)
                {
                    extraJson = SqliteInventoryStore.CreateDefaultAvatarExtraJson();
                }

                var sealFlag = metadata.IsSealed ? (byte)1 : (byte)0;
                InsertCharacterItem(
                    connection,
                    transaction,
                    characterId,
                    insertListType,
                    (short)targetSlot,
                    itemTemplateId,
                    insertKind,
                    storedStackCount,
                    instanceValue,
                    durability,
                    sealFlag,
                    optionValue,
                    expireTime,
                    marker16,
                    petSerialForInsert,
                    extraJson);

                results.Add(new BoosterRewardResult
                {
                    ListType = insertListType,
                    SlotIndex = (short)targetSlot,
                    ItemTemplateId = itemTemplateId,
                    StackCount = storedStackCount,
                    GrantedCount = grantedCount,
                });
            }

            return results.Count > 0;
        }

        private int FindEmptyBoosterRewardSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryListType listType,
            int slotStart,
            int slotEnd,
            bool isCreature)
        {
            if (!isCreature)
                return FindEmptySlot(connection, transaction, characterId, listType, slotStart, slotEnd);

            return FindEmptyPetCreatureInventorySlot(connection, transaction, characterId);
        }

        private int FillBoosterRewardExistingStacks(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryListType listType,
            int itemTemplateId,
            int expireTime,
            int stackLimit,
            int slotStart,
            int slotEnd,
            bool isPetConsumable,
            int remaining,
            List<BoosterRewardResult> results)
        {
            while (remaining > 0)
            {
                var existing = FindStackableItemByTemplateIdAndExpireTime(
                    connection,
                    transaction,
                    characterId,
                    listType,
                    itemTemplateId,
                    expireTime,
                    stackLimit,
                    slotStart,
                    slotEnd);
                if (existing == null)
                    break;

                var capacity = stackLimit > 0
                    ? Math.Max(0, stackLimit - existing.StackCount)
                    : remaining;
                var addCount = Math.Min(remaining, capacity);
                if (addCount <= 0)
                    break;

                var newStackCount = existing.StackCount + addCount;
                if (isPetConsumable)
                    UpdatePetStackCount(connection, transaction, existing.ItemUid, newStackCount);
                else
                    UpdateStackCount(connection, transaction, existing.ItemUid, newStackCount);
                results.Add(new BoosterRewardResult
                {
                    ListType = listType,
                    SlotIndex = existing.SlotIndex,
                    ItemTemplateId = itemTemplateId,
                    StackCount = newStackCount,
                    GrantedCount = addCount,
                });
                remaining -= addCount;
            }

            return remaining;
        }

        private static int ResolveBoosterRewardExpireTime(int itemTemplateId, ItemMetadata metadata)
        {
            if (metadata == null)
                return 0;

            if (metadata.IsStackable)
            {
                var stackable = LoadStackableItem(itemTemplateId);
                if (stackable == null)
                    return 0;

                if (stackable.UsablePeriod > 0)
                    return AddDaysFromNow(stackable.UsablePeriod);

                if (TryParsePvfExpirationUnixTime(
                        stackable.GetStringValue("expiration date"),
                        stackable.ExpirationDate,
                        out var stackableExpire) &&
                    stackableExpire > DateTimeOffset.Now.ToUnixTimeSeconds())
                    return stackableExpire;

                return 0;
            }

            if (string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal) &&
                ItemMetadataResolver.TryLoadEquipmentFile(itemTemplateId, out var equipment) &&
                TryParsePvfExpirationUnixTime(
                    equipment.GetStringValue("expiration date"),
                    -1,
                    out var equipmentExpire) &&
                equipmentExpire > DateTimeOffset.Now.ToUnixTimeSeconds())
                return equipmentExpire;

            return -1;
        }

        private static int AddDaysFromNow(int days)
        {
            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            var expire = now + (long)days * 86400L;
            return (int)Math.Min(int.MaxValue, expire);
        }

        private static bool TryParsePvfExpirationUnixTime(string value, int numericValue, out int expirationUnixTime)
        {
            expirationUnixTime = 0;
            if (!string.IsNullOrWhiteSpace(value))
            {
                var normalized = value.Trim().Trim('`');
                var match = Regex.Match(normalized, @"\d{4}-\d{2}-\d{2}(?:\s+\d{2}:\d{2}:\d{2})?");
                if (match.Success)
                {
                    var format = match.Value.Length > 10 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd";
                    if (DateTime.TryParseExact(
                            match.Value,
                            format,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var localDateTime))
                    {
                        var offset = new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
                        var unixTime = offset.ToUnixTimeSeconds();
                        if (unixTime > 0 && unixTime <= int.MaxValue)
                        {
                            expirationUnixTime = (int)unixTime;
                            return true;
                        }
                    }
                }
            }

            if (numericValue <= 0)
                return false;

            if (numericValue >= 1000000000)
            {
                expirationUnixTime = numericValue;
                return true;
            }

            if (DateTime.TryParseExact(
                    numericValue.ToString(CultureInfo.InvariantCulture),
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var numericLocalDate))
            {
                var offset = new DateTimeOffset(numericLocalDate, TimeZoneInfo.Local.GetUtcOffset(numericLocalDate));
                var unixTime = offset.ToUnixTimeSeconds();
                if (unixTime > 0 && unixTime <= int.MaxValue)
                {
                    expirationUnixTime = (int)unixTime;
                    return true;
                }
            }

            return false;
        }

        internal static PvfLib.StackableItemFile LoadStackableItem(int itemTemplateId)
        {
            lock (SqliteInventoryStore.StackableItemCacheLock)
            {
                if (SqliteInventoryStore.StackableItemCache.TryGetValue(itemTemplateId, out var cached))
                    return cached;
            }

            try
            {
                var entry = ItemMetadataResolver.GetStackableEntry(itemTemplateId);
                if (entry == null)
                    return null;

                var parsed = PvfLib.StackableItemFile.Parse(GameWorld.PvfArchiveAccessor.ReadText(Path.Combine("stackable", entry.FilePath)));
                lock (SqliteInventoryStore.StackableItemCacheLock)
                    SqliteInventoryStore.StackableItemCache[itemTemplateId] = parsed;
                return parsed;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [Booster] failed to load item=0x{itemTemplateId:X8}: {ex.Message}");
                return null;
            }
        }

        // ── Static item removal (used by QuestService) ────────

        /// <summary>
        /// Remove <paramref name="count"/> units of an item identified by template ID.
        /// For cube fragments the deduction targets the accounts table;
        /// for normal items it targets character_items (list_type=0).
        /// Returns null when the item is not found or the balance is insufficient.
        /// </summary>
        internal static (short SlotIndex, int RemovedCount, int RemainingCount)? RemoveItemByTemplateId(
            SqliteConnection conn, SqliteTransaction tx,
            int characterId, int itemTemplateId, int count)
        {
            // ── Cube fragment path ──
            if (CurrencyService.IsCubeFragment(itemTemplateId))
            {
                int accountId;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT account_id FROM characters WHERE character_id = @cid;";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    var result = cmd.ExecuteScalar();
                    if (result == null) return null;
                    accountId = Convert.ToInt32(result);
                }

                var cubes = CurrencyService.LoadCubeFragments(conn, tx, accountId);
                int idx = cubes.FindIndex(c => c.ItemId == itemTemplateId);
                if (idx < 0 || cubes[idx].Count < count) return null;

                CurrencyService.AddCubeFragment(conn, tx, accountId, itemTemplateId, -count);
                return ((short)cubes[idx].Slot, count, cubes[idx].Count - count);
            }

            // ── Normal item path ──
            // 走正规 DeleteItem/UpdateStackCount(按item_uid): 整删带排列锁清理, 部分扣减维护 instance_value/updated_at。
            // 旧版自写 DELETE/UPDATE 会留孤儿排列锁、instance_value 滞留旧值。
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT item_uid, slot_index, stack_count FROM character_items WHERE character_id = @cid AND list_type = 0 AND item_template_id = @tid LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@tid", itemTemplateId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    long itemUid = reader.GetInt64(0);
                    int slot = reader.GetInt32(1);
                    int stackCount = reader.GetInt32(2);
                    reader.Close();

                    // Material costs must be all-or-nothing; a short stack cannot be
                    // partially deleted and reported as a successful fixed-count spend.
                    if (stackCount < count) return null;

                    var db = new InventoryDbPrimitives();
                    if (stackCount <= count)
                    {
                        db.DeleteItem(conn, tx, itemUid);
                        return ((short)slot, count, 0);
                    }
                    else
                    {
                        int remaining = stackCount - count;
                        db.UpdateStackCount(conn, tx, itemUid, remaining);
                        return ((short)slot, count, remaining);
                    }
                }
            }
        }
    }
}
