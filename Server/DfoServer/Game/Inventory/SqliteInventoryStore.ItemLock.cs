using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        private const byte EquipmentLockErrorTitleTradeDelete = 17;
        private const byte EquipmentLockErrorInvalidTarget = 19;
        private const byte EquipmentLockErrorEmptySlot = 21;
        private const byte EquipmentLockErrorNoFreeId = 22;

        public bool TryLockEquipmentItem(InventoryListType listType, short slotIndex, out EquipmentItemLockResult result)
        {
            result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorInvalidTarget);
            if (!IsSupportedEquipmentLockListType(listType))
                return false;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var target = LoadEquipmentLockTarget(connection, transaction, listType, slotIndex);
                if (target == null)
                {
                    result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorEmptySlot);
                    return false;
                }

                if (!TryValidateEquipmentLockTarget(target, forLock: true, out var errorCode))
                {
                    result = CreateEquipmentLockResult(false, listType, slotIndex, errorCode);
                    return false;
                }

                if (target.EquipmentLockId != 0)
                {
                    result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorInvalidTarget);
                    return false;
                }

                var lockId = AllocateEquipmentLockId(connection, transaction);
                if (lockId == 0)
                {
                    result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorNoFreeId);
                    return false;
                }

                UpdateTargetEquipmentLockId(connection, transaction, target, lockId);
                UpsertEquipmentLock(connection, transaction, lockId, listType, slotIndex, state: 1, remainingSeconds: null);
                transaction.Commit();

                result = CreateEquipmentLockResult(true, listType, slotIndex, 0);
                return true;
            }
        }

        public bool TryUnlockEquipmentItem(InventoryListType listType, short slotIndex, out EquipmentItemLockResult result)
        {
            result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorInvalidTarget);
            if (!IsSupportedEquipmentLockListType(listType))
                return false;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var target = LoadEquipmentLockTarget(connection, transaction, listType, slotIndex);
                if (target == null)
                {
                    result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorEmptySlot);
                    return false;
                }

                if (!TryValidateEquipmentLockTarget(target, forLock: false, out var errorCode))
                {
                    result = CreateEquipmentLockResult(false, listType, slotIndex, errorCode);
                    return false;
                }

                if (target.EquipmentLockId == 0
                    || !TryLoadEquipmentLockState(connection, transaction, target.EquipmentLockId, out var state)
                    || state != 1)
                {
                    result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorInvalidTarget);
                    return false;
                }

                UpdateTargetEquipmentLockId(connection, transaction, target, 0);
                DeleteEquipmentLock(connection, transaction, target.EquipmentLockId);
                transaction.Commit();

                result = CreateEquipmentLockResult(true, listType, slotIndex, 0);
                return true;
            }
        }

        public bool TryCancelEquipmentItemUnlock(InventoryListType listType, short slotIndex, out EquipmentItemLockResult result)
        {
            result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorInvalidTarget);
            if (!IsSupportedEquipmentLockListType(listType))
                return false;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var target = LoadEquipmentLockTarget(connection, transaction, listType, slotIndex);
                if (target == null)
                {
                    result = CreateEquipmentLockResult(false, listType, slotIndex, EquipmentLockErrorEmptySlot);
                    return false;
                }

                if (!TryValidateEquipmentLockTarget(target, forLock: false, out var errorCode)
                    || target.EquipmentLockId == 0
                    || !TryLoadEquipmentLockState(connection, transaction, target.EquipmentLockId, out _))
                {
                    result = CreateEquipmentLockResult(false, listType, slotIndex, errorCode == 0 ? EquipmentLockErrorInvalidTarget : errorCode);
                    return false;
                }

                UpsertEquipmentLock(connection, transaction, target.EquipmentLockId, listType, slotIndex, state: 1, remainingSeconds: null);
                transaction.Commit();

                result = CreateEquipmentLockResult(true, listType, slotIndex, 0);
                return true;
            }
        }

        public IReadOnlyList<EquipmentItemLockEntry> LoadEquipmentItemLocks()
        {
            using (var connection = _context.OpenConnection())
                return LoadEquipmentItemLocks(connection, null, null);
        }

        public IReadOnlyList<EquipmentItemLockEntry> LoadEquipmentItemLocks(InventoryListType listType)
        {
            if (!IsSupportedEquipmentLockListType(listType))
                return Array.Empty<EquipmentItemLockEntry>();

            using (var connection = _context.OpenConnection())
                return LoadEquipmentItemLocks(connection, null, listType);
        }

        private IReadOnlyList<EquipmentItemLockEntry> LoadEquipmentItemLocks(SqliteConnection connection, SqliteTransaction transaction, InventoryListType? listType)
        {
            var entries = new List<EquipmentItemLockEntry>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT inventory_list_type, slot, state, remaining_seconds
FROM (
    SELECT ci.list_type AS inventory_list_type,
           ci.slot_index AS slot,
           l.state AS state,
           l.remaining_seconds AS remaining_seconds,
           l.equipment_lock_id AS equipment_lock_id
    FROM character_item_locks l
    JOIN character_items ci
      ON ci.character_id = l.character_id
     AND ci.equipment_lock_id = l.equipment_lock_id
    WHERE l.character_id = @cid
      AND ci.owner_scope = 'character'
      AND ci.equipment_lock_id > 0
      AND (@lt < 0 OR ci.list_type = @lt)
    UNION ALL
    SELECT 3 AS inventory_list_type,
           e.slot AS slot,
           l.state AS state,
           l.remaining_seconds AS remaining_seconds,
           l.equipment_lock_id AS equipment_lock_id
    FROM character_item_locks l
    JOIN character_equipped_entries e
      ON e.character_id = l.character_id
     AND e.equipment_lock_id = l.equipment_lock_id
    WHERE l.character_id = @cid
      AND e.equipment_lock_id > 0
      AND (@lt < 0 OR @lt = 3)
)
ORDER BY equipment_lock_id;";
                cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                cmd.Parameters.AddWithValue("@lt", listType.HasValue ? (int)listType.Value : -1);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        entries.Add(new EquipmentItemLockEntry
                        {
                            ListType = (InventoryListType)reader.GetInt32(0),
                            SlotIndex = Convert.ToInt16(reader.GetInt32(1), CultureInfo.InvariantCulture),
                            State = Convert.ToByte(reader.GetInt32(2), CultureInfo.InvariantCulture),
                            RemainingSeconds = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                        });
                    }
                }
            }

            return entries;
        }

        private EquipmentLockTarget LoadEquipmentLockTarget(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slotIndex)
        {
            if (listType == InventoryListType.Equipment)
                return LoadEquippedEquipmentLockTarget(connection, transaction, slotIndex);

            var item = _db.LoadItemRecord(connection, transaction, _context.CharacterId, listType, slotIndex);
            if (item == null)
                return null;

            return new EquipmentLockTarget
            {
                ItemUid = item.ItemUid,
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = item.ItemTemplateId,
                EquipmentLockId = item.EquipmentLockId,
                SealFlag = item.SealFlag,
                IsEquipped = false,
            };
        }

        private EquipmentLockTarget LoadEquippedEquipmentLockTarget(SqliteConnection connection, SqliteTransaction transaction, short slotIndex)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT item_id, equipment_lock_id
FROM character_equipped_entries
WHERE character_id = @cid AND slot = @slot
LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                cmd.Parameters.AddWithValue("@slot", (int)slotIndex);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new EquipmentLockTarget
                    {
                        ListType = InventoryListType.Equipment,
                        SlotIndex = slotIndex,
                        ItemTemplateId = reader.GetInt32(0),
                        EquipmentLockId = Convert.ToByte(reader.GetInt32(1), CultureInfo.InvariantCulture),
                        SealFlag = 0,
                        IsEquipped = true,
                    };
                }
            }
        }

        private bool TryValidateEquipmentLockTarget(EquipmentLockTarget target, bool forLock, out byte errorCode)
        {
            errorCode = EquipmentLockErrorInvalidTarget;
            if (target == null)
                return false;

            if (!ItemMetadataResolver.TryLoadEquipmentFile(target.ItemTemplateId, out var equipment))
                return false;

            if (forLock
                && IsEquipmentType(equipment.EquipmentType, "creature")
                && target.SealFlag != 0)
                return false;

            if (forLock
                && IsEquipmentType(equipment.EquipmentType, "title name")
                && IsEquipmentLockTradeDeleteAttachType(equipment.AttachType))
            {
                errorCode = EquipmentLockErrorTitleTradeDelete;
                return false;
            }

            return true;
        }

        private void UpdateTargetEquipmentLockId(SqliteConnection connection, SqliteTransaction transaction, EquipmentLockTarget target, byte equipmentLockId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                if (target.IsEquipped)
                {
                    cmd.CommandText = @"
UPDATE character_equipped_entries
SET equipment_lock_id = @lockId
WHERE character_id = @cid AND slot = @slot;";
                    cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                    cmd.Parameters.AddWithValue("@slot", (int)target.SlotIndex);
                }
                else
                {
                    cmd.CommandText = @"
UPDATE character_items
SET equipment_lock_id = @lockId,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @uid;";
                    cmd.Parameters.AddWithValue("@uid", target.ItemUid);
                }

                cmd.Parameters.AddWithValue("@lockId", (int)equipmentLockId);
                cmd.ExecuteNonQuery();
            }
        }

        private byte AllocateEquipmentLockId(SqliteConnection connection, SqliteTransaction transaction)
        {
            var used = new HashSet<int>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT equipment_lock_id FROM character_item_locks WHERE character_id = @cid AND equipment_lock_id > 0
UNION
SELECT equipment_lock_id FROM character_items WHERE character_id = @cid AND equipment_lock_id > 0
UNION
SELECT equipment_lock_id FROM character_equipped_entries WHERE character_id = @cid AND equipment_lock_id > 0;";
                cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        used.Add(reader.GetInt32(0));
                }
            }

            for (var lockId = 1; lockId <= 255; lockId++)
                if (!used.Contains(lockId))
                    return (byte)lockId;

            return 0;
        }

        private void UpsertEquipmentLock(SqliteConnection connection, SqliteTransaction transaction, byte equipmentLockId, InventoryListType listType, short slotIndex, byte state, int? remainingSeconds)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
INSERT INTO character_item_locks (
    character_id, equipment_lock_id, inventory_list_type, slot, state, remaining_seconds)
VALUES (@cid, @lockId, @listType, @slot, @state, @remainingSeconds)
ON CONFLICT(character_id, equipment_lock_id)
DO UPDATE SET
    inventory_list_type = excluded.inventory_list_type,
    slot = excluded.slot,
    state = excluded.state,
    remaining_seconds = excluded.remaining_seconds;";
                cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                cmd.Parameters.AddWithValue("@lockId", (int)equipmentLockId);
                cmd.Parameters.AddWithValue("@listType", (int)listType);
                cmd.Parameters.AddWithValue("@slot", (int)slotIndex);
                cmd.Parameters.AddWithValue("@state", (int)state);
                cmd.Parameters.AddWithValue("@remainingSeconds", remainingSeconds.HasValue ? (object)remainingSeconds.Value : DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        private bool TryLoadEquipmentLockState(SqliteConnection connection, SqliteTransaction transaction, byte equipmentLockId, out byte state)
        {
            state = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT state
FROM character_item_locks
WHERE character_id = @cid AND equipment_lock_id = @lockId
LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                cmd.Parameters.AddWithValue("@lockId", (int)equipmentLockId);
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return false;

                state = Convert.ToByte(value, CultureInfo.InvariantCulture);
                return true;
            }
        }

        private void DeleteEquipmentLock(SqliteConnection connection, SqliteTransaction transaction, byte equipmentLockId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
DELETE FROM character_item_locks
WHERE character_id = @cid AND equipment_lock_id = @lockId;";
                cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                cmd.Parameters.AddWithValue("@lockId", (int)equipmentLockId);
                cmd.ExecuteNonQuery();
            }
        }

        private static bool IsSupportedEquipmentLockListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.Pet;
        }

        private static EquipmentItemLockResult CreateEquipmentLockResult(bool success, InventoryListType listType, short slotIndex, byte errorCode)
        {
            return new EquipmentItemLockResult
            {
                Success = success,
                ErrorCode = errorCode,
                ListType = listType,
                SlotIndex = slotIndex,
                RemainingSeconds = 0,
            };
        }

        private static bool IsEquipmentType(string equipmentType, string expected)
        {
            return string.Equals(NormalizeEquipmentLockPvfToken(equipmentType), expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEquipmentLockTradeDeleteAttachType(string attachType)
        {
            return string.Equals(NormalizeEquipmentLockPvfToken(attachType), "trade delete", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeEquipmentLockPvfToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim().Trim('`').Trim();
            if (normalized.Length >= 2 && normalized[0] == '[' && normalized[normalized.Length - 1] == ']')
                normalized = normalized.Substring(1, normalized.Length - 2);

            return normalized.Trim();
        }

        private sealed class EquipmentLockTarget
        {
            public long ItemUid { get; set; }

            public InventoryListType ListType { get; set; }

            public short SlotIndex { get; set; }

            public int ItemTemplateId { get; set; }

            public byte EquipmentLockId { get; set; }

            public byte SealFlag { get; set; }

            public bool IsEquipped { get; set; }
        }
    }
}
