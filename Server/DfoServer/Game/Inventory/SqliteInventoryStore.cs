using DfoServer.Game.Currency;
using DfoServer.Infrastructure;
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

        public SqliteInventoryStore(string databasePath, string schemaFilePath)
        {
            if (databasePath == null) throw new ArgumentNullException(nameof(databasePath));
            if (schemaFilePath == null) throw new ArgumentNullException(nameof(schemaFilePath));

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
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
                var snapshot = new CharacterItemListSnapshot();
                var listParams = _equipStore.LoadContainerState(connection, null, characterId, accountId);
                snapshot.MainListParam16 = GetListParam(listParams, InventoryListType.Main);
                snapshot.AvatarListParam16 = GetListParam(listParams, InventoryListType.Avatar);
                snapshot.PersonalCargoListParam16 = NormalizePersonalCargoListParam(GetListParam(listParams, InventoryListType.PersonalCargo));
                snapshot.AccountCargoState = _equipStore.LoadAccountCargoState(connection, null, characterId, accountId);

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
                    var count = _equipStore.DeleteExpiredRentalEquipment(connection, transaction, characterId, accountId);
                    if (count > 0) transaction.Commit();
                    return count;
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
        internal bool TryDeleteItemCore(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId, InventoryListType listType, InventoryListType dbListType,
            short slotIndex, short deleteCount, out InventoryMutationResult result)
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

            var stackedCount = GetStackedRecordCount(item);
            var appliedCount = NormalizeRemovalCount(item, deleteCount);
            var itemRemainingCount = Math.Max(0, stackedCount - appliedCount);
            var isStackCountedRecord = IsStackCountedRecord(item);
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
                    _db.InsertCommonItem(connection, transaction, characterId, InventoryListType.Main, item);

                foreach (var item in snapshot.AvatarItems)
                    _db.InsertAvatarItem(connection, transaction, characterId, item);

                foreach (var item in snapshot.PersonalCargoItems)
                    _db.InsertCommonItem(connection, transaction, characterId, InventoryListType.PersonalCargo, item);

                foreach (var item in snapshot.PetItems)
                    _db.InsertPetItem(connection, transaction, characterId, item);

                foreach (var item in snapshot.AccountCargoItems)
                    _db.InsertAccountCargoItem(connection, transaction, accountId, item);

                transaction.Commit();
            }
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
