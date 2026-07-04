using DfoServer.Game.Currency;
using DfoServer.Infrastructure;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Premium;
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
        private const int SeriaLuckItemTemplateId = 2682272;
        private const short ReviveCoinSlotIndex = 1;
        internal static readonly object StackableItemCacheLock = new object();
        internal static readonly Dictionary<int, PvfLib.StackableItemFile> StackableItemCache = new Dictionary<int, PvfLib.StackableItemFile>();

        private readonly ScopedStoreContext _context;
        private readonly InventoryAuditLogger _auditLogger;
        internal readonly InventoryDbPrimitives _db;
        private readonly InventoryEnchantStore _enchantStore;
        private readonly InventoryItemUpgradeStore _itemUpgradeStore;
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
            _itemUpgradeStore = new InventoryItemUpgradeStore(_db, _auditLogger);
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
SELECT 12 AS list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, 0 AS pet_serial_or_handle, extra_json
FROM account_cargo_items
WHERE account_id = @accountId
ORDER BY slot_index;";
                    acCmd.Parameters.AddWithValue("@accountId", _context.AccountId);
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

                ApplySeriaLuckValueToCommonItems(connection, null, snapshot.MainItems);
                return snapshot;
            }
        }

        private void ApplySeriaLuckValueToCommonItems(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<CommonInventoryItem> items)
        {
            var matchingItems = items.Where(item => item.ItemTemplateId == SeriaLuckItemTemplateId).ToList();
            if (matchingItems.Count == 0)
                return;

            var value = (byte)_db.LoadSeriaLuckValue(connection, transaction, _context.AccountId);
            foreach (var item in matchingItems)
                item.ExtData0 = value;
        }

        private CommonInventoryItem ApplySeriaLuckValueToCommonItem(SqliteConnection connection, SqliteTransaction transaction, CommonInventoryItem item)
        {
            if (item != null && item.ItemTemplateId == SeriaLuckItemTemplateId)
                item.ExtData0 = (byte)_db.LoadSeriaLuckValue(connection, transaction, _context.AccountId);

            return item;
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
                if (CurrencyService.IsCubeFragmentSlot(slotIndex))
                {
                    var itemId = CurrencyService.GetCubeFragmentItemIdFromSlot(slotIndex);
                    if (itemId <= 0)
                        return false;

                    var cubes = CurrencyService.LoadCubeFragments(connection, transaction, _context.AccountId);
                    var currentCount = 0;
                    foreach (var (id, slot, count) in cubes)
                        if (id == itemId) { currentCount = count; break; }
                    if (currentCount < deleteCount)
                        return false;

                    CurrencyService.AddCubeFragment(connection, transaction, _context.AccountId, itemId, -deleteCount);
                    var remainingCount = currentCount - deleteCount;

                    var cubeWallet = _db.LoadWallet(connection, transaction, _context.CharacterId);
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
                        UpdatedCoin = cubeWallet.Coin,
                        RequestedCount = deleteCount,
                        AppliedCount = deleteCount,
                    };
                    return true;
                }


                var item = _db.LoadItemRecord(connection, transaction, _context.CharacterId, dbListType, slotIndex);
                if (item == null)
                    return false;

                var appliedCount = NormalizeRemovalCount(item, deleteCount);
                var itemRemainingCount = Math.Max(0, item.StackCount - appliedCount);
                var isStackCountedRecord = IsStackCountedRecord(item);
                var satietyMutation = default(PetSatietyMutation);
                if (isStackCountedRecord && appliedCount < item.StackCount)
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
                    satietyMutation = ApplyPetFoodSatiety(connection, transaction, _context.CharacterId, item.ItemTemplateId);

                _auditLogger.WriteDeleteAuditLog(connection, transaction, _context.CharacterId, item, appliedCount);
                var wallet = _db.LoadWallet(connection, transaction, _context.CharacterId);
                transaction.Commit();

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
                    UpdatedCoin = wallet.Coin,
                    RequestedCount = deleteCount,
                    AppliedCount = (short)appliedCount,
                    PetCreatureKey = satietyMutation.CreatureKey,
                    PetSatietyBefore = satietyMutation.Before,
                    PetSatietyAfter = satietyMutation.After,
                    PetSatietyChanged = satietyMutation.Changed,
                };
                return true;
            }
        }

        public bool TryUsePremiumContractItem(InventoryListType listType, short slotIndex, int expectedItemTemplateId, out PremiumContractUseResult result)
        {
            result = null;
            if (listType != InventoryListType.Main)
                return false;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var item = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Main, slotIndex);
                return TryUsePremiumContractItemRecord(connection, transaction, item, expectedItemTemplateId, out result);
            }
        }

        private bool TryUsePremiumContractItemRecord(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ItemRecord item,
            int expectedItemTemplateId,
            out PremiumContractUseResult result)
        {
            result = null;
            if (item == null)
                return false;

            if (!PremiumCatalog.Load().TryGetValue(item.ItemTemplateId, out var premiumType, out _))
                return false;

            result = new PremiumContractUseResult { IsPremiumContract = true };

            if (expectedItemTemplateId > 0 && item.ItemTemplateId != expectedItemTemplateId)
                return false;

            if (!IsStackCountedRecord(item) || item.StackCount <= 0)
                return false;

            if (item.EquipmentLockId != 0)
            {
                FileLogger.Log($"  [UsePremiumContract] REJECT: locked item slot={item.SlotIndex} item=0x{item.ItemTemplateId:X8}");
                return false;
            }

            var activated = PremiumService.TryActivateContract(connection, transaction, _context.AccountId, item.ItemTemplateId, 1);
            if (activated == null)
                return false;

            const short appliedCount = 1;
            var remainingCount = Math.Max(0, item.StackCount - appliedCount);
            if (remainingCount > 0)
                _db.UpdateStackCount(connection, transaction, item.ItemUid, remainingCount);
            else
                _db.DeleteItem(connection, transaction, item.ItemUid);

            _auditLogger.WriteDeleteAuditLog(connection, transaction, _context.CharacterId, item, appliedCount);
            var wallet = _db.LoadWallet(connection, transaction, _context.CharacterId);
            transaction.Commit();

            result.PremiumType = premiumType;
            result.PremiumRemaining = activated.Value.remaining;
            result.Mutation = new InventoryMutationResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = item.SlotIndex,
                ItemTemplateId = item.ItemTemplateId,
                RemainingStackCount = remainingCount,
                InstanceValue = remainingCount,
                Durability = item.Durability,
                UpdatedGold = wallet.Gold,
                UpdatedSp = wallet.Sp,
                UpdatedCoin = wallet.Coin,
                RequestedCount = appliedCount,
                AppliedCount = appliedCount,
            };
            return true;
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
                    _db.InsertAccountCargoItem(connection, transaction, _context.AccountId, item);

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

            if (requestedCount <= 0 || requestedCount >= source.StackCount)
                return source.StackCount;

            return requestedCount;
        }

        internal static bool IsStackCountedRecord(ItemRecord source)
        {
            if (source == null)
                return false;

            return source.ItemKind == "stackable" || IsPetConsumableRecord(source);
        }

        internal static bool IsPetConsumableRecord(ItemRecord source)
        {
            if (source == null)
                return false;

            return source.ListType == InventoryListType.Pet
                && source.ItemKind == "pet"
                && source.StackCount > 0
                && source.SlotIndex >= PetConsumableSlotStart
                && source.SlotIndex <= PetConsumableSlotEnd;
        }

        private static PetSatietyMutation ApplyPetFoodSatiety(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int itemTemplateId)
        {
            if (!TryResolvePetFeedSatietyDelta(itemTemplateId, out var delta) || delta <= 0)
                return default(PetSatietyMutation);

            var activeCreatureKey = ResolveActiveCreatureKey(connection, transaction, characterId);
            if (activeCreatureKey > 0)
                return TryIncreaseCreatureSatiety(connection, transaction, characterId, activeCreatureKey, delta, out var activeMutation)
                    ? activeMutation
                    : default(PetSatietyMutation);

            // Satiety belongs to a specific creature instance. If the active creature
            // cannot be resolved, leave all creatures unchanged instead of guessing.
            return default(PetSatietyMutation);
        }

        internal static bool TryResolvePetFeedSatietyDelta(int itemTemplateId, out int delta)
        {
            delta = 0;
            var stackable = InventoryDbPrimitives.LoadStackableItem(itemTemplateId);
            if (stackable == null || string.IsNullOrWhiteSpace(stackable.StackableType))
                return false;

            var stackableType = stackable.StackableType.Replace("`", string.Empty).Trim();
            if (!stackableType.StartsWith("[feed]", StringComparison.OrdinalIgnoreCase))
                return false;

            var values = ParsePetFeedIntData(stackable.IntData);
            if (values.Count < 3)
                return false;

            delta = Math.Max(0, values[2]);
            return delta > 0;
        }

        private static List<int> ParsePetFeedIntData(string intData)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(intData))
                return result;

            foreach (var token in intData.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    result.Add(value);
            }

            return result;
        }

        private static int ResolveActiveCreatureKey(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var equippedCreatureKey = ResolveEquippedCreatureKey(connection, transaction, characterId);
            if (equippedCreatureKey > 0)
                return equippedCreatureKey;

            var candidates = new List<int>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT creature_buffer FROM character_subtype0_fields WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", characterId);

                var value = command.ExecuteScalar();
                if (value is byte[] buffer)
                {
                    AddCreatureBufferCandidate(candidates, buffer, 0, littleEndian: true);
                    AddCreatureBufferCandidate(candidates, buffer, 0, littleEndian: false);
                    AddCreatureBufferCandidate(candidates, buffer, 4, littleEndian: true);
                    AddCreatureBufferCandidate(candidates, buffer, 4, littleEndian: false);
                }
            }

            foreach (var candidate in candidates)
            {
                if (CreatureKeyExists(connection, transaction, characterId, candidate))
                    return candidate;
            }

            return 0;
        }

        private static int ResolveEquippedCreatureKey(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var candidates = new List<int>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT raw_entry
FROM character_equipped_entries
WHERE character_id = @characterId
  AND slot = 24
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);

                var value = command.ExecuteScalar();
                if (value is byte[] rawEntry)
                {
                    AddCreatureBufferCandidate(candidates, rawEntry, 5, littleEndian: true);
                    AddCreatureBufferCandidate(candidates, rawEntry, 5, littleEndian: false);
                }
            }

            foreach (var candidate in candidates)
            {
                if (CreatureKeyExists(connection, transaction, characterId, candidate))
                    return candidate;
            }

            return 0;
        }

        private static void AddCreatureBufferCandidate(List<int> candidates, byte[] buffer, int offset, bool littleEndian)
        {
            if (buffer == null || buffer.Length < offset + 4)
                return;

            int value;
            if (littleEndian)
            {
                value = buffer[offset]
                    | (buffer[offset + 1] << 8)
                    | (buffer[offset + 2] << 16)
                    | (buffer[offset + 3] << 24);
            }
            else
            {
                value = (buffer[offset] << 24)
                    | (buffer[offset + 1] << 16)
                    | (buffer[offset + 2] << 8)
                    | buffer[offset + 3];
            }

            if (value > 0 && value < 1000000 && !candidates.Contains(value))
                candidates.Add(value);
        }

        private static bool CreatureKeyExists(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int creatureKey)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT 1
FROM character_creatures
WHERE character_id = @characterId
  AND creature_key = @creatureKey
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@creatureKey", creatureKey);
                return command.ExecuteScalar() != null;
            }
        }

        private static bool TryIncreaseCreatureSatiety(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int creatureKey,
            int delta,
            out PetSatietyMutation mutation)
        {
            mutation = default(PetSatietyMutation);
            var before = 0;
            var visibleBefore = 0;
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = @"
SELECT field04, field_after_value
FROM character_creatures
WHERE character_id = @characterId
  AND creature_key = @creatureKey
LIMIT 1;";
                select.Parameters.AddWithValue("@characterId", characterId);
                select.Parameters.AddWithValue("@creatureKey", creatureKey);

                using (var reader = select.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    before = Math.Max(0, Math.Min(100, reader.GetInt32(0)));
                    visibleBefore = Math.Max(0, Math.Min(100, reader.GetInt32(1)));
                }
            }

            if (before >= 100 && visibleBefore == before)
                return false;

            var after = before >= 100 ? before : Math.Min(100, before + delta);
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = @"
UPDATE character_creatures
SET field04 = @after,
    field_after_value = @after
WHERE character_id = @characterId
  AND creature_key = @creatureKey;";
                update.Parameters.AddWithValue("@after", after);
                update.Parameters.AddWithValue("@before", before);
                update.Parameters.AddWithValue("@characterId", characterId);
                update.Parameters.AddWithValue("@creatureKey", creatureKey);
                update.ExecuteNonQuery();
            }

            mutation = new PetSatietyMutation
            {
                CreatureKey = creatureKey,
                Before = before,
                After = after,
                Changed = after != before || visibleBefore != after,
            };

            return mutation.Changed;
        }

        private struct PetSatietyMutation
        {
            public int CreatureKey;

            public int Before;

            public int After;

            public bool Changed;
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
