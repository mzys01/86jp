using DfoServer.Infrastructure;
using DfoServer.Game.ExpertJob;
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
    public sealed class InventoryMoveRequest
    {
        public InventoryListType SourceListType { get; set; }

        public short SourceSlotIndex { get; set; }

        public int MoveCount { get; set; }

        public int SourceInstanceValue { get; set; }

        public InventoryListType DestinationListType { get; set; }

        public short DestinationSlotIndex { get; set; }

        public int DestinationInstanceValue { get; set; }
    }

    public sealed class InventoryMoveResult
    {
        public InventoryListType SourceListType { get; set; }

        public short SourceSlotIndex { get; set; }

        public int MoveValue32 { get; set; }

        public InventoryListType DestinationListType { get; set; }

        public short DestinationSlotIndex { get; set; }

        public bool Mutated { get; set; }

        public bool AckError { get; set; }
    }

    internal enum EquipOutcome
    {
        Equipped,
        Unequipped,
        ReverseError,
        NoOp,
    }

    public sealed class InventoryMutationResult
    {
        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int RemainingStackCount { get; set; }

        public int InstanceValue { get; set; }

        public ushort Durability { get; set; }

        public int UpdatedGold { get; set; }

        public int UpdatedSp { get; set; }

        public int UpdatedCoin { get; set; }

        public int UpdatedTokenCera { get; set; }

        public int UpdatedHappyTokenCera { get; set; }

        public short RequestedCount { get; set; }

        public short AppliedCount { get; set; }

        // 本次购买是否扣了金币(用于商城回包决定是否刷新主背包 slot0 金币显示)。
        public bool GoldSpent { get; set; }

        public int CostItemTemplateId { get; set; }

        public int CostItemNewStackCount { get; set; }

        public short CostItemSlotIndex { get; set; }

        public List<InventoryMutationResult> ExtraResults { get; } = new List<InventoryMutationResult>();
    }

    public sealed class BoosterRewardResult
    {
        public InventoryListType ListType { get; set; } = InventoryListType.Main;

        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int StackCount { get; set; }

        public int GrantedCount { get; set; }
    }

    public sealed class BoosterUseResult
    {
        public short SourceSlotIndex { get; set; }

        public int SourceItemTemplateId { get; set; }

        public int SourceRemainingStackCount { get; set; }

        public int SourceInstanceValue { get; set; }

        public List<BoosterRewardResult> Rewards { get; } = new List<BoosterRewardResult>();
    }

    public sealed class SqliteInventoryStore : IInventoryStore
    {
        internal const int DefaultAvatarUnknownFixed30 = 0x00001E00;
        internal const ushort DefaultAvatarUnknownFixed4 = 0x0400;
        private const short ReviveCoinSlotIndex = 1;
        internal static readonly object StackableItemCacheLock = new object();
        internal static readonly Dictionary<int, PvfLib.StackableItemFile> StackableItemCache = new Dictionary<int, PvfLib.StackableItemFile>();

        private readonly ScopedStoreContext _context;
        private readonly InventoryAuditLogger _auditLogger;
        internal readonly InventoryDbPrimitives _db;
        private readonly InventoryEnchantStore _enchantStore;
        private readonly InventoryPackageStore _packageStore;
        private readonly InventoryShopStore _shopStore;
        internal readonly InventoryEquipmentStore _equipStore;
        private readonly InventoryMigrationRunner _migrationRunner;

        public SqliteInventoryStore(string databasePath, string schemaFilePath)
        {
            _context = new ScopedStoreContext(databasePath, schemaFilePath);
            _auditLogger = new InventoryAuditLogger();
            _db = new InventoryDbPrimitives();
            _enchantStore = new InventoryEnchantStore(_db, _auditLogger);
            _packageStore = new InventoryPackageStore(_db, _auditLogger);
            _shopStore = new InventoryShopStore(_db, _auditLogger);
            _equipStore = new InventoryEquipmentStore(_db, _auditLogger);
            _migrationRunner = new InventoryMigrationRunner();
        }

        public IDisposable BeginScope(int characterId, int accountId) => _context.BeginScope(characterId, accountId);

        public int CountItem(int itemTemplateId)
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(_context.ConnectionString))
            {
                conn.Open();
                using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
                    "SELECT COALESCE(SUM(stack_count), 0) FROM character_items WHERE character_id = @cid AND list_type = 0 AND item_template_id = @tid",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                    cmd.Parameters.AddWithValue("@tid", itemTemplateId);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }


        public void RunMigrations()
        {
            using (var connection = _context.OpenConnection())
                _migrationRunner.RunMigrations(connection);
        }

        public void EnsureDatabase(CharacterItemListSnapshot seedSnapshot)
        {
            using (var connection = _context.OpenConnection())
            {
                _migrationRunner.RunMigrationsInternal(connection);

                if (HasSeedData(connection))
                    return;

                SeedInitialSnapshot(connection, seedSnapshot);
            }
        }

        public void EnsureContainerState(int characterId)
        {
            using (var connection = _context.OpenConnection())
            {
                int count;
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM character_container_state WHERE character_id = @cid";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    count = Convert.ToInt32(cmd.ExecuteScalar());
                }
                using (var tx = connection.BeginTransaction())
                {
                    if (count <= 0)
                    {
                        _equipStore.UpsertContainerState(connection, tx, characterId, _context.AccountId, InventoryListType.Main, 24);
                        _equipStore.UpsertContainerState(connection, tx, characterId, _context.AccountId, InventoryListType.Avatar, 0);
                        _equipStore.UpsertContainerState(connection, tx, characterId, _context.AccountId, InventoryListType.PersonalCargo, 0);
                    }

                    EnsureReviveCoinSlot(connection, tx, characterId);
                    tx.Commit();
                }
            }
        }

        private static void EnsureReviveCoinSlot(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
INSERT OR IGNORE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @cid, @cid, 0, @slotIndex, 1, 'stackable',
    0, 0, 0, 0, 0, 0, 0,
    0, '{}');";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slotIndex", ReviveCoinSlotIndex);
                cmd.ExecuteNonQuery();
            }
        }

        public CharacterItemListSnapshot LoadCharacterItemListSnapshot()
        {
            using (var connection = _context.OpenConnection())
            {
                var snapshot = new CharacterItemListSnapshot();
                var listParams = _equipStore.LoadContainerState(connection, null, _context.CharacterId, _context.AccountId);
                snapshot.MainListParam16 = GetListParam(listParams, InventoryListType.Main);
                snapshot.AvatarListParam16 = GetListParam(listParams, InventoryListType.Avatar);
                snapshot.PersonalCargoListParam16 = GetListParam(listParams, InventoryListType.PersonalCargo);
                snapshot.AccountCargoState = _equipStore.LoadAccountCargoState(connection, null, _context.CharacterId, _context.AccountId);

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json
FROM character_items
WHERE character_id = @characterId
ORDER BY list_type, slot_index;";
                    command.Parameters.AddWithValue("@characterId", _context.CharacterId);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var listType = (InventoryListType)reader.GetInt32(0);
                            var extraJson = reader.IsDBNull(12) ? "{}" : reader.GetString(12);

                            switch (listType)
                            {
                                case InventoryListType.Main:
                                    snapshot.MainItems.Add(InventoryItemCodec.ReadCommonItem(reader, extraJson));
                                    break;
                                case InventoryListType.Avatar:
                                    var avKind = reader.IsDBNull(3) ? "" : reader.GetString(3);
                                    snapshot.AvatarItems.Add(avKind == "avatar"
                                        ? InventoryItemCodec.ReadAvatarItem(reader, extraJson)
                                        : InventoryItemCodec.ReadEquipmentAsAvatarItem(reader, extraJson));
                                    break;
                                case InventoryListType.PersonalCargo:
                                    snapshot.PersonalCargoItems.Add(InventoryItemCodec.ReadCommonItem(reader, extraJson));
                                    break;
                                case InventoryListType.Pet:
                                    snapshot.PetItems.Add(InventoryItemCodec.ReadPetItem(reader, extraJson));
                                    break;
                            }
                        }
                    }
                }

                using (var acCmd = connection.CreateCommand())
                {
                    acCmd.CommandText = @"
SELECT list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json
FROM character_items
WHERE owner_scope = 'account' AND owner_id = @accountId AND list_type = @listType
ORDER BY slot_index;";
                    acCmd.Parameters.AddWithValue("@accountId", _context.AccountId);
                    acCmd.Parameters.AddWithValue("@listType", (int)InventoryListType.AccountCargo);
                    using (var reader = acCmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.AccountCargoItems.Add(InventoryItemCodec.ReadCommonItem(reader, reader.IsDBNull(12) ? "{}" : reader.GetString(12)));
                    }
                }

                // 读取账号级晶块, 合成虚拟 slot 条目添加到 MainItems
                var cubeFragments = CurrencyService.LoadCubeFragments(connection, null, _context.AccountId);
                foreach (var (itemId, slot, count) in cubeFragments)
                {
                    if (count > 0)
                    {
                        snapshot.MainItems.Add(new CommonInventoryItem
                        {
                            SlotIndex = (short)slot,
                            ItemTemplateId = itemId,
                            CountOrInstanceValue = count,
                        });
                    }
                }

                return snapshot;
            }
        }

        public int DeleteExpiredRentalEquipment()
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var count = _equipStore.DeleteExpiredRentalEquipment(connection, transaction, _context.CharacterId, _context.AccountId);
                if (count > 0) transaction.Commit();
                return count;
            }
        }

        public bool TryDeleteItem(InventoryListType listType, short slotIndex, short deleteCount, out InventoryMutationResult result)
        {
            result = null;
            if (!IsSupportedDeleteOrSellListType(listType))
                return false;

            var dbListType = MapToDbListType(listType);

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var item = _db.LoadItemRecord(connection, transaction, _context.CharacterId, dbListType, slotIndex);
                if (item == null)
                    return false;

                var appliedCount = NormalizeRemovalCount(item, deleteCount);
                if (item.ItemKind == "stackable" && appliedCount < item.StackCount)
                {
                    _db.UpdateStackCount(connection, transaction, item.ItemUid, item.StackCount - appliedCount);
                }
                else
                {
                    _db.DeleteItem(connection, transaction, item.ItemUid);
                }

                _auditLogger.WriteDeleteAuditLog(connection, transaction, _context.CharacterId, item, appliedCount);
                var wallet = _db.LoadWallet(connection, transaction, _context.CharacterId);
                transaction.Commit();

                result = new InventoryMutationResult
                {
                    ListType = listType,
                    SlotIndex = slotIndex,
                    ItemTemplateId = item.ItemTemplateId,
                    RemainingStackCount = Math.Max(0, item.StackCount - appliedCount),
                    InstanceValue = item.InstanceValue,
                    Durability = item.Durability,
                    UpdatedGold = wallet.Gold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = wallet.Coin,
                    RequestedCount = deleteCount,
                    AppliedCount = (short)appliedCount,
                };
                return true;
            }
        }

        public bool TryOpenAvatarPackage(AvatarPackageOpenRequest request, out AvatarPackageOpenResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _packageStore.TryOpenAvatarPackage(connection, transaction, _context.CharacterId, _context.AccountId, request, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        public bool TryOpenSelectablePackage(SelectablePackageOpenRequest request, out SelectablePackageOpenResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _packageStore.TryOpenSelectablePackage(connection, transaction, _context.CharacterId, _context.AccountId, request, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        public bool TryUseBoosterItem(short? slotIndex, IReadOnlyList<int> selectedItemTemplateIds, out BoosterUseResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _packageStore.TryUseBoosterItem(connection, transaction, _context.CharacterId, _context.AccountId, slotIndex, selectedItemTemplateIds, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        public bool TryOpenPackage0207(short slotIndex, IReadOnlyList<int> selectedItemTemplateIds, out BoosterUseResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _packageStore.TryOpenPackage0207(connection, transaction, _context.CharacterId, _context.AccountId, slotIndex, selectedItemTemplateIds, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        public bool TryBuyItem(int itemTemplateId, int buyCount, out InventoryMutationResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _shopStore.TryBuyItem(connection, transaction, _context.CharacterId, _context.AccountId, itemTemplateId, buyCount, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        internal const int QuickSlotStart = 3;
        internal const int QuickSlotEnd = 8;
        internal const int RentalBagSlotStart = 9;
        internal const int RentalBagSlotEnd = 64;

        // 宠物栏(list 7)"宠物"本体分页槽段(category 5): slot 0..139 共 140 格(实测计数)。
        // 其后 宠物装备=140..188(cat6)、宠物耗品=189..237(cat7)。新购宠物从本页首格开始填。
        internal const int PetInventorySlotStart = 0;
        internal const int PetInventorySlotEnd = 139;
        public bool TryPickupRentalWeapon(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int itemTemplateId,
            int expireTime,
            out short assignedSlot,
            out int instanceValue)
            => _equipStore.TryPickupRentalWeapon(connection, transaction, _context.CharacterId, _context.AccountId, itemTemplateId, expireTime, out assignedSlot, out instanceValue);

        public bool TryPickupItem(int itemTemplateId, int stackCount, out short assignedSlot)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var result = TryPickupItemCore(connection, transaction,
                    _context.CharacterId, _context.AccountId,
                    itemTemplateId, stackCount, out assignedSlot);
                if (result) transaction.Commit();
                return result;
            }
        }

        internal bool TryPickupItemCore(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int accountId,
            int itemTemplateId, int stackCount, out short assignedSlot)
        {
            assignedSlot = -1;

            // 晶块走账号级存储, 不进 character_items
            if (CurrencyService.IsCubeFragment(itemTemplateId))
            {
                CurrencyService.AddCubeFragment(connection, transaction, accountId, itemTemplateId, stackCount);
                assignedSlot = (short)CurrencyService.GetCubeFragmentSlot(itemTemplateId);
                return true;
            }

            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata.ItemKind == "special")
                return false;

            bool isConsumable = metadata.IsStackable
                && metadata.StackableType != null
                && metadata.StackableType.IndexOf("[waste]", System.StringComparison.OrdinalIgnoreCase) >= 0;

            if (metadata.IsStackable)
            {
                if (isConsumable)
                {
                    var existingQuick = _db.FindItemByTemplateIdInRange(connection, transaction, characterId, InventoryListType.Main, itemTemplateId, QuickSlotStart, QuickSlotEnd);
                    if (existingQuick != null && (metadata.StackLimit <= 0 || existingQuick.StackCount + stackCount <= metadata.StackLimit))
                    {
                        _db.UpdateStackCount(connection, transaction, existingQuick.ItemUid, existingQuick.StackCount + stackCount);
                        assignedSlot = existingQuick.SlotIndex;
                        return true;
                    }
                }

                var existing = _db.FindItemByTemplateId(connection, transaction, characterId, InventoryListType.Main, itemTemplateId);
                if (existing != null && (metadata.StackLimit <= 0 || existing.StackCount + stackCount <= metadata.StackLimit))
                {
                    _db.UpdateStackCount(connection, transaction, existing.ItemUid, existing.StackCount + stackCount);
                    assignedSlot = existing.SlotIndex;
                    return true;
                }
            }

            int slotStart, slotEnd;
            metadata.GetSlotRange(out slotStart, out slotEnd);

            if (isConsumable)
            {
                var quickSlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Main, QuickSlotStart, QuickSlotEnd);
                if (quickSlot >= 0)
                {
                    _db.InsertCharacterItem(
                        connection, transaction, characterId, InventoryListType.Main, (short)quickSlot,
                        itemTemplateId, metadata.ItemKind, stackCount, stackCount,
                        metadata.Durability, 0, 0, 0, 0, 0, "{}");
                    assignedSlot = (short)quickSlot;
                    return true;
                }
            }

            var targetSlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Main, slotStart, slotEnd);
            if (targetSlot < 0)
                return false;

            var qualitySeed = InventoryDbPrimitives.GenerateInstanceValue(itemTemplateId, targetSlot);
            var dbStackCount = metadata.IsStackable ? stackCount : qualitySeed;
            var dbInstanceValue = metadata.IsStackable ? stackCount : qualitySeed;
            _db.InsertCharacterItem(
                connection, transaction, characterId, InventoryListType.Main, (short)targetSlot,
                itemTemplateId, metadata.ItemKind, dbStackCount, dbInstanceValue,
                metadata.Durability, 0, 0, 0, metadata.IsStackable ? 0 : -1, 0, "{}");
            assignedSlot = (short)targetSlot;
            return true;
        }

        public bool TrySellItem(InventoryListType listType, short slotIndex, short sellCount, out InventoryMutationResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _shopStore.TrySellItem(connection, transaction, _context.CharacterId, _context.AccountId, listType, slotIndex, sellCount, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        public bool TryEnchantByBead(EnchantByBeadCommand command, out EnchantByBeadResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _enchantStore.TryEnchantByBead(connection, transaction, _context.CharacterId, _context.AccountId, command, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        public bool TryMoveItem(InventoryMoveRequest request, out InventoryMoveResult result)
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

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var source = _db.LoadItemRecord(connection, transaction, _context.CharacterId, dbSrcList, request.SourceSlotIndex);
                var destination = _db.LoadItemRecord(connection, transaction, _context.CharacterId, dbDstList, request.DestinationSlotIndex);

                FileLogger.Log($"  [MoveItem] source={(source != null ? $"uid={source.ItemUid} kind={source.ItemKind} tmpl=0x{source.ItemTemplateId:X8}" : "null")}, destination={(destination != null ? $"uid={destination.ItemUid} kind={destination.ItemKind} tmpl=0x{destination.ItemTemplateId:X8}" : "null")}");

                if (request.DestinationListType == InventoryListType.Equipment)
                {
                    var outcome = _equipStore.HandleEquipSlotMove(connection, transaction, _context.CharacterId, _context.AccountId, request, source, dbSrcList);
                    bool changed = outcome == EquipOutcome.Equipped || outcome == EquipOutcome.Unequipped;
                    if (changed)
                        transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount, mutated: changed);
                    result.AckError = outcome == EquipOutcome.ReverseError;
                    return true;
                }
                if (request.SourceListType == InventoryListType.Equipment)
                {
                    bool ok = _equipStore.HandleUnequipFromSlot(connection, transaction, _context.CharacterId, _context.AccountId, request.SourceSlotIndex);
                    if (ok) transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount, mutated: ok);
                    return true;
                }

                if (source == null)
                {
                    if (destination != null)
                    {
                        FileLogger.Log($"  [MoveItem] MOVE(empty-src): dst uid={destination.ItemUid} tmpl=0x{destination.ItemTemplateId:X8} → ({dbSrcList},{request.SourceSlotIndex})");
                        _db.UpdateItemPosition(connection, transaction, destination.ItemUid, dbSrcList, request.SourceSlotIndex);
                        _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "move_itemspace", destination, dbSrcList, request.SourceSlotIndex, request.MoveCount);
                        transaction.Commit();
                        result = CreateMoveResult(request, request.MoveCount);
                        return true;
                    }
                    FileLogger.Log($"  [MoveItem] FAIL: source is null at dbList={dbSrcList} slot={request.SourceSlotIndex} (dstInstanceValue={request.DestinationInstanceValue})");
                    return false;
                }

                if (!CanMoveToListType(source.ItemKind, request.DestinationListType))
                {
                    FileLogger.Log($"  [MoveItem] FAIL: CanMoveToListType({source.ItemKind}, {request.DestinationListType}) = false");
                    return false;
                }
                var moveCount = NormalizeMoveCount(source, request.MoveCount);

                if (CanStack(source, destination) && moveCount > 0)
                {
                    _db.UpdateStackCount(connection, transaction, destination.ItemUid, destination.StackCount + moveCount);

                    if (moveCount == source.StackCount)
                        _db.DeleteItem(connection, transaction, source.ItemUid);
                    else
                        _db.UpdateStackCount(connection, transaction, source.ItemUid, source.StackCount - moveCount);

                    _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
                    transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount);
                    return true;
                }

                if (source.ItemKind == "stackable" && moveCount > 0 && moveCount < source.StackCount && destination == null)
                {
                    _db.UpdateStackCount(connection, transaction, source.ItemUid, source.StackCount - moveCount);
                    _db.InsertSplitItem(connection, transaction, _context.CharacterId, source, dbDstList, request.DestinationSlotIndex, moveCount);
                    _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
                    transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount);
                    return true;
                }

                if (destination == null)
                {
                    FileLogger.Log($"  [MoveItem] MOVE: src uid={source.ItemUid} kind={source.ItemKind} tmpl=0x{source.ItemTemplateId:X8} → ({dbDstList},{request.DestinationSlotIndex})");
                    _db.UpdateItemPosition(connection, transaction, source.ItemUid, dbDstList, request.DestinationSlotIndex);
                    _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
                    transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount);
                    return true;
                }

                if (!CanSwap(source, destination))
                    return false;

                FileLogger.Log($"  [MoveItem] SWAP: src uid={source.ItemUid} kind={source.ItemKind} tmpl=0x{source.ItemTemplateId:X8} ↔ dst uid={destination.ItemUid} kind={destination.ItemKind} tmpl=0x{destination.ItemTemplateId:X8}");
                _db.SwapItems(connection, transaction, source, destination);
                _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
                transaction.Commit();
                result = CreateMoveResult(request, request.MoveCount);
                return true;
            }
        }

        public bool TrySortItems(int characterId, InventoryListType listType, byte category)
        {
            if (!IsSupportedSortListType(listType))
                return false;

            var segmentMap = GetSortSegmentMap(listType);
            if (!segmentMap.TryGetValue(category, out var range))
                return true;

            var (start, end) = range;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
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
                            items.Add(reader.GetInt64(0));
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

                    short newSlot = start;
                    foreach (var uid in items)
                    {
                        using (var upd = connection.CreateCommand())
                        {
                            upd.Transaction = transaction;
                            upd.CommandText = "UPDATE character_items SET slot_index = @slot WHERE item_uid = @uid";
                            upd.Parameters.AddWithValue("@slot", (int)newSlot);
                            upd.Parameters.AddWithValue("@uid", uid);
                            upd.ExecuteNonQuery();
                        }
                        newSlot++;
                    }
                }
                transaction.Commit();
                return true;
            }
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


        private bool HasSeedData(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(1) FROM character_items WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", _context.CharacterId);
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
        }

        private void SeedInitialSnapshot(SqliteConnection connection, CharacterItemListSnapshot snapshot)
        {
            using (var transaction = connection.BeginTransaction())
            {
                _equipStore.UpsertContainerState(connection, transaction, _context.CharacterId, _context.AccountId, InventoryListType.Main, snapshot.MainListParam16);
                _equipStore.UpsertContainerState(connection, transaction, _context.CharacterId, _context.AccountId, InventoryListType.Avatar, snapshot.AvatarListParam16);
                _equipStore.UpsertContainerState(connection, transaction, _context.CharacterId, _context.AccountId, InventoryListType.PersonalCargo, snapshot.PersonalCargoListParam16);
                _equipStore.UpsertContainerState(connection, transaction, _context.CharacterId, _context.AccountId, InventoryListType.Pet, 0);
                _equipStore.UpsertAccountCargoState(connection, transaction, _context.CharacterId, _context.AccountId, snapshot.AccountCargoState);

                foreach (var item in snapshot.MainItems)
                    _db.InsertCommonItem(connection, transaction, _context.CharacterId, InventoryListType.Main, item);

                foreach (var item in snapshot.AvatarItems)
                    _db.InsertAvatarItem(connection, transaction, _context.CharacterId, item);

                foreach (var item in snapshot.PersonalCargoItems)
                    _db.InsertCommonItem(connection, transaction, _context.CharacterId, InventoryListType.PersonalCargo, item);

                foreach (var item in snapshot.PetItems)
                    _db.InsertPetItem(connection, transaction, _context.CharacterId, item);

                foreach (var item in snapshot.AccountCargoItems)
                    _db.InsertAccountCargoItem(connection, transaction, _context.CharacterId, _context.AccountId, item);

                transaction.Commit();
            }
        }

        public void SaveEquipListBlob(byte[] blob)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                _equipStore.SaveEquipListBlob(connection, transaction, _context.CharacterId, _context.AccountId, blob);
                transaction.Commit();
            }
        }

        public void SeedNewCharacterEquipment((short slot, int itemId)[] equipment)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                _equipStore.SeedNewCharacterEquipment(connection, transaction, _context.CharacterId, _context.AccountId, equipment);
                transaction.Commit();
            }
        }

        private static ushort GetListParam(Dictionary<InventoryListType, ushort> states, InventoryListType listType)
        {
            return states.TryGetValue(listType, out var value) ? value : (ushort)0;
        }

        internal static AvatarInventoryItem CreateDefaultAvatarItem(short slotIndex, int avatarItemId, byte optionValue)
        {
            return new AvatarInventoryItem
            {
                SlotIndex = slotIndex,
                AvatarItemId = avatarItemId,
                OptionValue = optionValue,
                UnknownFixed30 = DefaultAvatarUnknownFixed30,
                UnknownFixed4 = DefaultAvatarUnknownFixed4,
            };
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

        private static bool IsSupportedMoveListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Pet;
        }

        internal static InventoryListType MapToDbListType(InventoryListType listType)
        {
            if (listType == InventoryListType.Equipment)
                return InventoryListType.Avatar;
            return listType;
        }

            internal static bool IsSupportedDeleteOrSellListType(InventoryListType listType)
            {
                return listType == InventoryListType.Main
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Pet;
            }

        private static bool IsSupportedSortListType(InventoryListType listType)
        {
            return IsSupportedMoveListType(listType);
        }

        internal static bool CanMoveToListType(string itemKind, InventoryListType destinationListType)
        {
            if (destinationListType == InventoryListType.Main
                || destinationListType == InventoryListType.Avatar
                || destinationListType == InventoryListType.Equipment
                || destinationListType == InventoryListType.PersonalCargo)
                return true;

            if (itemKind == "pet" && destinationListType == InventoryListType.Pet)
                return true;

            return false;
        }

        private static bool CanSwap(ItemRecord source, ItemRecord destination)
        {
            return CanMoveToListType(source.ItemKind, destination.ListType)
                && CanMoveToListType(destination.ItemKind, source.ListType);
        }

        private static bool CanStack(ItemRecord source, ItemRecord destination)
        {
            return source != null
                && destination != null
                && source.ItemKind == "stackable"
                && destination.ItemKind == "stackable"
                && source.ItemTemplateId == destination.ItemTemplateId;
        }

        private static int NormalizeMoveCount(ItemRecord source, int requestedMoveCount)
        {
            if (source.ItemKind != "stackable")
                return 1;

            if (requestedMoveCount <= 0 || requestedMoveCount > source.StackCount)
                return source.StackCount;

            return requestedMoveCount;
        }

        internal static int NormalizeRemovalCount(ItemRecord source, short requestedCount)
        {
            if (source.ItemKind != "stackable")
                return 1;

            if (requestedCount <= 0 || requestedCount >= source.StackCount)
                return source.StackCount;

            return requestedCount;
        }

        public bool TryBuyCeraShopItem(int productId, int buyCount, out InventoryMutationResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _shopStore.TryBuyCeraShopItem(connection, transaction, _context.CharacterId, _context.AccountId, productId, buyCount, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        // 宠物判定: 物品在 equipment.lst 且 .equ 的 [equipment type] 为 [creature]。
        // CreatureExtraResolver 对不在 equipment.lst 的物品会抛异常, 这里吞掉返回 false。
        internal static bool IsCreatureItem(int itemTemplateId)
        {
            try
            {
                return CreatureExtraResolver.HasCreatureExtra(itemTemplateId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [CeraShopBuy] IsCreatureItem(0x{itemTemplateId:X8}) 判定失败, 视为非宠物: {ex.Message}");
                return false;
            }
        }

        internal static ItemRecord ReadItemRecord(SqliteDataReader reader)
        {
            return new ItemRecord
            {
                ItemUid = reader.GetInt64(0),
                ListType = (InventoryListType)reader.GetInt32(1),
                SlotIndex = Convert.ToInt16(reader.GetInt32(2), CultureInfo.InvariantCulture),
                ItemTemplateId = reader.GetInt32(3),
                ItemKind = reader.GetString(4),
                StackCount = reader.GetInt32(5),
                InstanceValue = reader.GetInt32(6),
                Durability = Convert.ToUInt16(reader.GetInt32(7), CultureInfo.InvariantCulture),
                SealFlag = Convert.ToByte(reader.GetInt32(8), CultureInfo.InvariantCulture),
                OptionValue = Convert.ToByte(reader.GetInt32(9), CultureInfo.InvariantCulture),
                ExpireTime = reader.GetInt32(10),
                Marker16 = reader.GetInt32(11),
                PetSerialOrHandle = reader.GetInt32(12),
                ExtraJson = reader.IsDBNull(13) ? "{}" : reader.GetString(13),
            };
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

        internal sealed class ItemRecord
        {
            public long ItemUid { get; set; }

            public InventoryListType ListType { get; set; }

            public short SlotIndex { get; set; }

            public int ItemTemplateId { get; set; }

            public string ItemKind { get; set; } = "unknown";

            public int StackCount { get; set; }

            public int InstanceValue { get; set; }

            public ushort Durability { get; set; }

            public byte SealFlag { get; set; }

            public byte OptionValue { get; set; }

            public int ExpireTime { get; set; }

            public int Marker16 { get; set; }

            public int PetSerialOrHandle { get; set; }

            public string ExtraJson { get; set; } = "{}";
        }

        internal sealed class WalletState
        {
            public int Gold { get; set; }

            public int Sp { get; set; }

            public int Coin { get; set; }

            public int TokenCera { get; set; }

            public int HappyTokenCera { get; set; }
        }
    }
}
