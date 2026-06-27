using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        internal SqliteInventoryStore.ItemRecord FindStackableItemByTemplateIdAndExpireTime(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, int templateId, int expireTime, int stackLimit)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value, durability
FROM character_items
WHERE character_id = @characterId AND list_type = @listType AND item_template_id = @templateId AND expire_time = @expireTime
  AND (@stackLimit <= 0 OR stack_count < @stackLimit)
ORDER BY stack_count DESC, slot_index ASC
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@templateId", templateId);
                command.Parameters.AddWithValue("@expireTime", expireTime);
                command.Parameters.AddWithValue("@stackLimit", stackLimit);
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

        internal SqliteInventoryStore.ItemRecord LoadItemRecord(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json
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
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json
FROM character_items
WHERE character_id = @characterId AND list_type = @listType AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return InventoryItemCodec.ReadCommonItem(reader, reader.IsDBNull(12) ? "{}" : reader.GetString(12));
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
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json
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

        internal void InsertCharacterItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, short slotIndex, int templateId, string itemKind, int stackCount, int instanceValue, ushort durability, byte sealFlag, byte optionValue, int expireTime, int marker16, int petSerialOrHandle, string extraJson)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @ownerId, @characterId, @listType, @slotIndex, @templateId, @itemKind,
    @stackCount, @instanceValue, @durability, @sealFlag, @optionValue, @expireTime, @marker16,
    @petSerialOrHandle, @extraJson);";
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
                command.Parameters.AddWithValue("@extraJson", extraJson);
                command.ExecuteNonQuery();
            }
        }

        internal void InsertCommonItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, CommonInventoryItem item)
        {
            InsertCharacterItem(connection, transaction, characterId, listType, item.SlotIndex, item.ItemTemplateId, InventoryItemCodec.InferCommonItemKind(item), item.CountOrInstanceValue, item.CountOrInstanceValue, item.Durability, item.SealFlag, 0, item.ExpireTime, item.Marker16, 0, InventoryItemCodec.SerializeCommon(item));
        }

        internal void InsertAvatarItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, AvatarInventoryItem item)
        {
            InsertCharacterItem(connection, transaction, characterId, InventoryListType.Avatar, item.SlotIndex, item.AvatarItemId, "avatar", 0, 0, 0, 0, item.OptionValue, 0, item.UnknownFixed30, 0, InventoryItemCodec.SerializeAvatar(item));
        }

        internal void InsertPetItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, PetInventoryItem item)
        {
            InsertCharacterItem(connection, transaction, characterId, InventoryListType.Pet, item.SlotIndex, item.CreatureItemId, "pet", 0, 0, 0, 0, 0, 0, 0, item.CreatureSerialOrHandle, InventoryItemCodec.SerializePet(item));
        }

        internal void InsertAccountCargoItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, CommonInventoryItem item)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'account', @ownerId, @characterId, @listType, @slotIndex, @templateId, @itemKind,
    @stackCount, @instanceValue, @durability, @sealFlag, @optionValue, @expireTime, @marker16,
    0, @extraJson);";
                command.Parameters.AddWithValue("@ownerId", accountId);
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.AccountCargo);
                command.Parameters.AddWithValue("@slotIndex", item.SlotIndex);
                command.Parameters.AddWithValue("@templateId", item.ItemTemplateId);
                command.Parameters.AddWithValue("@itemKind", InventoryItemCodec.InferCommonItemKind(item));
                command.Parameters.AddWithValue("@stackCount", item.CountOrInstanceValue);
                command.Parameters.AddWithValue("@instanceValue", item.CountOrInstanceValue);
                command.Parameters.AddWithValue("@durability", item.Durability);
                command.Parameters.AddWithValue("@sealFlag", item.SealFlag);
                command.Parameters.AddWithValue("@optionValue", 0);
                command.Parameters.AddWithValue("@expireTime", item.ExpireTime);
                command.Parameters.AddWithValue("@marker16", item.Marker16);
                command.Parameters.AddWithValue("@extraJson", InventoryItemCodec.SerializeCommon(item));
                command.ExecuteNonQuery();
            }
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

        internal void UpdateCommonExtraJson(SqliteConnection connection, SqliteTransaction transaction, long itemUid, CommonInventoryItem item)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_items
SET extra_json = @extraJson,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@extraJson", InventoryItemCodec.SerializeCommon(item));
                command.Parameters.AddWithValue("@itemUid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        internal void DeleteItem(SqliteConnection connection, SqliteTransaction transaction, long itemUid)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM character_items WHERE item_uid = @itemUid;";
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

        internal SqliteInventoryStore.WalletState LoadWallet(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var snap = CurrencyService.LoadWallet(connection, transaction, characterId);
            var w = new SqliteInventoryStore.WalletState { Gold = snap.Gold, Coin = snap.Cera, TokenCera = snap.TokenCera, HappyTokenCera = snap.HappyTokenCera };
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

        internal void UpdateWallet(SqliteConnection connection, SqliteTransaction transaction, int characterId, int gold, int coin)
        {
            CurrencyService.UpdateGold(connection, transaction, characterId, gold);
            CurrencyService.UpdateCera(connection, transaction, characterId, coin);
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
            return 999999998;
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
            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata.ItemKind == "special")
                return false;

            if (CurrencyService.IsCubeFragment(itemTemplateId))
            {
                var count = Math.Max(1, stackCount);
                CurrencyService.AddCubeFragment(connection, transaction, accountId, itemTemplateId, count);
                result = new BoosterRewardResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = (short)CurrencyService.GetCubeFragmentSlot(itemTemplateId),
                    ItemTemplateId = itemTemplateId,
                    StackCount = count,
                    GrantedCount = count,
                };
                return true;
            }

            var effectiveCount = Math.Max(1, stackCount);
            var isAvatarReward = InventoryPackageStore.IsAvatarReward(metadata);
            var isPetConsumable = SqliteInventoryStore.IsPetConsumableItem(metadata);
            var stackListType = isPetConsumable ? InventoryListType.Pet : InventoryListType.Main;
            if (metadata.IsStackable && !isAvatarReward)
            {
                var existing = FindItemByTemplateId(connection, transaction, characterId, stackListType, itemTemplateId);
                if (existing != null && (metadata.StackLimit <= 0 || existing.StackCount + effectiveCount <= metadata.StackLimit))
                {
                    var newStackCount = existing.StackCount + effectiveCount;
                    if (isPetConsumable)
                        UpdatePetStackCount(connection, transaction, existing.ItemUid, newStackCount);
                    else
                        UpdateStackCount(connection, transaction, existing.ItemUid, newStackCount);
                    result = new BoosterRewardResult
                    {
                        ListType = stackListType,
                        SlotIndex = existing.SlotIndex,
                        ItemTemplateId = itemTemplateId,
                        StackCount = newStackCount,
                        GrantedCount = effectiveCount,
                    };
                    return true;
                }
            }

            int slotStart;
            int slotEnd;
            var insertListType = InventoryListType.Main;
            var insertKind = metadata.ItemKind;
            var expireTime = metadata.IsStackable ? 0 : -1;
            var marker16 = metadata.IsStackable ? 0 : -1;
            var petSerial = 0;
            var isPetEquipment = string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal) &&
                SqliteInventoryStore.IsPetInventoryEquipment(itemTemplateId);
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
                petSerial = NextPetSerialOrHandle(connection, transaction, characterId);
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

            var targetSlot = FindEmptySlot(connection, transaction, characterId, insertListType, slotStart, slotEnd);
            if (targetSlot < 0)
            {
                FileLogger.Log($"  [Booster] no empty slot item=0x{itemTemplateId:X8} list={insertListType} range={slotStart}-{slotEnd}");
                return false;
            }

            var petNonStackable = isCreature || isPetArtifactEquipment;
            var instanceValue = metadata.IsStackable ? effectiveCount : insertListType == InventoryListType.Pet || insertListType == InventoryListType.Avatar ? 0 : GenerateInstanceValue(itemTemplateId, targetSlot);
            var storedStackCount = petNonStackable || insertListType == InventoryListType.Avatar
                ? 0
                : metadata.IsStackable ? effectiveCount : instanceValue;
            var durability = insertListType == InventoryListType.Pet || insertListType == InventoryListType.Avatar
                ? (ushort)0
                : metadata.Durability;
            var optionValue = (byte)0;
            var extraJson = "{}";
            if (insertListType == InventoryListType.Avatar)
            {
                var avatarItem = SqliteInventoryStore.CreateDefaultAvatarItem((short)targetSlot, itemTemplateId, 0);
                optionValue = avatarItem.OptionValue;
                extraJson = InventoryItemCodec.SerializeAvatar(avatarItem);
            }

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
                0,
                optionValue,
                expireTime,
                marker16,
                petSerial,
                extraJson);

            result = new BoosterRewardResult
            {
                ListType = insertListType,
                SlotIndex = (short)targetSlot,
                ItemTemplateId = itemTemplateId,
                StackCount = storedStackCount,
                GrantedCount = effectiveCount,
            };
            return true;
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
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT slot_index, stack_count FROM character_items WHERE character_id = @cid AND list_type = 0 AND item_template_id = @tid LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@tid", itemTemplateId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    int slot = reader.GetInt32(0);
                    int stackCount = reader.GetInt32(1);
                    reader.Close();

                    if (stackCount <= count)
                    {
                        // Full removal
                        using (var del = conn.CreateCommand())
                        {
                            del.Transaction = tx;
                            del.CommandText = "DELETE FROM character_items WHERE character_id = @cid AND list_type = 0 AND slot_index = @slot;";
                            del.Parameters.AddWithValue("@cid", characterId);
                            del.Parameters.AddWithValue("@slot", slot);
                            del.ExecuteNonQuery();
                        }
                        return ((short)slot, count, 0);
                    }
                    else
                    {
                        // Partial deduction
                        int remaining = stackCount - count;
                        using (var upd = conn.CreateCommand())
                        {
                            upd.Transaction = tx;
                            upd.CommandText = "UPDATE character_items SET stack_count = @ns WHERE character_id = @cid AND list_type = 0 AND slot_index = @slot;";
                            upd.Parameters.AddWithValue("@ns", remaining);
                            upd.Parameters.AddWithValue("@cid", characterId);
                            upd.Parameters.AddWithValue("@slot", slot);
                            upd.ExecuteNonQuery();
                        }
                        return ((short)slot, count, remaining);
                    }
                }
            }
        }
    }
}
