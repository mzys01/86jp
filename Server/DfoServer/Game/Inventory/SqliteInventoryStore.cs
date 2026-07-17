using DfoServer.Game.Currency;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.ItemUpgrade;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore : IInventoryStore
    {
        internal const int DefaultAvatarUnknownFixed30 = 0x00001E00;
        internal const ushort DefaultAvatarUnknownFixed4 = 0x0400;
        internal const ushort DefaultPersonalCargoCapacity = 8;
        internal static readonly object StackableItemCacheLock = new object();
        internal static readonly Dictionary<int, PvfLib.StackableItemFile> StackableItemCache = new Dictionary<int, PvfLib.StackableItemFile>();

        private readonly string _connectionString;
        internal string ConnectionString => _connectionString;
        private readonly InventoryAuditLogger _auditLogger;
        internal readonly InventoryDbPrimitives _db;
        private readonly InventoryEnchantStore _enchantStore;
        private readonly InventoryItemUpgradeStore _itemUpgradeStore;
        private readonly InventoryPackageStore _packageStore;
        private readonly InventoryShopStore _shopStore;
        internal readonly InventoryEquipmentStore _equipStore;
        private readonly IRentalTimeProvider _rentalTimeProvider;

        public SqliteInventoryStore(string databasePath, string schemaFilePath, IRentalTimeProvider rentalTimeProvider = null)
        {
            if (databasePath == null) throw new ArgumentNullException(nameof(databasePath));
            if (schemaFilePath == null) throw new ArgumentNullException(nameof(schemaFilePath));

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            _connectionString = Infrastructure.SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
            _rentalTimeProvider = rentalTimeProvider ?? SystemRentalTimeProvider.Instance;
            _auditLogger = new InventoryAuditLogger();
            _db = new InventoryDbPrimitives();
            _enchantStore = new InventoryEnchantStore(_db, _auditLogger);
            _itemUpgradeStore = new InventoryItemUpgradeStore(_db, _auditLogger);
            _packageStore = new InventoryPackageStore(_db, _auditLogger);
            _shopStore = new InventoryShopStore(_db, _auditLogger);
            _equipStore = new InventoryEquipmentStore(_db, _auditLogger);
        }

        public int CountItem(int characterId, int itemTemplateId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT COALESCE(SUM(stack_count), 0) FROM character_items WHERE character_id = @cid AND list_type = 0 AND item_template_id = @tid",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@tid", itemTemplateId);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        public void EnsureDatabase(int characterId, int accountId, CharacterItemListSnapshot seedSnapshot)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();

                if (HasSeedData(connection, characterId))
                    return;

                SeedInitialSnapshot(connection, characterId, accountId, seedSnapshot);
            }
        }

        public void EnsureContainerState(int characterId, int accountId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
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
                        _equipStore.UpsertContainerState(connection, tx, characterId, accountId, InventoryListType.Main, 24);
                        _equipStore.UpsertContainerState(connection, tx, characterId, accountId, InventoryListType.Avatar, 0);
                        _equipStore.UpsertContainerState(connection, tx, characterId, accountId, InventoryListType.PersonalCargo, DefaultPersonalCargoCapacity);
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
    'character', @cid, @cid, 0, @slotIndex, @itemId, 'stackable',
    0, 0, 0, 0, 0, 0, 0,
    0, '{}');";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slotIndex", Game.ReviveCoin.ReviveCoinService.WalletSlot);
                cmd.Parameters.AddWithValue("@itemId", Game.ReviveCoin.ReviveCoinService.ItemId);
                cmd.ExecuteNonQuery();
            }
        }

        public CharacterItemListSnapshot LoadCharacterItemListSnapshot(int characterId, int accountId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                NormalizeRentalInventoryRows(connection, characterId, _rentalTimeProvider.UtcNowUnixSeconds());
                using (var repairTransaction = connection.BeginTransaction())
                {
                    RepairPetCreatureItemListSlotConflict(connection, repairTransaction, characterId);
                    RepairEquippedPetCreatureExtraRaw(connection, repairTransaction, characterId);
                    repairTransaction.Commit();
                }

                var snapshot = new CharacterItemListSnapshot();
                var listParams = _equipStore.LoadContainerState(connection, null, characterId, accountId);
                snapshot.MainListParam16 = GetListParam(listParams, InventoryListType.Main);
                snapshot.AvatarListParam16 = GetListParam(listParams, InventoryListType.Avatar);
                snapshot.PersonalCargoListParam16 = NormalizePersonalCargoListParam(GetListParam(listParams, InventoryListType.PersonalCargo));
                snapshot.AccountCargoState = _equipStore.LoadAccountCargoState(connection, null, characterId, accountId);
                var petCreatureExtraBySerial = LoadPetCreatureExtraJsonMap(connection, null, characterId);

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, equipment_lock_id, extra_json
FROM character_items
WHERE character_id = @characterId
ORDER BY list_type, slot_index;";
                    command.Parameters.AddWithValue("@characterId", characterId);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var listType = (InventoryListType)reader.GetInt32(0);
                            var extraJson = reader.IsDBNull(13) ? "{}" : reader.GetString(13);

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
                                    if (IsCreatureItem(reader.GetInt32(2)))
                                    {
                                        var petSerial = reader.GetInt32(11);
                                        petCreatureExtraBySerial.TryGetValue(petSerial, out var storedExtraJson);
                                        extraJson = MergePetCreatureInstanceExtraJsonForRead(storedExtraJson, extraJson);
                                    }
                                    snapshot.PetItems.Add(InventoryItemCodec.ReadPetItem(reader, extraJson));
                                    break;
                            }
                        }
                    }
                }

                using (var acCmd = connection.CreateCommand())
                {
                    acCmd.CommandText = @"
SELECT 12 AS list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, 0 AS pet_serial_or_handle, 0 AS equipment_lock_id, extra_json
FROM account_cargo_items
WHERE account_id = @accountId
ORDER BY slot_index;";
                    acCmd.Parameters.AddWithValue("@accountId", accountId);
                    using (var reader = acCmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.AccountCargoItems.Add(InventoryItemCodec.ReadCommonItem(reader, reader.IsDBNull(13) ? "{}" : reader.GetString(13)));
                    }
                }

                // 读取账号级晶块, 合成虚拟 slot 条目添加到 MainItems
                var cubeFragments = CurrencyService.LoadCubeFragments(connection, null, accountId);
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

        public int DeleteExpiredRentalEquipment(int characterId, int accountId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var count = _equipStore.DeleteExpiredRentalEquipment(
                        connection,
                        transaction,
                        characterId,
                        accountId,
                        _rentalTimeProvider.UtcNowUnixSeconds());
                    if (count > 0) transaction.Commit();
                    return count;
                }
            }
        }

        public RentalInfoSnapshot RebuildRentalInfoFromInventory(
            int characterId,
            int accountId,
            RentalInfoSnapshot storedRentalInfo)
        {
            // 登录时以背包/装备栏为权威状态重建租赁栏，避免旧 0x0357 内部存储漏项。
            var rebuilt = new RentalInfoSnapshot();
            if (storedRentalInfo != null)
                rebuilt.RentalId = storedRentalInfo.RentalId;

            if (characterId <= 0)
                return rebuilt;

            var now = _rentalTimeProvider.UtcNowUnixSeconds();
            var shopIdByInventoryId = BuildRentalShopIndex(storedRentalInfo);
            var shopIdByExpireTime = BuildRentalShopExpireIndex(storedRentalInfo);
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                NormalizeRentalInventoryRows(connection, characterId, now);
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT item_template_id, expire_time, slot_index
FROM character_items
WHERE character_id = @characterId
  AND list_type = @listType
  AND expire_time > @now
ORDER BY slot_index;";
                    cmd.Parameters.AddWithValue("@characterId", characterId);
                    cmd.Parameters.AddWithValue("@listType", (int)InventoryListType.Main);
                    cmd.Parameters.AddWithValue("@now", now);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var inventoryTemplateId = reader.GetInt32(0);
                            var expireTime = reader.GetInt32(1);
                            if (!TryResolveRentalShopId(shopIdByInventoryId, shopIdByExpireTime, inventoryTemplateId, expireTime, out var shopId))
                                continue;

                            rebuilt.UpsertItem(shopId, unchecked((uint)inventoryTemplateId), unchecked((uint)expireTime));
                        }
                    }
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT item_id, expire_time, slot
FROM character_equipped_entries
WHERE character_id = @characterId
  AND expire_time > @now
ORDER BY slot;";
                    cmd.Parameters.AddWithValue("@characterId", characterId);
                    cmd.Parameters.AddWithValue("@now", now);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var inventoryTemplateId = reader.GetInt32(0);
                            var expireTime = reader.GetInt32(1);
                            if (!TryResolveRentalShopId(shopIdByInventoryId, shopIdByExpireTime, inventoryTemplateId, expireTime, out var shopId))
                                continue;

                            rebuilt.UpsertItem(shopId, unchecked((uint)inventoryTemplateId), unchecked((uint)expireTime));
                        }
                    }
                }
            }

            return rebuilt;
        }

        private static Dictionary<uint, uint> BuildRentalShopIndex(RentalInfoSnapshot storedRentalInfo)
        {
            var map = new Dictionary<uint, uint>();
            if (storedRentalInfo == null)
                return map;

            foreach (var item in storedRentalInfo.Items)
            {
                if (item == null || item.ItemId == 0 || item.InventoryTemplateId == 0)
                    continue;

                map[item.InventoryTemplateId] = item.ItemId;
            }

            return map;
        }

        private static Dictionary<uint, uint> BuildRentalShopExpireIndex(RentalInfoSnapshot storedRentalInfo)
        {
            var map = new Dictionary<uint, uint>();
            if (storedRentalInfo == null)
                return map;

            foreach (var item in storedRentalInfo.Items)
            {
                if (item == null || item.ItemId == 0 || item.ExpireTime == 0)
                    continue;

                map[item.ExpireTime] = item.ItemId;
            }

            return map;
        }

        private static bool TryResolveRentalShopId(
            Dictionary<uint, uint> shopIdByInventoryId,
            Dictionary<uint, uint> shopIdByExpireTime,
            int inventoryTemplateId,
            int expireTime,
            out uint shopId)
        {
            shopId = 0;
            if (!RentalWeaponInventoryMapper.IsValidInventoryTemplate(inventoryTemplateId))
                return false;

            var inventoryId = unchecked((uint)inventoryTemplateId);
            if (shopIdByInventoryId.TryGetValue(inventoryId, out shopId) && shopId != 0)
                return true;

            var expireKey = unchecked((uint)expireTime);
            if (shopIdByExpireTime.TryGetValue(expireKey, out shopId) && shopId != 0)
                return true;

            shopId = inventoryId;
            return true;
        }

        private static void NormalizeRentalInventoryRows(SqliteConnection connection, int characterId, uint now)
        {
            // 历史数据可能把租赁装备写成普通装备或 instance_value 非零；读取前统一成客户端可显示形态。
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT item_uid, item_template_id
FROM character_items
WHERE character_id = @characterId
  AND list_type = @listType
  AND expire_time > @now;";
                cmd.Parameters.AddWithValue("@characterId", characterId);
                cmd.Parameters.AddWithValue("@listType", (int)InventoryListType.Main);
                cmd.Parameters.AddWithValue("@now", now);
                var rows = new List<(long itemUid, int itemTemplateId)>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var itemTemplateId = reader.GetInt32(1);
                        if (!RentalWeaponInventoryMapper.IsValidInventoryTemplate(itemTemplateId))
                            continue;

                        rows.Add((reader.GetInt64(0), itemTemplateId));
                    }
                }

                foreach (var row in rows)
                {
                    using (var update = connection.CreateCommand())
                    {
                        update.CommandText = @"
UPDATE character_items
SET item_kind = 'special',
    stack_count = @qualitySeed,
    instance_value = 0,
    durability = @durability,
    marker_16 = -1,
    extra_json = CASE WHEN extra_json IS NULL OR extra_json = '{}' THEN @extraJson ELSE extra_json END,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @itemUid;";
                        update.Parameters.AddWithValue("@qualitySeed", RentalWeaponRequestCodec.RentalWeaponQualitySeed);
                        update.Parameters.AddWithValue("@durability", RentalWeaponRequestCodec.RentalWeaponDurability);
                        update.Parameters.AddWithValue("@extraJson", "{\"extData0\":0,\"prefixData0E\":\"0000000000000000\",\"middleData1A\":\"0000000000000000000000000000000000\",\"tailData2F\":\"00000000000000000000000000000000000000000000000000000000000000000000000000\"}");
                        update.Parameters.AddWithValue("@itemUid", row.itemUid);
                        update.ExecuteNonQuery();
                    }
                }
            }
        }

        public bool TryDeleteItem(int characterId, int accountId, InventoryListType listType, short slotIndex, short deleteCount, out InventoryMutationResult result)
        {
            result = null;
            if (!IsSupportedDeleteOrSellListType(listType))
                return false;

            var dbListType = MapToDbListType(listType);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    if (CurrencyService.IsCubeFragmentSlot(slotIndex))
                    {
                        var itemId = CurrencyService.GetCubeFragmentItemIdFromSlot(slotIndex);
                        if (itemId <= 0)
                            return false;

                        var cubes = CurrencyService.LoadCubeFragments(connection, transaction, accountId);
                    var currentCount = 0;
                    foreach (var (id, slot, count) in cubes)
                        if (id == itemId) { currentCount = count; break; }
                    if (currentCount < deleteCount)
                        return false;

                    CurrencyService.AddCubeFragment(connection, transaction, accountId, itemId, -deleteCount);
                    var remainingCount = currentCount - deleteCount;

                    var cubeWallet = _db.LoadWallet(connection, transaction, characterId);
                    transaction.Commit();

                    result = new InventoryMutationResult
                    {
                        ListType = listType,
                        SlotIndex = slotIndex,
                        ItemTemplateId = itemId,
                        RemainingStackCount = remainingCount,
                        InstanceValue = remainingCount,
                        Durability = 0,
                        UpdatedGold = cubeWallet.Gold,
                        UpdatedSp = cubeWallet.Sp,
                        UpdatedCoin = cubeWallet.Cera,
                        RequestedCount = deleteCount,
                        AppliedCount = deleteCount,
                    };
                    return true;
                }

                    // 金币槽(主背包 slot=0, item_template_id=0): 客户端用 DELETE_ITEM 同步"消耗金币"
                    // (例如消耗金币的技能), 走通用 TryDeleteItemCore 会把整行物理删除导致余额归零。
                    // 这里像 cube fragment 一样按数量增减, 而非删行。
                    // 只对主背包(Main)生效; avatar/equipment/pet 的 slot=0 不是金币, 不能误判。
                    if (slotIndex == 0 && listType == InventoryListType.Main)
                    {
                        if (deleteCount <= 0)
                            return false;

                        if (!CurrencyService.TrySpendGold(connection, transaction, characterId, deleteCount))
                            return false;

                        var goldWallet = _db.LoadWallet(connection, transaction, characterId);
                        transaction.Commit();

                        result = new InventoryMutationResult
                        {
                            ListType = listType,
                            SlotIndex = slotIndex,
                            ItemTemplateId = 0,
                            RemainingStackCount = goldWallet.Gold,
                            InstanceValue = goldWallet.Gold,
                            Durability = 0,
                            UpdatedGold = goldWallet.Gold,
                            UpdatedSp = goldWallet.Sp,
                            UpdatedCoin = goldWallet.Cera,
                            RequestedCount = deleteCount,
                            AppliedCount = deleteCount,
                        };
                        return true;
                    }


                var ok = TryDeleteItemCore(connection, transaction, characterId, listType, dbListType, slotIndex, deleteCount, out result);
                if (ok)
                    transaction.Commit();
                return ok;
                }
            }
        }

        // 非晶块删除内核: 调用方持有连接与事务并负责提交(失败不提交=回滚)。
        // 有期限的 PVF stackable 会以 special 形态持久化；已验证其 PVF 类型的调用方
        // 可显式保留逐颗堆叠语义，不改变其他 special 的通用删除行为。
        internal bool TryDeleteItemCore(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId, InventoryListType listType, InventoryListType dbListType,
            short slotIndex, short deleteCount, out InventoryMutationResult result,
            bool treatSourceAsStackable = false)
        {
            result = null;

            var item = _db.LoadItemRecord(connection, transaction, characterId, dbListType, slotIndex);
            if (item == null)
                return false;

            if (IsEquipmentItemLocked(connection, transaction, characterId, item))
            {
                FileLogger.Log($"  [DeleteItem] REJECT: locked item listType={dbListType} slot={slotIndex} lockId={item.EquipmentLockId}");
                return false;
            }

            var isStackCountedRecord = treatSourceAsStackable || IsStackCountedRecord(item);
            var stackedCount = treatSourceAsStackable
                ? Math.Max(0, item.StackCount)
                : GetStackedRecordCount(item);
            var appliedCount = isStackCountedRecord
                ? deleteCount <= 0 || deleteCount >= stackedCount
                    ? stackedCount
                    : deleteCount
                : 1;
            var itemRemainingCount = Math.Max(0, stackedCount - appliedCount);
            var satietyMutation = default(PetSatietyMutation);
            if (isStackCountedRecord && appliedCount < stackedCount)
            {
                if (IsPetConsumableRecord(item))
                    _db.UpdatePetStackCount(connection, transaction, item.ItemUid, itemRemainingCount);
                else
                    _db.UpdateStackCount(connection, transaction, item.ItemUid, itemRemainingCount);
            }
            else
            {
                _db.DeleteItem(connection, transaction, item.ItemUid);
            }

            if (IsPetConsumableRecord(item))
                satietyMutation = ApplyPetFoodSatiety(connection, transaction, characterId, item.ItemTemplateId);

            _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, item, appliedCount);
            var wallet = _db.LoadWallet(connection, transaction, characterId);

            result = new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = item.ItemTemplateId,
                RemainingStackCount = itemRemainingCount,
                InstanceValue = isStackCountedRecord ? itemRemainingCount : item.InstanceValue,
                Durability = item.Durability,
                UpdatedGold = wallet.Gold,
                UpdatedSp = wallet.Sp,
                UpdatedCoin = wallet.Cera,
                RequestedCount = deleteCount,
                AppliedCount = (short)appliedCount,
                PetCreatureKey = satietyMutation.CreatureKey,
                PetSatietyBefore = satietyMutation.Before,
                PetSatietyAfter = satietyMutation.After,
                PetSatietyChanged = satietyMutation.Changed,
            };
            return true;
        }

        private static bool HasSeedData(SqliteConnection connection, int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(1) FROM character_items WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", characterId);
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
        }

        private void SeedInitialSnapshot(SqliteConnection connection, int characterId, int accountId, CharacterItemListSnapshot snapshot)
        {
            using (var transaction = connection.BeginTransaction())
            {
                _equipStore.UpsertContainerState(connection, transaction, characterId, accountId, InventoryListType.Main, snapshot.MainListParam16);
                _equipStore.UpsertContainerState(connection, transaction, characterId, accountId, InventoryListType.Avatar, snapshot.AvatarListParam16);
                _equipStore.UpsertContainerState(connection, transaction, characterId, accountId, InventoryListType.PersonalCargo, NormalizePersonalCargoListParam(snapshot.PersonalCargoListParam16));
                _equipStore.UpsertContainerState(connection, transaction, characterId, accountId, InventoryListType.Pet, 0);
                _equipStore.UpsertAccountCargoState(connection, transaction, characterId, accountId, snapshot.AccountCargoState);

                foreach (var item in snapshot.MainItems)
                    InsertSnapshotCommonItem(connection, transaction, characterId, InventoryListType.Main, item);

                foreach (var item in snapshot.AvatarItems)
                    InsertSnapshotAvatarItem(connection, transaction, characterId, item);

                foreach (var item in snapshot.PersonalCargoItems)
                    InsertSnapshotCommonItem(connection, transaction, characterId, InventoryListType.PersonalCargo, item);

                foreach (var item in snapshot.PetItems)
                    InsertSnapshotPetItem(connection, transaction, characterId, item);

                foreach (var item in snapshot.AccountCargoItems)
                    InsertSnapshotAccountCargoItem(connection, transaction, accountId, item);

                transaction.Commit();
            }
        }

        private void InsertSnapshotCommonItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, InventoryListType listType, CommonInventoryItem item)
        {
            _db.InsertCharacterItem(
                connection,
                transaction,
                characterId,
                listType,
                item.SlotIndex,
                item.ItemTemplateId,
                InventoryItemCodec.InferCommonItemKind(item),
                item.CountOrInstanceValue,
                item.CountOrInstanceValue,
                item.Durability,
                item.SealFlag,
                0,
                item.ExpireTime,
                item.Marker16,
                0,
                InventoryItemCodec.SerializeCommon(item),
                item.EquipmentLockId);
        }

        private void InsertSnapshotAvatarItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, AvatarInventoryItem item)
        {
            _db.InsertCharacterItem(
                connection,
                transaction,
                characterId,
                InventoryListType.Avatar,
                item.SlotIndex,
                item.AvatarItemId,
                "avatar",
                0,
                0,
                0,
                0,
                item.OptionValue,
                0,
                item.UnknownFixed30,
                0,
                InventoryItemCodec.SerializeAvatar(item));
        }

        private void InsertSnapshotPetItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, PetInventoryItem item)
        {
            _db.InsertCharacterItem(
                connection,
                transaction,
                characterId,
                InventoryListType.Pet,
                item.SlotIndex,
                item.CreatureItemId,
                "pet",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                item.CreatureSerialOrHandle,
                InventoryItemCodec.SerializePet(item));
        }

        private void InsertSnapshotAccountCargoItem(SqliteConnection connection, SqliteTransaction transaction, int accountId, CommonInventoryItem item)
        {
            _db.InsertAccountCargoItem(
                connection,
                transaction,
                accountId,
                item.SlotIndex,
                item.ItemTemplateId,
                InventoryItemCodec.InferCommonItemKind(item),
                item.CountOrInstanceValue,
                item.CountOrInstanceValue,
                item.Durability,
                item.SealFlag,
                0,
                item.ExpireTime,
                item.Marker16,
                InventoryItemCodec.SerializeCommon(item));
        }

        public void SeedNewCharacterEquipment(int characterId, int accountId, (short slot, int itemId)[] equipment)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                        _equipStore.SeedNewCharacterEquipment(connection, transaction, characterId, accountId, equipment);
                    transaction.Commit();
                }
            }
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private static ushort GetListParam(Dictionary<InventoryListType, ushort> states, InventoryListType listType)
        {
            return states.TryGetValue(listType, out var value) ? value : (ushort)0;
        }

        internal static string CreateDefaultAvatarExtraJson()
        {
            return "{\"reserved0\":\"" + InventoryItemViewBytes.ToHex(new byte[5]) + "\""
                + ",\"reserved1\":\"" + InventoryItemViewBytes.ToHex(new byte[71]) + "\""
                + ",\"reserved2\":\"" + InventoryItemViewBytes.ToHex(new byte[30]) + "\""
                + ",\"unknownFixed4\":" + DefaultAvatarUnknownFixed4.ToString(CultureInfo.InvariantCulture)
                + ",\"tailData\":\"" + InventoryItemViewBytes.ToHex(new byte[7]) + "\"}";
        }

        internal static string CreateDefaultAvatarExtraJson(int itemTemplateId)
        {
            var extraJson = CreateDefaultAvatarExtraJson();
            var socketTypes = ItemMetadataResolver.ResolveAvatarDefaultSocketTypes(itemTemplateId);
            if (socketTypes == null || socketTypes.Count == 0)
                return extraJson;

            var record = new ItemRecord
            {
                ExtraJson = extraJson,
            };
            InventoryItemView.ForAvatar(record).AvatarDetail.SetSocketTypes(socketTypes);
            return record.ExtraJson;
        }

        internal static string CreateDefaultPetExtraJson()
        {
            return "{\"tailData0A\":\"" + InventoryItemViewBytes.ToHex(new byte[74]) + "\"}";
        }

            internal static bool IsSupportedDeleteOrSellListType(InventoryListType listType)
            {
                return listType == InventoryListType.Main
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Pet;
            }

        internal static int NormalizeRemovalCount(ItemRecord source, short requestedCount)
        {
            if (!IsStackCountedRecord(source))
                return 1;

            var currentCount = GetStackedRecordCount(source);
            if (requestedCount <= 0 || requestedCount >= currentCount)
                return currentCount;

            return requestedCount;
        }

        internal static bool IsStackCountedRecord(ItemRecord source)
        {
            if (source == null)
                return false;

            return source.ItemKind == "stackable" || IsPetConsumableRecord(source);
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
                EquipmentLockId = reader.FieldCount > 14
                    ? Convert.ToByte(reader.GetInt32(13), CultureInfo.InvariantCulture)
                    : (byte)0,
                ExtraJson = reader.IsDBNull(reader.FieldCount > 14 ? 14 : 13)
                    ? "{}"
                    : reader.GetString(reader.FieldCount > 14 ? 14 : 13),
            };
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

            public byte EquipmentLockId { get; set; }

            public string ExtraJson { get; set; } = "{}";
        }
    }
}
