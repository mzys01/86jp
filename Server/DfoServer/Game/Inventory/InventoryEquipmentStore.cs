using System;
using System.Collections.Generic;
using System.Globalization;
using DfoServer.Game.ItemUpgrade;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryEquipmentStore
    {
        private const int CharmEquipmentSlot = 29;
        private const string DefaultEquipmentExtraJson =
            "{\"extData0\":0,\"prefixData0E\":\"0000000000000000\",\"middleData1A\":\"0000000000000000000000000000000000\",\"tailData2F\":\"00000000000000000000000000000000000000000000000000000000000000000000000000\"}";

        private readonly InventoryDbPrimitives _db;
        private readonly InventoryAuditLogger _auditLogger;

        internal InventoryEquipmentStore(InventoryDbPrimitives db, InventoryAuditLogger auditLogger)
        {
            _db = db;
            _auditLogger = auditLogger;
        }

        // ── public API (delegated from SqliteInventoryStore) ──

        internal void SeedNewCharacterEquipment(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, (short slot, int itemId)[] equipment)
        {
            var entries = LoadEquipEntriesTx(connection, transaction, characterId);
            foreach (var (slot, itemId) in equipment)
            {
                var meta = ItemMetadataResolver.Resolve(itemId);
                if (meta == null)
                    throw new System.IO.InvalidDataException(
                        $"[SeedEquip] 初始装备 itemId={itemId} 不在 PVF 装备表 — 创建数据错误, 不静默跳过");

                var fields = new MakeEquipListCodec.DisplayFields
                {
                    InstanceValue = ItemQuality.TopQualitySeed,
                    Durability = meta.Durability,
                };
                var raw = MakeEquipListCodec.BuildEntryFromDisplayFields(slot, itemId, fields);

                int diff = InvenItem.VerifyRoundTrip(raw, out _);
                if (diff >= 0)
                    throw new System.IO.InvalidDataException(
                        $"[SeedEquip] itemId={itemId} slot={slot}: entry roundtrip 首差 offset {diff} (len={raw.Length}) — 不入库");

                var entry = new MakeEquipListCodec.Entry { Slot = slot, ItemId = itemId, Raw = raw };
                int insertAt = entries.FindIndex(e => e.Slot > slot);
                if (insertAt < 0) entries.Add(entry); else entries.Insert(insertAt, entry);
                FileLogger.Log($"  [SeedEquip] 穿戴 slot={slot} itemId={itemId} dur={meta.Durability} ({raw.Length}B)");
            }
            SaveEquipEntriesTx(connection, transaction, characterId, entries);
        }

        internal bool TryPickupRentalWeapon(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            int itemTemplateId,
            int expireTime,
            out short assignedSlot,
            out int instanceValue)
        {
            assignedSlot = -1;
            instanceValue = 0;
            if (connection == null || transaction == null)
                return false;

            var itemKind = "special";
            var durability = RentalWeaponRequestCodec.RentalWeaponDurability;
            if (!RentalWeaponInventoryMapper.IsValidInventoryTemplate(itemTemplateId))
                return false;

            // 续租只刷新同一背包模板，避免同商店条目ID的不同武器互相覆盖。
            var equipped = FindEquippedRentalByInventoryTemplate(
                connection, transaction, characterId, itemTemplateId);
            if (equipped != null)
            {
                instanceValue = RentalWeaponRequestCodec.RentalWeaponQualitySeed;
                UpdateEquippedRentalWeaponEntry(
                    connection, transaction, characterId, equipped, itemTemplateId, instanceValue, expireTime);
                return true;
            }

            var existing = FindRentalByInventoryTemplate(
                connection, transaction, characterId, InventoryListType.Main, itemTemplateId,
                SqliteInventoryStore.QuickSlotStart, SqliteInventoryStore.RentalBagSlotEnd);

            if (existing != null
                && existing.SlotIndex >= SqliteInventoryStore.QuickSlotStart
                && existing.SlotIndex <= SqliteInventoryStore.RentalBagSlotEnd)
            {
                instanceValue = RentalWeaponRequestCodec.RentalWeaponQualitySeed;
                UpdateRentalWeaponEntry(
                    connection, transaction, existing.ItemUid, itemTemplateId, instanceValue, expireTime);
                assignedSlot = existing.SlotIndex;
                return true;
            }

            // 先放快捷栏可见区，满了再放普通背包扩展区。
            var targetSlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Main,
                SqliteInventoryStore.QuickSlotStart, SqliteInventoryStore.QuickSlotEnd);
            if (targetSlot < 0)
                targetSlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Main,
                    SqliteInventoryStore.RentalBagSlotStart, SqliteInventoryStore.RentalBagSlotEnd);
            if (targetSlot < 0)
                return false;

            instanceValue = RentalWeaponRequestCodec.RentalWeaponQualitySeed;
            _db.InsertCharacterItem(
                connection, transaction, characterId, InventoryListType.Main, (short)targetSlot,
                itemTemplateId, itemKind, instanceValue, 0,
                durability, 0, 0, expireTime, -1, 0, DefaultEquipmentExtraJson);
            assignedSlot = (short)targetSlot;
            return true;
        }

        internal int DeleteExpiredRentalEquipment(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            uint now)
        {
            var removed = 0;

            var expiredBagItems = new List<(long itemUid, int slotIndex, int itemTemplateId, int expireTime)>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, slot_index, item_template_id, expire_time
FROM character_items
WHERE character_id = @characterId
  AND list_type = @listType
  AND slot_index >= @slotStart
  AND slot_index <= @slotEnd
  AND expire_time > 0
  AND expire_time <= @now
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.Main);
                command.Parameters.AddWithValue("@slotStart", SqliteInventoryStore.QuickSlotStart);
                command.Parameters.AddWithValue("@slotEnd", SqliteInventoryStore.RentalBagSlotEnd);
                command.Parameters.AddWithValue("@now", now);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var itemTemplateId = reader.GetInt32(2);
                        if (!RentalWeaponInventoryMapper.IsValidInventoryTemplate(itemTemplateId))
                            continue;

                        expiredBagItems.Add((
                            reader.GetInt64(0),
                            reader.GetInt32(1),
                            itemTemplateId,
                            reader.GetInt32(3)));
                    }
                }
            }

            foreach (var item in expiredBagItems)
            {
                _db.DeleteItem(connection, transaction, item.itemUid);
                removed++;
                FileLogger.Log($"[RentalExpire] DELETE inventory char={characterId} slot={item.slotIndex} item=0x{item.itemTemplateId:X8} expire={item.expireTime}");
            }

            var expiredEquippedItems = new List<(int slot, int itemId, int expireTime)>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT slot, item_id, expire_time
FROM character_equipped_entries
WHERE character_id = @characterId
  AND expire_time > 0
  AND expire_time <= @now
ORDER BY slot;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@now", now);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var itemId = reader.GetInt32(1);
                        if (!RentalWeaponInventoryMapper.IsValidInventoryTemplate(itemId)
                            && !ItemMetadataResolver.IsNameTagItem(itemId))
                            continue;

                        expiredEquippedItems.Add((
                            reader.GetInt32(0),
                            itemId,
                            reader.GetInt32(2)));
                    }
                }
            }

            var nameTagCleared = false;
            foreach (var item in expiredEquippedItems)
            {
                DeleteEquippedEntry(connection, transaction, characterId, item.slot);
                removed++;
                if (item.slot == 28 && ItemMetadataResolver.IsNameTagItem(item.itemId))
                    nameTagCleared = true;
                FileLogger.Log($"[ExpiredEquipCleanup] DELETE equipped char={characterId} slot={item.slot} item=0x{item.itemId:X8} expire={item.expireTime}");
            }

            if (nameTagCleared)
                ClearNameTagSubtype1Fields(connection, transaction, characterId);

            return removed;
        }

        private static void ClearNameTagSubtype1Fields(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE character_subtype1_fields
SET name_tag_item_id = 0, name_tag_expire_time = 0
WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
            FileLogger.Log($"[ExpiredEquipCleanup] cleared name_tag subtype1 fields char={characterId}");
        }

        internal void UpsertNameTagEquippedEntry(SqliteConnection connection, SqliteTransaction transaction, int characterId, int itemTemplateId, int expireTime)
        {
            const int nameTagSlot = 28;
            var entries = LoadEquipEntriesTx(connection, transaction, characterId);
            entries.RemoveAll(e => e.Slot == nameTagSlot);
            var fields = new MakeEquipListCodec.DisplayFields
            {
                InstanceValue = unchecked((uint)InventoryDbPrimitives.GenerateInstanceValue(itemTemplateId, nameTagSlot)),
            };
            var raw = MakeEquipListCodec.BuildEntryFromDisplayFields(nameTagSlot, itemTemplateId, fields);
            var entry = new MakeEquipListCodec.Entry
            {
                Slot = nameTagSlot,
                ItemId = itemTemplateId,
                Raw = raw,
                ExpireTime = expireTime,
            };
            var insertAt = entries.FindIndex(e => e.Slot > nameTagSlot);
            if (insertAt < 0) entries.Add(entry); else entries.Insert(insertAt, entry);
            SaveEquipEntriesTx(connection, transaction, characterId, entries);
            FileLogger.Log($"  [NameTag] equipped slot={nameTagSlot} item=0x{itemTemplateId:X8} expire={expireTime}");
        }

        // ── equip / unequip (called from SqliteInventoryStore.TryMoveItem) ──

        internal EquipOutcome HandleEquipSlotMove(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int accountId,
            InventoryMoveRequest request, SqliteInventoryStore.ItemRecord mainSource, InventoryListType dbSrcList)
        {
            var entries = LoadEquipEntriesTx(connection, transaction, characterId);

            int equipSlot = request.DestinationSlotIndex;

            if (request.SourceInstanceValue == 0)
            {
                var removed = entries.Find(e => e.Slot == equipSlot);
                if (removed == null)
                {
                    if (equipSlot == 12)
                    {
                        FileLogger.Log($"  [EquipMove] slot {equipSlot} 已空 (称号 P2 反转包) -> ReverseError");
                        return EquipOutcome.ReverseError;
                    }
                    FileLogger.Log($"  [EquipMove] slot {equipSlot} 已空, 无操作");
                    return EquipOutcome.Unequipped;
                }
                // 卸下克隆装扮时清零 raw[12..15] 克隆目标
                if (ItemMetadataResolver.IsCloneAvatarItem(removed.ItemId))
                  Array.Clear(removed.Raw, 12, 4);
                var occupiedTarget = _db.LoadItemRecord(connection, transaction, characterId, dbSrcList, request.SourceSlotIndex);
                if (occupiedTarget != null)
                {
                    FileLogger.Log($"  [EquipMove] UNEQUIP blocked: target container slot occupied list={dbSrcList} slot={request.SourceSlotIndex} item=0x{occupiedTarget.ItemTemplateId:X8} kind={occupiedTarget.ItemKind}");
                    return EquipOutcome.NoOp;
                }
                entries.Remove(removed);
                SaveEquipEntriesTx(connection, transaction, characterId, entries);
                InsertEquipToContainer(connection, transaction, characterId, dbSrcList, request.SourceSlotIndex, removed.ItemId, removed.Raw, removed.ExpireTime, removed.EquipmentLockId);
                FileLogger.Log($"  [EquipMove] UNEQUIP: removed equip slot {equipSlot} itemId=0x{removed.ItemId:X8} -> {dbSrcList} slot {request.SourceSlotIndex}");
                return EquipOutcome.Unequipped;
            }
            else
            {
                if (IsPetCreatureEquipSlotMove(request, mainSource, dbSrcList))
                    return HandlePetCreatureEquipSlotMove(
                        connection, transaction, characterId, request, mainSource, dbSrcList, entries);

                int wantId = request.SourceInstanceValue;
                var existing = entries.Find(e => e.Slot == equipSlot);
                if (!IsValidEquipSource(mainSource, dbSrcList, wantId))
                {
                    if (equipSlot == 12)
                    {
                        FileLogger.Log($"  [EquipMove] slot {equipSlot} source mismatch (称号 P2 反转包) want=0x{wantId:X8} found=0x{mainSource?.ItemTemplateId ?? 0:X8} -> ReverseError");
                        return EquipOutcome.ReverseError;
                    }
                    FileLogger.Log($"  [EquipMove] EQUIP blocked: invalid source slot={request.SourceSlotIndex} want=0x{wantId:X8} found={(mainSource != null ? $"0x{mainSource.ItemTemplateId:X8}/{mainSource.ItemKind}" : "null")}");
                    return EquipOutcome.NoOp;
                }
                if (!IsCharmSlotCompatible(wantId, equipSlot))
                {
                    FileLogger.Log($"  [EquipMove] EQUIP blocked: item=0x{wantId:X8} is incompatible with slot {equipSlot} (charm slot=29)");
                    return EquipOutcome.NoOp;
                }
                if (equipSlot == 12 && existing != null && existing.ItemId == wantId)
                {
                    FileLogger.Log($"  [EquipMove] slot {equipSlot} 已是 0x{wantId:X8} (称号 P2 反转包) -> ReverseError");
                    return EquipOutcome.ReverseError;
                }

                byte[] entryRaw;
                var fields = LoadDisplayFieldsFromCharacterItem(connection, transaction, characterId, dbSrcList, request.SourceSlotIndex);
                if (fields == null)
                {
                    FileLogger.Log($"  [EquipMove] EQUIP: slot {equipSlot} want 0x{wantId:X8} — no DB record (no-op)");
                    return EquipOutcome.NoOp;
                }
                entryRaw = MakeEquipListCodec.BuildEntryFromDisplayFields(equipSlot, wantId, fields.Value);

                    // 克隆装扮：计算并注入 raw[12..15] 克隆目标物品ID
                    if (ItemMetadataResolver.IsCloneAvatarItem(wantId))
                    {
                        uint cloneTarget = 0;
                        if (existing != null && !ItemMetadataResolver.IsCloneAvatarItem(existing.ItemId))
                        cloneTarget = (uint)existing.ItemId;
                        BitConverter.GetBytes(cloneTarget).CopyTo(entryRaw, 12);
                    }

                _db.DeleteCharacterItemSlot(connection, transaction, characterId, dbSrcList, request.SourceSlotIndex);

                if (existing != null)
                {
                    entries.Remove(existing);
                    InsertEquipToContainer(connection, transaction, characterId, dbSrcList, request.SourceSlotIndex, existing.ItemId, existing.Raw, existing.ExpireTime, existing.EquipmentLockId);
                    FileLogger.Log($"  [EquipMove] REPLACE: slot {equipSlot} old 0x{existing.ItemId:X8} -> {dbSrcList} slot {request.SourceSlotIndex}");
                }

                var newEntry = new MakeEquipListCodec.Entry
                {
                    Slot = equipSlot,
                    ItemId = wantId,
                    Raw = entryRaw,
                    ExpireTime = mainSource != null ? mainSource.ExpireTime : 0,
                    EquipmentLockId = mainSource != null ? mainSource.EquipmentLockId : (byte)0,
                };
                int insertAt = entries.FindIndex(e => e.Slot > equipSlot);
                if (insertAt < 0) entries.Add(newEntry); else entries.Insert(insertAt, newEntry);
                SaveEquipEntriesTx(connection, transaction, characterId, entries);
                FileLogger.Log($"  [EquipMove] EQUIP: slot {equipSlot} itemId=0x{wantId:X8}");
                return EquipOutcome.Equipped;
            }
        }

        private EquipOutcome HandlePetCreatureEquipSlotMove(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryMoveRequest request,
            SqliteInventoryStore.ItemRecord source,
            InventoryListType dbSrcList,
            List<MakeEquipListCodec.Entry> entries)
        {
            var creatureKey = source.PetSerialOrHandle;
            if (creatureKey <= 0)
            {
                FileLogger.Log($"  [EquipMove] PET EQUIP blocked: source slot={request.SourceSlotIndex} item=0x{source.ItemTemplateId:X8} has no creature key");
                return EquipOutcome.NoOp;
            }

            var existing = entries.Find(e => e.Slot == request.DestinationSlotIndex);
            if (existing != null)
                entries.Remove(existing);

            _db.DeleteItem(connection, transaction, source.ItemUid);

            if (existing != null)
            {
                InsertEquipToContainer(
                    connection, transaction, characterId, dbSrcList, request.SourceSlotIndex,
                    existing.ItemId, existing.Raw, existing.ExpireTime, existing.EquipmentLockId);
            }

            var raw = BuildPetCreatureEquipEntry(request.DestinationSlotIndex, source.ItemTemplateId, creatureKey);
            var newEntry = new MakeEquipListCodec.Entry
            {
                Slot = request.DestinationSlotIndex,
                ItemId = source.ItemTemplateId,
                Raw = raw,
                ExpireTime = source.ExpireTime,
                EquipmentLockId = source.EquipmentLockId,
            };

            int insertAt = entries.FindIndex(e => e.Slot > request.DestinationSlotIndex);
            if (insertAt < 0) entries.Add(newEntry); else entries.Insert(insertAt, newEntry);
            SaveEquipEntriesTx(connection, transaction, characterId, entries);

            FileLogger.Log($"  [EquipMove] PET EQUIP: slot {request.DestinationSlotIndex} itemId=0x{source.ItemTemplateId:X8} key={creatureKey} from {dbSrcList} slot {request.SourceSlotIndex}");
            return EquipOutcome.Equipped;
        }

        internal bool HandleUnequipFromSlot(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, int equipSlot)
        {
            var entries = LoadEquipEntriesTx(connection, transaction, characterId);
            var removed = entries.Find(e => e.Slot == equipSlot);
            if (removed == null)
            {
                FileLogger.Log($"  [EquipMove] UNEQUIP(src): slot {equipSlot} not in equip list (no-op)");
                return false;
            }
            // 卸下克隆装扮时清零 raw[12..15] 克隆目标
            if (ItemMetadataResolver.IsCloneAvatarItem(removed.ItemId))
              Array.Clear(removed.Raw, 12, 4);
            entries.Remove(removed);
            SaveEquipEntriesTx(connection, transaction, characterId, entries);
            FileLogger.Log($"  [EquipMove] UNEQUIP(src): removed slot {equipSlot} itemId=0x{removed.ItemId:X8}");
            return true;
        }

        // ── container / cargo state (called from SqliteInventoryStore) ──

        internal Dictionary<InventoryListType, ushort> LoadContainerState(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId)
        {
            var states = new Dictionary<InventoryListType, ushort>();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT list_type, list_param16
FROM character_container_state
WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", characterId);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        states[(InventoryListType)reader.GetInt32(0)] = Convert.ToUInt16(reader.GetInt32(1), CultureInfo.InvariantCulture);
                }
            }

            return states;
        }

        internal void UpsertContainerState(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, InventoryListType listType, ushort listParam16)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, @listType, @listParam16);";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@listParam16", listParam16);
                command.ExecuteNonQuery();
            }
        }

        internal AccountCargoStateSnapshot LoadAccountCargoState(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT selection_key, value32, item_count
FROM account_cargo_state
WHERE account_id = @accountId;";
                command.Parameters.AddWithValue("@accountId", accountId);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return new AccountCargoStateSnapshot();

                    return new AccountCargoStateSnapshot
                    {
                        SelectionKey = Convert.ToUInt16(reader.GetInt32(0), CultureInfo.InvariantCulture),
                        Value32 = reader.GetInt32(1),
                        ItemCount = Convert.ToUInt16(reader.GetInt32(2), CultureInfo.InvariantCulture),
                    };
                }
            }
        }

        internal void UpsertAccountCargoState(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, AccountCargoStateSnapshot state)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO account_cargo_state (account_id, selection_key, value32, item_count, updated_at)
VALUES (@accountId, @selectionKey, @value32, @itemCount, CURRENT_TIMESTAMP);";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@selectionKey", state.SelectionKey);
                command.Parameters.AddWithValue("@value32", state.Value32);
                command.Parameters.AddWithValue("@itemCount", state.ItemCount);
                command.ExecuteNonQuery();
            }
        }

        internal ushort CountAccountCargoItems(SqliteConnection connection, SqliteTransaction transaction, int accountId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT COUNT(1) FROM account_cargo_items WHERE account_id = @accountId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                return Convert.ToUInt16(Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
        }

        // ── private helpers ──

        private List<MakeEquipListCodec.Entry> LoadEquipEntriesTx(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var entries = new List<MakeEquipListCodec.Entry>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT slot, item_id, raw_entry, expire_time, equipment_lock_id FROM character_equipped_entries WHERE character_id = @cid ORDER BY slot";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        entries.Add(new MakeEquipListCodec.Entry
                        {
                            Slot = r.GetInt32(0),
                            ItemId = r.GetInt32(1),
                            Raw = (byte[])r.GetValue(2),
                            ExpireTime = r.GetInt32(3),
                            EquipmentLockId = Convert.ToByte(r.GetInt32(4), CultureInfo.InvariantCulture),
                        });
                }
            }
            return entries;
        }

        private void SaveEquipEntriesTx(SqliteConnection connection, SqliteTransaction transaction, int characterId, List<MakeEquipListCodec.Entry> entries)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM character_equipped_entries WHERE character_id = @cid";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
            foreach (var e in entries)
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "INSERT INTO character_equipped_entries(character_id, slot, item_id, expire_time, equipment_lock_id, raw_entry) VALUES(@cid, @s, @iid, @expireTime, @equipmentLockId, @raw)";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@s", e.Slot);
                    cmd.Parameters.AddWithValue("@iid", e.ItemId);
                    cmd.Parameters.AddWithValue("@expireTime", e.ExpireTime);
                    cmd.Parameters.AddWithValue("@equipmentLockId", (int)e.EquipmentLockId);
                    cmd.Parameters.AddWithValue("@raw", e.Raw);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static bool IsValidEquipSource(SqliteInventoryStore.ItemRecord source, InventoryListType sourceListType, int expectedItemId)
        {
            if (source == null || source.ItemTemplateId != expectedItemId)
                return false;

            if (source.ItemKind == "equipment" || source.ItemKind == "avatar")
                return true;

            if (source.ItemKind == "special" && RentalWeaponInventoryMapper.IsValidInventoryTemplate(source.ItemTemplateId))
                return true;

            return sourceListType == InventoryListType.Pet
                && source.ItemKind == "pet"
                && TryIsPetInventoryEquipment(expectedItemId);
        }

        internal static bool IsCharmSlotCompatible(int itemTemplateId, int equipmentSlot)
        {
            var equipmentType = EquipmentTypeInfo.ParseOrUnknown(ItemMetadataResolver.ResolveEquipmentType(itemTemplateId));
            var isCharm = equipmentType == EquipmentType.Charm;
            return equipmentSlot == CharmEquipmentSlot ? isCharm : !isCharm;
        }

        private static bool TryIsPetInventoryEquipment(int itemTemplateId)
        {
            try
            {
                return ItemMetadataResolver.IsPetInventoryEquipment(itemTemplateId);
            }
            catch
            {
                return false;
            }
        }

        private MakeEquipListCodec.DisplayFields? LoadDisplayFieldsFromCharacterItem(
            SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, short slotIndex)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"SELECT item_template_id, stack_count, durability, extra_json, item_kind, option_value, pet_serial_or_handle
                                    FROM character_items WHERE character_id=@cid AND list_type=@lt AND slot_index=@si";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lt", (int)listType);
                cmd.Parameters.AddWithValue("@si", (int)slotIndex);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    var itemTemplateId = reader.GetInt32(0);
                    var extraJson = reader.IsDBNull(3) ? "{}" : reader.GetString(3);
                    var itemKind = reader.IsDBNull(4) ? "" : reader.GetString(4);
                    var extra = ItemExtraView.Parse(extraJson);
                    var equipmentExtra = extra.Equipment;
                    var durabilityFromDb = (ushort)reader.GetInt32(2);
                    var isAvatar = string.Equals(itemKind, "avatar", StringComparison.Ordinal);
                    var isPet = listType == InventoryListType.Pet && string.Equals(itemKind, "pet", StringComparison.Ordinal);
                    var petSerialOrHandle = reader.GetInt32(6);
                    var optionValue = Convert.ToByte(reader.GetInt32(5), CultureInfo.InvariantCulture);
                    var f = new MakeEquipListCodec.DisplayFields
                    {
                        InstanceValue = unchecked((uint)(isPet ? petSerialOrHandle : reader.GetInt32(1))),
                        Durability = listType == InventoryListType.Avatar ? optionValue : durabilityFromDb,
                        Reinforce = equipmentExtra.ExtData0,
                        Enchant = unchecked((uint)equipmentExtra.EnchantCardId),
                        EnchantUpgradeCount = equipmentExtra.EnchantUpgradeCount,
                        AmplifyType = equipmentExtra.AmplifyType,
                        AmplifyValue = equipmentExtra.AmplifyValue,
                    };
                    if (isPet && petSerialOrHandle != 0 && CreatureExtraResolver.HasCreatureExtra(itemTemplateId))
                        f.CreatureExtra = unchecked((uint)petSerialOrHandle);
                    f.Emblem = equipmentExtra.EmblemData;
                    f.Rune = equipmentExtra.Rune;
                    f.MagicSealCount = equipmentExtra.SealCount;
                    if (f.MagicSealCount > 0)
                    {
                        f.MagicSealTypes = equipmentExtra.SealTypes;
                        f.MagicSealVal1s = equipmentExtra.SealVal1s;
                        f.MagicSealVal2s = equipmentExtra.SealVal2s;
                        f.MagicSealTail = equipmentExtra.SealTail;
                    }
                    f.Forging = equipmentExtra.Forging;
                    if (isAvatar)
                        f.JewelSocket = SqliteInventoryStore.AvatarReservedToEquippedJewel(extra.Avatar.Reserved2);
                    else
                        f.JewelSocket = equipmentExtra.JewelSocket;
                    return f;
                }
            }
        }

        private void InsertEquipToContainer(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, short slot, int itemId, byte[] entryRaw, int entryExpireTime, byte equipmentLockId = 0)
        {
            if (listType == InventoryListType.Pet)
            {
                int serial = ResolvePetSerialOrHandleFromEquippedRaw(entryRaw);
                _db.InsertCharacterItem(connection, transaction, characterId, InventoryListType.Pet, slot, itemId, "pet",
                    stackCount: 0, instanceValue: 0, durability: 0, sealFlag: 0, optionValue: 0,
                    expireTime: 0, marker16: 0, petSerialOrHandle: serial, extraJson: "{}", equipmentLockId: equipmentLockId);
                return;
            }
            //   durability(entry+10)     → 84B Durability(+11)
            //   enchant(entry+16,u32)    → 84B PrefixData0E[0..3] (enchantIndex +14)
            //   enchantUpgrade(entry+20) → 84B PrefixData0E[4] (卡片升级次数)
            //   amplifyType(entry+21)    → 84B PrefixData0E[5] (+19)
            //   amplifyValue(entry+22)   → 84B PrefixData0E[6..7] (+20)
            ushort dur = 0;
            int countOrIv = itemId;
            int expireTime = entryExpireTime;
            string extraJson = "{}";
            if (entryRaw != null && entryRaw.Length >= 24)
            {
                var f = MakeEquipListCodec.ParseDisplayFields(entryRaw);
                dur = f.Durability;
                countOrIv = unchecked((int)f.InstanceValue);
                if (listType == InventoryListType.Avatar)
                {
                    var avatarExtraBuilder = ItemExtraViewBuilder.FromAvatarView(null);
                    avatarExtraBuilder.Avatar.UnknownFixed4 = SqliteInventoryStore.DefaultAvatarUnknownFixed4;
                    avatarExtraBuilder.Avatar.Reserved2 = SqliteInventoryStore.EquippedJewelToAvatarReserved(f.JewelSocket);
                    _db.InsertCharacterItem(
                        connection, transaction, characterId, listType, slot, itemId, "avatar",
                        stackCount: 0, instanceValue: 0, durability: 0, sealFlag: 0, optionValue: ResolveAvatarOptionValue(f),
                        expireTime: 0, marker16: SqliteInventoryStore.DefaultAvatarUnknownFixed30, petSerialOrHandle: 0,
                        extraJson: avatarExtraBuilder.Build().Serialize(), equipmentLockId: equipmentLockId);
                    return;
                }

                var extraBuilder = new ItemExtraViewBuilder();
                extraBuilder.Equipment.ExtData0 = f.Reinforce;
                extraBuilder.Equipment.EnchantCardId = unchecked((int)f.Enchant);
                extraBuilder.Equipment.EnchantUpgradeCount = f.EnchantUpgradeCount;
                extraBuilder.Equipment.AmplifyType = f.AmplifyType;
                extraBuilder.Equipment.AmplifyValue = f.AmplifyValue;
                extraBuilder.Equipment.EmblemData = f.Emblem;
                extraBuilder.Equipment.Rune = f.Rune;
                extraBuilder.Equipment.SealCount = f.MagicSealCount;
                extraBuilder.Equipment.SealTypes = f.MagicSealTypes;
                extraBuilder.Equipment.SealVal1s = f.MagicSealVal1s;
                extraBuilder.Equipment.SealVal2s = f.MagicSealVal2s;
                extraBuilder.Equipment.SealTail = f.MagicSealTail;
                extraBuilder.Equipment.Forging = f.Forging;
                extraBuilder.Equipment.JewelSocket = f.JewelSocket;
                extraJson = extraBuilder.Build().Serialize();
            }
            byte ov = (byte)(listType == InventoryListType.Avatar ? dur : 0);
            _db.InsertCharacterItem(connection, transaction, characterId, listType, slot, itemId, "equipment",
                stackCount: countOrIv, instanceValue: 0, durability: dur, sealFlag: 0, optionValue: ov,
                expireTime: expireTime, marker16: -1, petSerialOrHandle: 0, extraJson: extraJson, equipmentLockId: equipmentLockId);
            FileLogger.Log($"  [InsertEquipToContainer] listType={listType} slot={slot} itemId=0x{itemId:X8} durability={dur} optionValue={ov}");
        }

        private static byte ResolveAvatarOptionValue(MakeEquipListCodec.DisplayFields fields)
        {
            var durabilityOption = unchecked((byte)(fields.Durability & 0xFF));
            return durabilityOption != 0 ? durabilityOption : fields.Reinforce;
        }

        private static bool IsPetCreatureEquipSlotMove(
            InventoryMoveRequest request,
            SqliteInventoryStore.ItemRecord source,
            InventoryListType dbSrcList)
        {
            return request.DestinationSlotIndex == 24
                && dbSrcList == InventoryListType.Pet
                && source != null
                && string.Equals(source.ItemKind, "pet", StringComparison.Ordinal)
                && source.SlotIndex >= SqliteInventoryStore.PetInventorySlotStart
                && source.SlotIndex <= SqliteInventoryStore.PetInventorySlotEnd
                && source.PetSerialOrHandle > 0
                && (request.SourceInstanceValue == 0 || request.SourceInstanceValue == source.ItemTemplateId);
        }

        private static byte[] BuildPetCreatureEquipEntry(short slot, int itemTemplateId, int creatureKey)
        {
            var fields = new MakeEquipListCodec.DisplayFields
            {
                InstanceValue = unchecked((uint)creatureKey),
            };
            return MakeEquipListCodec.BuildEntryFromDisplayFields(slot, itemTemplateId, fields);
        }

        private static int ResolvePetSerialOrHandleFromEquippedRaw(byte[] entryRaw)
        {
            if (entryRaw != null && entryRaw.Length >= 9)
            {
                var creatureKey = BitConverter.ToInt32(entryRaw, 5);
                if (creatureKey > 0 && creatureKey < 1000000)
                    return creatureKey;
            }

            return entryRaw != null && entryRaw.Length >= 28 ? BitConverter.ToInt32(entryRaw, 24) : 0;
        }

        private MakeEquipListCodec.Entry FindEquippedRentalByInventoryTemplate(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int itemTemplateId)
        {
            var entries = LoadEquipEntriesTx(connection, transaction, characterId);
            foreach (var entry in entries)
            {
                if (entry.ItemId != itemTemplateId || entry.ExpireTime <= 0)
                    continue;

                return entry;
            }

            return null;
        }

        private void UpdateEquippedRentalWeaponEntry(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            MakeEquipListCodec.Entry entry,
            int itemTemplateId,
            int wireValue,
            int expireTime)
        {
            var item = InvenItem.Parse(entry.Raw);
            item.ItemId = itemTemplateId;
            item.Value = unchecked((uint)wireValue);
            item.Durability = RentalWeaponRequestCodec.RentalWeaponDurability;
            if (item.Slot <= 10)
                item.Expansion = BitConverter.GetBytes(expireTime);

            var raw = item.ToBytes();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE character_equipped_entries
SET item_id = @itemId,
    expire_time = @expireTime,
    raw_entry = @raw
WHERE character_id = @cid AND slot = @slot;";
                cmd.Parameters.AddWithValue("@itemId", itemTemplateId);
                cmd.Parameters.AddWithValue("@expireTime", expireTime);
                cmd.Parameters.AddWithValue("@raw", raw);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", entry.Slot);
                cmd.ExecuteNonQuery();
            }
        }

        private SqliteInventoryStore.ItemRecord FindRentalByInventoryTemplate(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryListType listType,
            int itemTemplateId,
            int slotStart,
            int slotEnd)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, slot_index, item_template_id, stack_count, instance_value, expire_time
FROM character_items
WHERE character_id = @characterId AND list_type = @listType
  AND slot_index >= @slotStart AND slot_index <= @slotEnd
  AND expire_time > 0
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotStart", slotStart);
                command.Parameters.AddWithValue("@slotEnd", slotEnd);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var templateId = reader.GetInt32(2);
                        var expireTimeVal = reader.GetInt32(5);
                        if (templateId != itemTemplateId || expireTimeVal <= 0)
                            continue;

                        return new SqliteInventoryStore.ItemRecord
                        {
                            ItemUid = reader.GetInt64(0),
                            SlotIndex = Convert.ToInt16(reader.GetInt32(1), CultureInfo.InvariantCulture),
                            ItemTemplateId = templateId,
                            StackCount = reader.GetInt32(3),
                            InstanceValue = reader.GetInt32(4),
                            ExpireTime = expireTimeVal,
                        };
                    }
                }
            }

            return null;
        }

        private void UpdateRentalWeaponEntry(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long itemUid,
            int itemTemplateId,
            int wireValue,
            int expireTime)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_items
SET item_template_id = @itemTemplateId,
    expire_time = @expireTime,
    stack_count = @wireValue,
    instance_value = 0,
    item_kind = 'special',
    durability = @durability,
    marker_16 = -1,
    extra_json = CASE WHEN extra_json IS NULL OR extra_json = '{}' THEN @extraJson ELSE extra_json END,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@itemTemplateId", itemTemplateId);
                command.Parameters.AddWithValue("@expireTime", expireTime);
                command.Parameters.AddWithValue("@wireValue", wireValue);
                command.Parameters.AddWithValue("@durability", RentalWeaponRequestCodec.RentalWeaponDurability);
                command.Parameters.AddWithValue("@extraJson", DefaultEquipmentExtraJson);
                command.Parameters.AddWithValue("@itemUid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        private void DeleteEquippedEntry(SqliteConnection connection, SqliteTransaction transaction, int characterId, int slot)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM character_equipped_entries WHERE character_id = @cid AND slot = @slot;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@slot", slot);
                command.ExecuteNonQuery();
            }
        }
    }
}
