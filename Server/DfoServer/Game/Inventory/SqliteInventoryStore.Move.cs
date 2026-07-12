using DfoServer.Infrastructure;
using DfoServer.Game.Currency;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.ItemUpgrade;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryMoveItem(int characterId, int accountId, InventoryMoveRequest request, out InventoryMoveResult result)
        {
            result = null;

            if (!IsSupportedMoveListType(request.SourceListType) || !IsSupportedMoveListType(request.DestinationListType))
            {
                FileLogger.Log($"  [MoveItem] REJECT: unsupported listType src={request.SourceListType} dst={request.DestinationListType}");
                return false;
            }

            var dbSrcList = MapToDbListType(request.SourceListType);
            var dbDstList = MapToDbListType(request.DestinationListType);

            FileLogger.Log($"  [MoveItem] dbSrc={dbSrcList}({(int)dbSrcList}) slot={request.SourceSlotIndex}, dbDst={dbDstList}({(int)dbDstList}) slot={request.DestinationSlotIndex}");

            if (dbSrcList == dbDstList && request.SourceSlotIndex == request.DestinationSlotIndex
                && request.DestinationListType != InventoryListType.Equipment
                && request.SourceListType != InventoryListType.Equipment)
            {
                result = CreateMoveResult(request, 0, mutated: false);
                return true;
            }

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                bool srcIsCargo = request.SourceListType == InventoryListType.AccountCargo;
                bool dstIsCargo = request.DestinationListType == InventoryListType.AccountCargo;

                var source = srcIsCargo
                    ? _db.LoadAccountCargoItemRecord(connection, transaction, accountId, request.SourceSlotIndex)
                    : _db.LoadItemRecord(connection, transaction, characterId, dbSrcList, request.SourceSlotIndex);
                var destination = dstIsCargo
                    ? _db.LoadAccountCargoItemRecord(connection, transaction, accountId, request.DestinationSlotIndex)
                    : _db.LoadItemRecord(connection, transaction, characterId, dbDstList, request.DestinationSlotIndex);

                FileLogger.Log($"  [MoveItem] source={(source != null ? $"uid={source.ItemUid} kind={source.ItemKind} tmpl=0x{source.ItemTemplateId:X8}" : "null")}, destination={(destination != null ? $"uid={destination.ItemUid} kind={destination.ItemKind} tmpl=0x{destination.ItemTemplateId:X8}" : "null")}");

                if (request.SourceListType == InventoryListType.Pet && source != null)
                    source = NormalizePetInventoryEquipmentSourceFallback(connection, transaction, characterId, source);

                if (TryHandlePetCreatureEquipMove(connection, transaction, characterId, request, source, destination, out result))
                {
                    if (result != null && result.Mutated)
                        transaction.Commit();
                    return true;
                }

                if (request.DestinationListType == InventoryListType.Equipment)
                {
                    var outcome = _equipStore.HandleEquipSlotMove(connection, transaction, characterId, accountId, request, source, dbSrcList);
                    bool changed = outcome == EquipOutcome.Equipped || outcome == EquipOutcome.Unequipped;
                    if (changed)
                        transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount, mutated: changed);
                    if (changed)
                        AttachEquipmentMoveState(connection, null, characterId, result, request.DestinationSlotIndex, outcome, source);
                    result.AckError = outcome == EquipOutcome.ReverseError;
                    return true;
                }
                if (request.SourceListType == InventoryListType.Equipment)
                {
                    bool ok = _equipStore.HandleUnequipFromSlot(connection, transaction, characterId, accountId, request.SourceSlotIndex);
                    if (ok) transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount, mutated: ok);
                    if (ok)
                        AttachEquipmentMoveState(connection, null, characterId, result, request.SourceSlotIndex, EquipOutcome.Unequipped, null);
                    return true;
                }

                if (source == null)
                {
                    if (destination != null)
                    {
                        if (dbSrcList == InventoryListType.Main
                            && !CharmInventoryPolicy.CanEnterMain(
                                connection,
                                transaction,
                                characterId,
                                destination.ItemTemplateId,
                                destination.ListType == InventoryListType.Main ? destination.ItemUid : 0))
                        {
                            result = CreateMoveResult(request, 0, mutated: false);
                            result.FailureReason = InventoryMoveFailureReason.CharmCarryLimit;
                            return false;
                        }

                        FileLogger.Log($"  [MoveItem] MOVE(empty-src): dst uid={destination.ItemUid} tmpl=0x{destination.ItemTemplateId:X8} -> ({dbSrcList},{request.SourceSlotIndex})");
                        MoveRecordTo(characterId, accountId, connection, transaction, destination, dbSrcList, request.SourceSlotIndex);
                        _auditLogger.WriteAuditLog(connection, transaction, characterId, "move_itemspace", destination, dbSrcList, request.SourceSlotIndex, request.MoveCount);
                        transaction.Commit();
                        result = CreateMoveResult(request, request.MoveCount);
                        return true;
                    }
                    FileLogger.Log($"  [MoveItem] FAIL: source is null at dbList={dbSrcList} slot={request.SourceSlotIndex} (dstInstanceValue={request.DestinationInstanceValue})");
                    return false;
                }

                if (!CanMoveToListType(source.ItemKind, request.DestinationListType)
                    || !CanMoveToMainSlotRange(source.ItemKind, dbDstList, request.DestinationSlotIndex))
                {
                    FileLogger.Log($"  [MoveItem] FAIL: invalid destination kind={source.ItemKind} dst={request.DestinationListType}/{dbDstList} slot={request.DestinationSlotIndex}");
                    return false;
                }
                var moveCount = NormalizeMoveCount(source, request.MoveCount);
                destination = ResolveDestinationStackTarget(characterId, accountId, connection, transaction, source, destination, dbSrcList, dbDstList, moveCount);

                if (dbDstList == InventoryListType.Main)
                {
                    var excludedSourceUid = source.ListType == InventoryListType.Main ? source.ItemUid : 0;
                    var excludedDestinationUid = destination != null && destination.ListType == InventoryListType.Main
                        ? destination.ItemUid
                        : 0;
                    if (!CharmInventoryPolicy.CanEnterMain(
                        connection,
                        transaction,
                        characterId,
                        source.ItemTemplateId,
                        excludedSourceUid,
                        excludedDestinationUid))
                    {
                        result = CreateMoveResult(request, 0, mutated: false);
                        result.FailureReason = InventoryMoveFailureReason.CharmCarryLimit;
                        return false;
                    }
                }

                if (CanStack(source, destination, moveCount) && moveCount > 0)
                {
                    UpdateRecordStackCount(connection, transaction, destination, destination.StackCount + moveCount);

                    if (moveCount == source.StackCount)
                    {
                        DeleteRecord(connection, transaction, source);
                        DeleteSortItemLock(characterId, connection, transaction, dbSrcList, request.SourceSlotIndex);
                    }
                    else
                        UpdateRecordStackCount(connection, transaction, source, source.StackCount - moveCount);

                    _auditLogger.WriteAuditLog(connection, transaction, characterId, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
                    transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount);
                    result.DestinationListType = destination.ListType;
                    result.DestinationSlotIndex = destination.SlotIndex;
                    return true;
                }

                if (IsStackableRecord(source) && moveCount > 0 && moveCount < source.StackCount && destination == null)
                {
                    UpdateRecordStackCount(connection, transaction, source, source.StackCount - moveCount);
                    InsertSplitRecord(characterId, accountId, connection, transaction, source, dbDstList, request.DestinationSlotIndex, moveCount);
                    _auditLogger.WriteAuditLog(connection, transaction, characterId, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
                    transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount);
                    return true;
                }

                if (destination == null)
                {
                    FileLogger.Log($"  [MoveItem] MOVE: src uid={source.ItemUid} kind={source.ItemKind} tmpl=0x{source.ItemTemplateId:X8} -> ({dbDstList},{request.DestinationSlotIndex})");
                    MoveRecordTo(characterId, accountId, connection, transaction, source, dbDstList, request.DestinationSlotIndex);
                    _auditLogger.WriteAuditLog(connection, transaction, characterId, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
                    transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount);
                    return true;
                }

                if (!CanSwap(source, destination))
                    return false;

                if (IsAccountCargoRecord(source) || IsAccountCargoRecord(destination))
                    return false;

                FileLogger.Log($"  [MoveItem] SWAP: src uid={source.ItemUid} kind={source.ItemKind} tmpl=0x{source.ItemTemplateId:X8} <-> dst uid={destination.ItemUid} kind={destination.ItemKind} tmpl=0x{destination.ItemTemplateId:X8}");
                _db.SwapItems(connection, transaction, source, destination);
                SwapSortItemLocks(characterId, connection, transaction, source.ListType, source.SlotIndex, destination.ListType, destination.SlotIndex);
                _auditLogger.WriteAuditLog(connection, transaction, characterId, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
                transaction.Commit();
                result = CreateMoveResult(request, request.MoveCount);
                return true;
            }
        }

        public bool TrySortItems(int characterId, int accountId, InventoryListType listType, byte category)
        {
            if (!IsSupportedSortListType(listType))
                return false;

            if (listType == InventoryListType.AccountCargo)
                return TrySortAccountCargoItems(accountId);

            var segmentMap = GetSortSegmentMap(listType);
            if (!segmentMap.TryGetValue(category, out var range))
                return true;

            var (start, end) = range;

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var lockedSlots = LoadSortItemLockSlots(characterId, connection, transaction, listType, start, end);
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"SELECT item_uid, slot_index, item_template_id
                        FROM character_items
                        WHERE character_id = @cid AND list_type = @lt
                          AND slot_index >= @start AND slot_index <= @end
                        ORDER BY item_kind ASC, item_template_id ASC";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@lt", (int)listType);
                    cmd.Parameters.AddWithValue("@start", (int)start);
                    cmd.Parameters.AddWithValue("@end", (int)end);

                    var items = new List<long>();
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var slot = Convert.ToInt16(reader.GetInt32(1), CultureInfo.InvariantCulture);
                            if (!lockedSlots.Contains(slot))
                                items.Add(reader.GetInt64(0));
                        }
                    }

                    int tempSlot = -10000;
                    foreach (var uid in items)
                    {
                        using (var upd = connection.CreateCommand())
                        {
                            upd.Transaction = transaction;
                            upd.CommandText = "UPDATE character_items SET slot_index = @slot WHERE item_uid = @uid";
                            upd.Parameters.AddWithValue("@slot", tempSlot--);
                            upd.Parameters.AddWithValue("@uid", uid);
                            upd.ExecuteNonQuery();
                        }
                    }

                    var targetSlots = new List<short>();
                    for (short slot = start; slot <= end; slot++)
                    {
                        if (!lockedSlots.Contains(slot))
                            targetSlots.Add(slot);
                    }

                    for (var i = 0; i < items.Count && i < targetSlots.Count; i++)
                    {
                        using (var upd = connection.CreateCommand())
                        {
                            upd.Transaction = transaction;
                            upd.CommandText = "UPDATE character_items SET slot_index = @slot WHERE item_uid = @uid";
                            upd.Parameters.AddWithValue("@slot", (int)targetSlots[i]);
                            upd.Parameters.AddWithValue("@uid", items[i]);
                            upd.ExecuteNonQuery();
                        }
                    }
                }
                transaction.Commit();
                return true;
            }
        }

        private bool TrySortAccountCargoItems(int accountId)
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var items = new List<long>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"SELECT item_uid FROM account_cargo_items
                        WHERE account_id = @aid ORDER BY item_kind ASC, item_template_id ASC";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            items.Add(reader.GetInt64(0));
                    }
                }

                int tempSlot = -10000;
                foreach (var uid in items)
                {
                    using (var upd = connection.CreateCommand())
                    {
                        upd.Transaction = transaction;
                        upd.CommandText = "UPDATE account_cargo_items SET slot_index = @slot WHERE item_uid = @uid";
                        upd.Parameters.AddWithValue("@slot", tempSlot--);
                        upd.Parameters.AddWithValue("@uid", uid);
                        upd.ExecuteNonQuery();
                    }
                }

                for (var i = 0; i < items.Count; i++)
                {
                    using (var upd = connection.CreateCommand())
                    {
                        upd.Transaction = transaction;
                        upd.CommandText = "UPDATE account_cargo_items SET slot_index = @slot WHERE item_uid = @uid";
                        upd.Parameters.AddWithValue("@slot", i);
                        upd.Parameters.AddWithValue("@uid", items[i]);
                        upd.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
                return true;
            }
        }

        public bool TryToggleSortItemLock(int characterId, InventoryListType listType, short slotIndex, out SortItemLockEntry entry)
        {
            entry = null;
            if (!IsSupportedSortListType(listType))
                return false;

            var dbListType = MapToDbListType(listType);
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var item = _db.LoadItemRecord(connection, transaction, characterId, dbListType, slotIndex);
                if (item == null)
                    return false;
                if (!CanApplySortItemLock(dbListType, slotIndex))
                {
                    DeleteSortItemLock(characterId, connection, transaction, dbListType, slotIndex);
                    transaction.Commit();
                    return false;
                }
                if (LoadSortItemLock(characterId, connection, transaction, dbListType, slotIndex).HasValue)
                {
                    DeleteSortItemLock(characterId, connection, transaction, dbListType, slotIndex);
                    transaction.Commit();
                    entry = new SortItemLockEntry { ListType = dbListType, SlotIndex = slotIndex, State = 0 };
                    return true;
                }

                UpsertSortItemLock(characterId, connection, transaction, dbListType, slotIndex, 1);
                transaction.Commit();
                entry = new SortItemLockEntry { ListType = dbListType, SlotIndex = slotIndex, State = 1 };
                return true;
            }
        }

        public bool TryUnlockSortItemLock(int characterId, InventoryListType listType, short slotIndex)
        {
            if (!IsSupportedSortListType(listType))
                return false;

            var dbListType = MapToDbListType(listType);
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var item = _db.LoadItemRecord(connection, transaction, characterId, dbListType, slotIndex);
                if (item != null && !CanApplySortItemLock(dbListType, slotIndex))
                {
                    DeleteSortItemLock(characterId, connection, transaction, dbListType, slotIndex);
                    transaction.Commit();
                    return false;
                }

                DeleteSortItemLock(characterId, connection, transaction, dbListType, slotIndex);
                transaction.Commit();
                return true;
            }
        }

        public IReadOnlyList<SortItemLockEntry> LoadSortItemLocks(int characterId)
        {
            using (var connection = OpenConnection())
            {
                var entries = new List<SortItemLockEntry>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT list_type, slot_index, state
FROM character_sort_item_locks
WHERE character_id = @cid
ORDER BY sort_order;";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var listType = (InventoryListType)reader.GetInt32(0);
                            var slotIndex = Convert.ToInt16(reader.GetInt32(1), CultureInfo.InvariantCulture);
                            if (!CanApplySortItemLock(listType, slotIndex))
                                continue;

                            entries.Add(new SortItemLockEntry
                            {
                                ListType = listType,
                                SlotIndex = slotIndex,
                                State = Convert.ToByte(reader.GetInt32(2), CultureInfo.InvariantCulture),
                            });
                        }
                    }
                }
                return entries;
            }
        }

        public IReadOnlyList<SortItemLockEntry> LoadSortItemLocks(int characterId, InventoryListType listType)
        {
            var dbListType = MapToDbListType(listType);
            using (var connection = OpenConnection())
            {
                var entries = new List<SortItemLockEntry>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT list_type, slot_index, state
FROM character_sort_item_locks
WHERE character_id = @cid AND list_type = @lt
ORDER BY sort_order;";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@lt", (int)dbListType);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var lockListType = (InventoryListType)reader.GetInt32(0);
                            var slotIndex = Convert.ToInt16(reader.GetInt32(1), CultureInfo.InvariantCulture);
                            if (!CanApplySortItemLock(lockListType, slotIndex))
                                continue;

                            entries.Add(new SortItemLockEntry
                            {
                                ListType = lockListType,
                                SlotIndex = slotIndex,
                                State = Convert.ToByte(reader.GetInt32(2), CultureInfo.InvariantCulture),
                            });
                        }
                    }
                }
                return entries;
            }
        }

        public CommonInventoryItem LoadCommonItemForRefresh(int characterId, int accountId, InventoryListType listType, short slotIndex)
        {
            var dbListType = MapToDbListType(listType);
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                if (dbListType == InventoryListType.Main && CurrencyService.IsCubeFragmentSlot(slotIndex))
                {
                    foreach (var cube in CurrencyService.LoadCubeFragments(connection, transaction, accountId))
                    {
                        if (cube.Slot == slotIndex && cube.Count > 0)
                        {
                            return new CommonInventoryItem
                            {
                                SlotIndex = slotIndex,
                                ItemTemplateId = cube.ItemId,
                                CountOrInstanceValue = cube.Count,
                            };
                        }
                    }
                    return null;
                }

                if (dbListType == InventoryListType.AccountCargo)
                    return _db.LoadAccountCargoCommonItem(connection, transaction, accountId, slotIndex);

                return _db.LoadCommonItem(connection, transaction, characterId, dbListType, slotIndex);
            }
        }

        public AvatarInventoryItem LoadAvatarItemForRefresh(int characterId, short slotIndex)
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
                return _db.LoadAvatarItem(connection, transaction, characterId, slotIndex);
        }

        public PetInventoryItem LoadPetItemForRefresh(int characterId, short slotIndex)
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
                return _db.LoadPetItem(connection, transaction, characterId, slotIndex);
        }

        private void UpsertSortItemLock(int characterId, SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slotIndex, byte state)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
INSERT INTO character_sort_item_locks (character_id, sort_order, list_type, slot_index, state)
VALUES (@cid, COALESCE((SELECT MAX(sort_order) + 1 FROM character_sort_item_locks WHERE character_id = @cid), 0), @lt, @slot, @state)
ON CONFLICT(character_id, list_type, slot_index)
DO UPDATE SET state = excluded.state;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lt", (int)listType);
                cmd.Parameters.AddWithValue("@slot", (int)slotIndex);
                cmd.Parameters.AddWithValue("@state", (int)state);
                cmd.ExecuteNonQuery();
            }
        }

        private void DeleteSortItemLock(int characterId, SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slotIndex)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
DELETE FROM character_sort_item_locks
WHERE character_id = @cid AND list_type = @lt AND slot_index = @slot;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lt", (int)listType);
                cmd.Parameters.AddWithValue("@slot", (int)slotIndex);
                cmd.ExecuteNonQuery();
            }
        }

        private void MoveSortItemLock(int characterId, SqliteConnection connection, SqliteTransaction transaction, InventoryListType fromListType, short fromSlotIndex, InventoryListType toListType, short toSlotIndex)
        {
            if (LoadSortItemLock(characterId, connection, transaction, toListType, toSlotIndex).HasValue)
                DeleteSortItemLock(characterId, connection, transaction, toListType, toSlotIndex);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE character_sort_item_locks
SET list_type = @toLt, slot_index = @toSlot
WHERE character_id = @cid AND list_type = @fromLt AND slot_index = @fromSlot;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@fromLt", (int)fromListType);
                cmd.Parameters.AddWithValue("@fromSlot", (int)fromSlotIndex);
                cmd.Parameters.AddWithValue("@toLt", (int)toListType);
                cmd.Parameters.AddWithValue("@toSlot", (int)toSlotIndex);
                cmd.ExecuteNonQuery();
            }
        }

        private void SwapSortItemLocks(int characterId, SqliteConnection connection, SqliteTransaction transaction, InventoryListType firstListType, short firstSlotIndex, InventoryListType secondListType, short secondSlotIndex)
        {
            var first = LoadSortItemLock(characterId, connection, transaction, firstListType, firstSlotIndex);
            var second = LoadSortItemLock(characterId, connection, transaction, secondListType, secondSlotIndex);
            DeleteSortItemLock(characterId, connection, transaction, firstListType, firstSlotIndex);
            DeleteSortItemLock(characterId, connection, transaction, secondListType, secondSlotIndex);

            if (first.HasValue)
                UpsertSortItemLock(characterId, connection, transaction, secondListType, secondSlotIndex, first.Value);
            if (second.HasValue)
                UpsertSortItemLock(characterId, connection, transaction, firstListType, firstSlotIndex, second.Value);
        }

        private byte? LoadSortItemLock(int characterId, SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slotIndex)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT state
FROM character_sort_item_locks
WHERE character_id = @cid AND list_type = @lt AND slot_index = @slot
LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lt", (int)listType);
                cmd.Parameters.AddWithValue("@slot", (int)slotIndex);
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return null;
                return Convert.ToByte(value, CultureInfo.InvariantCulture);
            }
        }

        private HashSet<short> LoadSortItemLockSlots(int characterId, SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short start, short end)
        {
            var slots = new HashSet<short>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT slot_index
FROM character_sort_item_locks
WHERE character_id = @cid AND list_type = @lt
  AND slot_index >= @start AND slot_index <= @end;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lt", (int)listType);
                cmd.Parameters.AddWithValue("@start", (int)start);
                cmd.Parameters.AddWithValue("@end", (int)end);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var slot = Convert.ToInt16(reader.GetInt32(0), CultureInfo.InvariantCulture);
                        if (CanApplySortItemLock(listType, slot))
                            slots.Add(slot);
                    }
                }
            }
            return slots;
        }

        private static bool CanApplySortItemLock(InventoryListType listType, short slotIndex)
        {
            if (listType == InventoryListType.Main
                && slotIndex >= AvatarEmblemSlotStart
                && slotIndex <= AvatarEmblemSlotEnd)
                return false;

            if (listType == InventoryListType.Pet
                && slotIndex >= PetInventorySlotStart
                && slotIndex <= PetInventorySlotEnd)
                return false;

            return true;
        }

        private ItemRecord ResolveDestinationStackTarget(
            int characterId, int accountId,
            SqliteConnection connection,
            SqliteTransaction transaction,
            ItemRecord source,
            ItemRecord destination,
            InventoryListType dbSrcList,
            InventoryListType dbDstList,
            int moveCount)
        {
            if (destination != null || source == null || !IsStackableRecord(source))
                return destination;

            if (dbSrcList == dbDstList)
                return destination;

            var stackLimit = GetStackLimit(source);
            ItemRecord stackTarget;
            if (dbDstList == InventoryListType.AccountCargo)
            {
                stackTarget = _db.FindAccountCargoStackableItemByTemplateIdAndExpireTime(
                    connection,
                    transaction,
                    accountId,
                    source.ItemTemplateId,
                    source.ExpireTime,
                    stackLimit,
                    requiredCapacity: moveCount);
            }
            else
            {
                stackTarget = _db.FindStackableItemByTemplateIdAndExpireTime(
                    connection,
                    transaction,
                    characterId,
                    dbDstList,
                    source.ItemTemplateId,
                    source.ExpireTime,
                    stackLimit,
                    requiredCapacity: moveCount);
            }

            return CanStack(source, stackTarget, moveCount) ? stackTarget : destination;
        }

        private void UpdateRecordStackCount(SqliteConnection connection, SqliteTransaction transaction, ItemRecord item, int stackCount)
        {
            if (IsAccountCargoRecord(item))
                _db.UpdateAccountCargoStackCount(connection, transaction, item.ItemUid, stackCount);
            else
                _db.UpdateStackCount(connection, transaction, item.ItemUid, stackCount);
        }

        private void DeleteRecord(SqliteConnection connection, SqliteTransaction transaction, ItemRecord item)
        {
            if (IsAccountCargoRecord(item))
                _db.DeleteAccountCargoItem(connection, transaction, item.ItemUid);
            else
                _db.DeleteItem(connection, transaction, item.ItemUid);
        }

        private void InsertSplitRecord(int characterId, int accountId, SqliteConnection connection, SqliteTransaction transaction, ItemRecord source, InventoryListType destinationListType, short destinationSlotIndex, int moveCount)
        {
            if (destinationListType == InventoryListType.AccountCargo)
            {
                _db.InsertAccountCargoItemRecord(connection, transaction, accountId, source, destinationSlotIndex, moveCount);
                return;
            }

            _db.InsertSplitItem(connection, transaction, characterId, source, destinationListType, destinationSlotIndex, moveCount);
        }

        private void MoveRecordTo(int characterId, int accountId, SqliteConnection connection, SqliteTransaction transaction, ItemRecord item, InventoryListType destinationListType, short destinationSlotIndex)
        {
            if (IsAccountCargoRecord(item))
            {
                if (destinationListType == InventoryListType.AccountCargo)
                    _db.UpdateAccountCargoItemSlot(connection, transaction, item.ItemUid, destinationSlotIndex);
                else
                    _db.MoveItemFromAccountCargo(connection, transaction, characterId, item, destinationListType, destinationSlotIndex);
                return;
            }

            if (destinationListType == InventoryListType.AccountCargo)
            {
                _db.MoveItemToAccountCargo(connection, transaction, accountId, item, destinationSlotIndex);
                return;
            }

            _db.UpdateItemPosition(connection, transaction, item.ItemUid, destinationListType, destinationSlotIndex);
            MoveSortItemLock(characterId, connection, transaction, item.ListType, item.SlotIndex, destinationListType, destinationSlotIndex);
        }

        /// <summary>
        /// </summary>
        private static Dictionary<byte, (short start, short end)> GetSortSegmentMap(InventoryListType listType)
        {
            switch (listType)
            {
                case InventoryListType.Main:
                    return new Dictionary<byte, (short, short)>
                    {
                        { 1,  (9, 64) },
                        { 2,  (65, 120) },
                        { 3,  (121, 176) },
                        { 4,  (177, 232) },
                        { 10, (233, 288) },
                    };
                case InventoryListType.Pet:
                    return new Dictionary<byte, (short, short)>
                    {
                        { 5, (0, 139) },    // 宠物(本体), 共 140 格
                        { 6, (140, 188) },  // 宠物装备
                        { 7, (189, 237) },  // 宠物耗品
                    };
                case InventoryListType.Avatar:
                    return new Dictionary<byte, (short, short)>
                    {
                        { 8, (0, 209) },
                    };
                case InventoryListType.PersonalCargo:
                    return new Dictionary<byte, (short, short)>
                    {
                        { 11, (0, 151) },
                    };
                default:
                    return new Dictionary<byte, (short, short)>();
            }
        }

        private static InventoryMoveResult CreateMoveResult(InventoryMoveRequest request, int moveValue32, bool mutated = true)
        {
            return new InventoryMoveResult
            {
                SourceListType = request.SourceListType,
                SourceSlotIndex = request.SourceSlotIndex,
                MoveValue32 = moveValue32,
                DestinationListType = request.DestinationListType,
                DestinationSlotIndex = request.DestinationSlotIndex,
                Mutated = mutated,
            };
        }

        private static void AttachEquipmentMoveState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryMoveResult result,
            short equipmentSlot,
            EquipOutcome outcome,
            ItemRecord equippedSource)
        {
            result.AffectedEquipmentSlot = equipmentSlot;

            var mutation = BuildSubtype0TailMutation(
                connection,
                transaction,
                characterId,
                equipmentSlot,
                outcome,
                equippedSource);
            if (mutation != null)
                result.Subtype0TailMutation = mutation;
        }

        private static Subtype0TailMoveMutation BuildSubtype0TailMutation(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short equipmentSlot,
            EquipOutcome outcome,
            ItemRecord equippedSource)
        {
            bool equipped = outcome == EquipOutcome.Equipped && equippedSource != null;

            if (equipmentSlot == 11)
            {
                return new Subtype0TailMoveMutation
                {
                    ForgingChanged = true,
                    Forging = equipped ? ResolveForging(equippedSource) : (byte)0,
                };
            }

            if (equipmentSlot == 24)
            {
                if (!equipped || equippedSource.PetSerialOrHandle <= 0)
                {
                    return new Subtype0TailMoveMutation
                    {
                        EquippedCreatureChanged = true,
                        EquippedCreatureNameBytes = Array.Empty<byte>(),
                    };
                }

                return new Subtype0TailMoveMutation
                {
                    EquippedCreatureChanged = true,
                    EquippedCreatureItemId = (uint)equippedSource.ItemTemplateId,
                    EquippedCreatureNameBytes = LoadCreatureNameBytes(connection, transaction, characterId, equippedSource.PetSerialOrHandle),
                    EquippedCreatureAliveState = 1,
                };
            }

            if (equipmentSlot == 28)
            {
                return new Subtype0TailMoveMutation
                {
                    NameTagChanged = true,
                    NameTagItemId = equipped ? (uint)equippedSource.ItemTemplateId : 0u,
                    NameTagExpireTime = equipped && equippedSource.ExpireTime > 0 ? (uint)equippedSource.ExpireTime : 0u,
                };
            }

            return null;
        }

        private static byte ResolveForging(ItemRecord item)
        {
            if (item == null)
                return 0;

            try
            {
                return ItemExtraView.Parse(item.ExtraJson).Equipment.Forging;
            }
            catch
            {
                return 0;
            }
        }

        private static byte[] LoadCreatureNameBytes(SqliteConnection connection, SqliteTransaction transaction, int characterId, int creatureKey)
        {
            if (creatureKey <= 0)
                return Array.Empty<byte>();

            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
SELECT creature_text
FROM character_creatures
WHERE character_id = @characterId
  AND creature_key = @creatureKey
LIMIT 1;";
                    command.Parameters.AddWithValue("@characterId", characterId);
                    command.Parameters.AddWithValue("@creatureKey", creatureKey);

                    var value = command.ExecuteScalar();
                    return value is byte[] bytes ? bytes : Array.Empty<byte>();
                }
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static bool IsSupportedMoveListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Pet
                || listType == InventoryListType.AccountCargo;
        }

        internal static InventoryListType MapToDbListType(InventoryListType listType)
        {
            if (listType == InventoryListType.Equipment)
                return InventoryListType.Avatar;
            return listType;
        }

        private static bool IsSupportedSortListType(InventoryListType listType)
        {
            return IsSupportedMoveListType(listType);
        }

        internal static bool CanMoveToListType(string itemKind, InventoryListType destinationListType)
        {
            if (destinationListType == InventoryListType.Equipment)
                return itemKind == "equipment";

            if (destinationListType == InventoryListType.Main
                || destinationListType == InventoryListType.Avatar
                || destinationListType == InventoryListType.PersonalCargo
                || destinationListType == InventoryListType.AccountCargo)
                return true;

            if (itemKind == "pet" && destinationListType == InventoryListType.Pet)
                return true;

            return false;
        }

        private static bool CanMoveToMainSlotRange(string itemKind, InventoryListType destinationListType, short destinationSlotIndex)
        {
            if (destinationListType != InventoryListType.Main)
                return true;

            if (destinationSlotIndex >= 9 && destinationSlotIndex <= 64)
                return itemKind == "equipment";

            return true;
        }

        private static bool CanSwap(ItemRecord source, ItemRecord destination)
        {
            return CanMoveToListType(source.ItemKind, destination.ListType)
                && CanMoveToListType(destination.ItemKind, source.ListType);
        }

        private static bool CanStack(ItemRecord source, ItemRecord destination, int moveCount)
        {
            return source != null
                && destination != null
                && IsStackableRecord(source)
                && IsStackableRecord(destination)
                && source.ItemTemplateId == destination.ItemTemplateId
                && source.ExpireTime == destination.ExpireTime
                && HasStackCapacity(destination, moveCount);
        }

        private static int NormalizeMoveCount(ItemRecord source, int requestedMoveCount)
        {
            if (!IsStackableRecord(source))
                return 1;

            if (requestedMoveCount <= 0 || requestedMoveCount > source.StackCount)
                return source.StackCount;

            return requestedMoveCount;
        }

        private static bool IsStackableRecord(ItemRecord item)
        {
            return item != null && InventoryDbPrimitives.LoadStackableItem(item.ItemTemplateId) != null;
        }

        private static bool HasStackCapacity(ItemRecord destination, int moveCount)
        {
            var stackLimit = GetStackLimit(destination);
            return stackLimit <= 0 || destination.StackCount + moveCount <= stackLimit;
        }

        private static int GetStackLimit(ItemRecord item)
        {
            var stackable = item != null ? InventoryDbPrimitives.LoadStackableItem(item.ItemTemplateId) : null;
            return stackable != null ? stackable.StackLimit : 0;
        }

        private static bool IsAccountCargoRecord(ItemRecord item)
        {
            return item != null && item.ListType == InventoryListType.AccountCargo;
        }

        private static int GetSortPriority(string itemKind)
        {
            switch (itemKind)
            {
                case "stackable":
                    return 0;
                case "equipment":
                    return 1;
                case "avatar":
                    return 2;
                case "pet":
                    return 3;
                default:
                    return 4;
            }
        }
    }
}
