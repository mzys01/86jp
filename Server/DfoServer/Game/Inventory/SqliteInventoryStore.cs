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
    }

    public sealed class SqliteInventoryStore
    {
        private const int DefaultAvatarUnknownFixed30 = 0x00001E00;
        private const ushort DefaultAvatarUnknownFixed4 = 0x0400;
        private const short ReviveCoinSlotIndex = 1;

        private int _activeCharacterId = 1000;
        private int _activeAccountId = 1;
        private int DefaultCharacterId => _activeCharacterId;
        private int DefaultAccountId => _activeAccountId;
        private readonly object _activeLock = new object();

        private readonly string _connectionString;

        public SqliteInventoryStore(string databasePath, string schemaFilePath)
        {
            if (databasePath == null) throw new ArgumentNullException(nameof(databasePath));
            if (schemaFilePath == null) throw new ArgumentNullException(nameof(schemaFilePath));

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        /// <summary>
        /// </summary>
        public IDisposable BeginScope(int characterId, int accountId)
        {
            Monitor.Enter(_activeLock);
            _activeCharacterId = characterId;
            _activeAccountId = accountId;
            return new ScopeReleaser(this);
        }

        private void EndScope()
        {
            Monitor.Exit(_activeLock);
        }

        public int CountItem(int itemTemplateId)
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
                    "SELECT COALESCE(SUM(stack_count), 0) FROM character_items WHERE character_id = @cid AND list_type = 0 AND item_template_id = @tid",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", DefaultCharacterId);
                    cmd.Parameters.AddWithValue("@tid", itemTemplateId);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        private sealed class ScopeReleaser : IDisposable
        {
            private readonly SqliteInventoryStore _store;
            private bool _disposed;
            public ScopeReleaser(SqliteInventoryStore store) { _store = store; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _store.EndScope();
            }
        }

        public void RunMigrations()
        {
            using (var connection = OpenConnection())
                RunMigrationsInternal(connection);
        }

        private void RunMigrationsInternal(SqliteConnection connection)
        {
            DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(connection, "characters", new[]
            {
                ("direction", "INTEGER NOT NULL DEFAULT 5"),
                ("area_state", "INTEGER NOT NULL DEFAULT 3"),
                ("appearance_blob", "BLOB"),
                ("delete_flag", "INTEGER NOT NULL DEFAULT 0"),
            });
            DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(connection, "account_cargo_state", new[]
            {
                ("item_count", "INTEGER NOT NULL DEFAULT 0"),
            });
            // 点券/代币券/欢乐代币券账号化: 旧库补列(账号级钱包)
            DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(connection, "accounts", new[]
            {
                ("cera", "INTEGER NOT NULL DEFAULT 0"),
                ("token_cera", "INTEGER NOT NULL DEFAULT 0"),
                ("happy_token_cera", "INTEGER NOT NULL DEFAULT 0"),
            });
            DfoServer.Sqlite.SqliteSchemaMigrator.MigrateCharacterItemsUniqueConstraint(connection);
            CurrencyService.MigrateCeraFromPacketTemplates(connection);
            MigrateAccountCargoFromPacketTemplates(connection);
            MigrateSubtype1BlobIfNeeded(connection);
            DfoServer.Game.CharacterData.SqliteSubtype0FieldsRepository.MigrateFromBlobIfNeeded(connection);
        }

        private void MigrateSubtype1BlobIfNeeded(SqliteConnection connection)
        {
            try
            {
                bool hasNewShape;
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(character_subtype1_fields);";
                    hasNewShape = false;
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            if (string.Equals(r.GetString(1), "name_tag_item_id", StringComparison.OrdinalIgnoreCase))
                                hasNewShape = true;
                }
                if (!hasNewShape)
                {
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = @"
ALTER TABLE character_subtype1_fields ADD COLUMN name_tag_item_id INTEGER NOT NULL DEFAULT 0;
ALTER TABLE character_subtype1_fields ADD COLUMN name_tag_expire_time INTEGER NOT NULL DEFAULT 0;
DELETE FROM character_subtype1_fields;";
                        cmd.ExecuteNonQuery();
                    }
                    FileLogger.Log("[MigrateSubtype1] 检测到旧列形(无 name_tag_item_id): 已加列并清空, 从 equip_list_blob 重迁移");
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM character_subtype1_fields;";
                    if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return;
                }
                var cids = new System.Collections.Generic.List<int>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT character_id FROM equipped_items WHERE equip_list_blob IS NOT NULL;";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read()) cids.Add(r.GetInt32(0));
                    }
                }
                foreach (var cid in cids)
                    DfoServer.Game.CharacterData.Subtype1BlobMigrator.Migrate(connection, cid);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[MigrateSubtype1] ERROR: {ex}");
            }
        }

        public void EnsureDatabase(CharacterItemListSnapshot seedSnapshot)
        {
            using (var connection = OpenConnection())
            {
                RunMigrationsInternal(connection);

                if (HasSeedData(connection))
                    return;

                SeedInitialSnapshot(connection, seedSnapshot);
            }
        }

        public void EnsureContainerState(int characterId)
        {
            using (var connection = OpenConnection())
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
                        UpsertContainerState(connection, tx, InventoryListType.Main, 24);
                        UpsertContainerState(connection, tx, InventoryListType.Avatar, 0);
                        UpsertContainerState(connection, tx, InventoryListType.PersonalCargo, 0);
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
            using (var connection = OpenConnection())
            {
                var snapshot = new CharacterItemListSnapshot();
                var listParams = LoadContainerState(connection);
                snapshot.MainListParam16 = GetListParam(listParams, InventoryListType.Main);
                snapshot.AvatarListParam16 = GetListParam(listParams, InventoryListType.Avatar);
                snapshot.PersonalCargoListParam16 = GetListParam(listParams, InventoryListType.PersonalCargo);
                snapshot.AccountCargoState = LoadAccountCargoState(connection);

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json
FROM character_items
WHERE character_id = @characterId
ORDER BY list_type, slot_index;";
                    command.Parameters.AddWithValue("@characterId", DefaultCharacterId);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var listType = (InventoryListType)reader.GetInt32(0);
                            var extraJson = reader.IsDBNull(12) ? "{}" : reader.GetString(12);

                            switch (listType)
                            {
                                case InventoryListType.Main:
                                    snapshot.MainItems.Add(ReadCommonItem(reader, extraJson));
                                    break;
                                case InventoryListType.Avatar:
                                    var avKind = reader.IsDBNull(3) ? "" : reader.GetString(3);
                                    snapshot.AvatarItems.Add(avKind == "avatar"
                                        ? ReadAvatarItem(reader, extraJson)
                                        : ReadEquipmentAsAvatarItem(reader, extraJson));
                                    break;
                                case InventoryListType.PersonalCargo:
                                    snapshot.PersonalCargoItems.Add(ReadCommonItem(reader, extraJson));
                                    break;
                                case InventoryListType.Pet:
                                    snapshot.PetItems.Add(ReadPetItem(reader, extraJson));
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
                    acCmd.Parameters.AddWithValue("@accountId", DefaultAccountId);
                    acCmd.Parameters.AddWithValue("@listType", (int)InventoryListType.AccountCargo);
                    using (var reader = acCmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.AccountCargoItems.Add(ReadCommonItem(reader, reader.IsDBNull(12) ? "{}" : reader.GetString(12)));
                    }
                }
                return snapshot;
            }
        }

        public bool TryDeleteItem(InventoryListType listType, short slotIndex, short deleteCount, out InventoryMutationResult result)
        {
            result = null;
            if (!IsSupportedDeleteOrSellListType(listType))
                return false;

            var dbListType = MapToDbListType(listType);

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var item = LoadItemRecord(connection, transaction, dbListType, slotIndex);
                if (item == null)
                    return false;

                var appliedCount = NormalizeRemovalCount(item, deleteCount);
                if (item.ItemKind == "stackable" && appliedCount < item.StackCount)
                {
                    UpdateStackCount(connection, transaction, item.ItemUid, item.StackCount - appliedCount);
                }
                else
                {
                    DeleteItem(connection, transaction, item.ItemUid);
                }

                WriteDeleteAuditLog(connection, transaction, item, appliedCount);
                var wallet = LoadWallet(connection, transaction);
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
            result = null;
            if (request == null || request.Choices.Count == 0)
                return false;

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var packageItem = LoadItemRecord(connection, transaction, InventoryListType.Main, request.SlotIndex);
                if (packageItem == null || packageItem.StackCount <= 0)
                {
                    FileLogger.Log($"  [AvatarPackage] REJECT: no usable package at slot={request.SlotIndex}");
                    return false;
                }

                if (!AvatarPackageDefinitionResolver.TryResolve(packageItem.ItemTemplateId, out var definition))
                {
                    FileLogger.Log($"  [AvatarPackage] REJECT: item=0x{packageItem.ItemTemplateId:X8} is not a supported avatar package");
                    return false;
                }

                if (!ValidateAvatarPackageChoices(definition, request, out var optionByItemId))
                {
                    FileLogger.Log($"  [AvatarPackage] REJECT: choices do not match package item=0x{packageItem.ItemTemplateId:X8}");
                    return false;
                }

                var addedAvatarCount = 0;
                var addedMainItemCount = 0;
                var addedPetCount = 0;
                var grantedItems = new List<PackageGrantedItem>();

                foreach (var reward in definition.Rewards)
                {
                    if (reward.IsAvatar)
                    {
                        var optionValue = optionByItemId[reward.ItemTemplateId];
                        for (var i = 0; i < reward.Count; i++)
                        {
                            var targetSlot = FindEmptySlot(connection, transaction, InventoryListType.Avatar, 0, 500);
                            if (targetSlot < 0)
                            {
                                FileLogger.Log($"  [AvatarPackage] REJECT: no avatar slot for item=0x{reward.ItemTemplateId:X8}");
                                return false;
                            }

                            InsertAvatarItem(
                                connection,
                                transaction,
                                CreateDefaultAvatarItem((short)targetSlot, reward.ItemTemplateId, optionValue));
                            grantedItems.Add(new PackageGrantedItem
                            {
                                ListType = InventoryListType.Avatar,
                                SlotIndex = (short)targetSlot,
                                ItemTemplateId = reward.ItemTemplateId,
                                DisplayCount = 1,
                                Durability = 0,
                            });
                            addedAvatarCount++;
                        }

                        continue;
                    }

                    if (!TryInsertPackageReward(connection, transaction, reward, ref addedMainItemCount, ref addedPetCount, grantedItems))
                    {
                        FileLogger.Log($"  [AvatarPackage] REJECT: cannot insert package reward item=0x{reward.ItemTemplateId:X8} count={reward.Count}");
                        return false;
                    }
                }

                if (packageItem.StackCount > 1)
                    UpdateStackCount(connection, transaction, packageItem.ItemUid, packageItem.StackCount - 1);
                else
                    DeleteItem(connection, transaction, packageItem.ItemUid);

                WriteOpenPackageAuditLog(connection, transaction, packageItem, addedAvatarCount, addedMainItemCount, addedPetCount);
                transaction.Commit();

                result = new AvatarPackageOpenResult
                {
                    SlotIndex = request.SlotIndex,
                    PackageItemTemplateId = packageItem.ItemTemplateId,
                    AddedAvatarCount = addedAvatarCount,
                    AddedMainItemCount = addedMainItemCount,
                    AddedPetCount = addedPetCount,
                };
                result.GrantedItems.AddRange(grantedItems);
                return true;
            }
        }

        private static bool ValidateAvatarPackageChoices(
            AvatarPackageDefinition definition,
            AvatarPackageOpenRequest request,
            out Dictionary<int, byte> optionByItemId)
        {
            optionByItemId = new Dictionary<int, byte>();
            if (definition == null || request == null)
                return false;

            if (request.Choices.Count != definition.AvatarItemIds.Count)
                return false;

            foreach (var choice in request.Choices)
            {
                if (!definition.AvatarItemIds.Contains(choice.ItemTemplateId))
                    return false;
                if (optionByItemId.ContainsKey(choice.ItemTemplateId))
                    return false;

                optionByItemId[choice.ItemTemplateId] = choice.OptionValue;
            }

            return true;
        }

        private bool TryInsertPackageReward(
            SqliteConnection connection,
            SqliteTransaction transaction,
            PackageRewardEntry reward,
            ref int addedMainItemCount,
            ref int addedPetCount,
            List<PackageGrantedItem> grantedItems = null)
        {
            if (reward.ExpireTime > 0 && reward.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
            {
                FileLogger.Log($"  [AvatarPackage] SKIP expired reward item=0x{reward.ItemTemplateId:X8} count={reward.Count} expire={reward.ExpireTime}");
                return true;
            }

            var metadata = ItemMetadataResolver.Resolve(reward.ItemTemplateId);
            if (metadata.ItemKind == "special")
            {
                FileLogger.Log($"  [AvatarPackage] SKIP unsupported special reward item=0x{reward.ItemTemplateId:X8} count={reward.Count}");
                return true;
            }

            if (metadata.IsStackable)
            {
                var remaining = reward.Count;
                var existingItem = reward.ExpireTime > 0
                    ? null
                    : FindItemByTemplateId(connection, transaction, InventoryListType.Main, reward.ItemTemplateId);
                if (existingItem != null)
                {
                    var capacity = metadata.StackLimit > 0 ? Math.Max(0, metadata.StackLimit - existingItem.StackCount) : remaining;
                    var addCount = Math.Min(remaining, capacity);
                    if (addCount > 0)
                    {
                        UpdateStackCount(connection, transaction, existingItem.ItemUid, existingItem.StackCount + addCount);
                        grantedItems?.Add(new PackageGrantedItem
                        {
                            ListType = InventoryListType.Main,
                            SlotIndex = existingItem.SlotIndex,
                            ItemTemplateId = reward.ItemTemplateId,
                            DisplayCount = addCount,
                            Durability = 0,
                        });
                        remaining -= addCount;
                    }
                }

                while (remaining > 0)
                {
                    var insertCount = metadata.StackLimit > 0 ? Math.Min(metadata.StackLimit, remaining) : remaining;
                    int slotStart, slotEnd;
                    metadata.GetSlotRange(out slotStart, out slotEnd);
                    var targetSlot = FindEmptySlot(connection, transaction, InventoryListType.Main, slotStart, slotEnd);
                    if (targetSlot < 0)
                        return false;

                    InsertCharacterItem(
                        connection,
                        transaction,
                        InventoryListType.Main,
                        (short)targetSlot,
                        reward.ItemTemplateId,
                        reward.ExpireTime > 0 ? "special" : metadata.ItemKind,
                        insertCount,
                        insertCount,
                        0,
                        0,
                        0,
                        reward.ExpireTime,
                        0,
                        0,
                        "{}");
                    grantedItems?.Add(new PackageGrantedItem
                    {
                        ListType = InventoryListType.Main,
                        SlotIndex = (short)targetSlot,
                        ItemTemplateId = reward.ItemTemplateId,
                        DisplayCount = insertCount,
                        Durability = 0,
                    });
                    remaining -= insertCount;
                }

                addedMainItemCount += reward.Count;
                return true;
            }

            var isCreature = string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal) &&
                             IsCreatureItem(reward.ItemTemplateId);
            for (var i = 0; i < reward.Count; i++)
            {
                if (isCreature)
                {
                    var petSlot = FindEmptySlot(connection, transaction, InventoryListType.Pet, PetInventorySlotStart, PetInventorySlotEnd);
                    if (petSlot < 0)
                        return false;

                    InsertCharacterItem(
                        connection,
                        transaction,
                        InventoryListType.Pet,
                        (short)petSlot,
                        reward.ItemTemplateId,
                        "pet",
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        NextPetSerialOrHandle(connection, transaction),
                        "{}");
                    grantedItems?.Add(new PackageGrantedItem
                    {
                        ListType = InventoryListType.Pet,
                        SlotIndex = (short)petSlot,
                        ItemTemplateId = reward.ItemTemplateId,
                        DisplayCount = 1,
                        Durability = 0,
                    });
                    addedPetCount++;
                    continue;
                }

                int slotStart, slotEnd;
                metadata.GetSlotRange(out slotStart, out slotEnd);
                var targetSlot = FindEmptySlot(connection, transaction, InventoryListType.Main, slotStart, slotEnd);
                if (targetSlot < 0)
                    return false;

                var instanceValue = GenerateInstanceValue(reward.ItemTemplateId, targetSlot);
                InsertCharacterItem(
                    connection,
                    transaction,
                    InventoryListType.Main,
                    (short)targetSlot,
                    reward.ItemTemplateId,
                    metadata.ItemKind,
                    instanceValue,
                    instanceValue,
                    metadata.Durability,
                    0,
                    0,
                    reward.ExpireTime,
                    -1,
                    0,
                    "{}");
                grantedItems?.Add(new PackageGrantedItem
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = (short)targetSlot,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = 1,
                    Durability = metadata.Durability,
                });
                addedMainItemCount++;
            }

            return true;
        }

        public bool TryOpenSelectablePackage(SelectablePackageOpenRequest request, out SelectablePackageOpenResult result)
        {
            result = null;
            if (request == null)
                return false;

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var packageItem = LoadItemRecord(connection, transaction, InventoryListType.Main, request.SlotIndex);
                if (packageItem == null || packageItem.StackCount <= 0)
                {
                    FileLogger.Log($"  [SelectablePackage] REJECT: no usable package at slot={request.SlotIndex}");
                    return false;
                }

                if (packageItem.ExpireTime > 0 && packageItem.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                {
                    FileLogger.Log($"  [SelectablePackage] REJECT: expired package item=0x{packageItem.ItemTemplateId:X8} expire={packageItem.ExpireTime}");
                    return false;
                }

                if (!SelectablePackageDefinitionResolver.TryResolve(packageItem.ItemTemplateId, out var definition))
                {
                    FileLogger.Log($"  [SelectablePackage] REJECT: item=0x{packageItem.ItemTemplateId:X8} has no selectable package data");
                    return false;
                }

                var addedMainItemCount = 0;
                var addedAvatarCount = 0;
                var addedPetCount = 0;
                var grantedItems = new List<PackageGrantedItem>();
                PackageRewardEntry rewardForResult = null;

                if (request.HasAvatarChoices)
                {
                    var seenAvatarChoices = new HashSet<int>();
                    if (!DefinitionHasAvatarReward(definition))
                    {
                        FileLogger.Log($"  [SelectablePackage] REJECT: package item=0x{packageItem.ItemTemplateId:X8} has no avatar rewards for long 0x00A0 body");
                        return false;
                    }

                    foreach (var choice in request.AvatarChoices)
                    {
                        if (!seenAvatarChoices.Add(choice.ItemTemplateId))
                        {
                            FileLogger.Log($"  [SelectablePackage] REJECT: duplicate avatar choice item=0x{choice.ItemTemplateId:X8}");
                            return false;
                        }

                        if (!SelectablePackageDefinitionResolver.IsAvatarEquipment(choice.ItemTemplateId))
                        {
                            FileLogger.Log($"  [SelectablePackage] REJECT: avatar choice item=0x{choice.ItemTemplateId:X8} is not valid for package item=0x{packageItem.ItemTemplateId:X8}");
                            return false;
                        }

                        if (!definition.TryGetReward(choice.ItemTemplateId, out var avatarReward))
                        {
                            avatarReward = new PackageRewardEntry
                            {
                                ItemTemplateId = choice.ItemTemplateId,
                                Count = 1,
                                ExpireTime = SelectablePackageDefinitionResolver.ResolveItemExpirationUnixTime(choice.ItemTemplateId),
                            };
                            FileLogger.Log($"  [SelectablePackage] WARN: avatar choice item=0x{choice.ItemTemplateId:X8} accepted by equipment metadata but missing from parsed package rewards item=0x{packageItem.ItemTemplateId:X8}");
                        }

                        if (rewardForResult == null)
                            rewardForResult = avatarReward;

                        if (avatarReward.ExpireTime > 0 && avatarReward.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                        {
                            FileLogger.Log($"  [SelectablePackage] REJECT: avatar choice item=0x{choice.ItemTemplateId:X8} expired at {avatarReward.ExpireTime}");
                            return false;
                        }

                        var targetSlot = FindEmptySlot(connection, transaction, InventoryListType.Avatar, 0, 500);
                        if (targetSlot < 0)
                        {
                            FileLogger.Log($"  [SelectablePackage] REJECT: no avatar slot for selected item=0x{choice.ItemTemplateId:X8}");
                            return false;
                        }

                        InsertAvatarItem(
                            connection,
                            transaction,
                            CreateDefaultAvatarItem((short)targetSlot, choice.ItemTemplateId, choice.OptionValue));
                        grantedItems.Add(new PackageGrantedItem
                        {
                            ListType = InventoryListType.Avatar,
                            SlotIndex = (short)targetSlot,
                            ItemTemplateId = choice.ItemTemplateId,
                            DisplayCount = 1,
                            Durability = 0,
                        });
                        addedAvatarCount++;
                    }
                }
                else
                {
                    if (!definition.TryGetReward(request.SelectedItemTemplateId, out var reward))
                    {
                        if (!DefinitionHasAvatarReward(definition) ||
                            !SelectablePackageDefinitionResolver.IsAvatarEquipment(request.SelectedItemTemplateId))
                        {
                            FileLogger.Log($"  [SelectablePackage] REJECT: selected item=0x{request.SelectedItemTemplateId:X8} is not in package item=0x{packageItem.ItemTemplateId:X8}");
                            return false;
                        }

                        reward = new PackageRewardEntry
                        {
                            ItemTemplateId = request.SelectedItemTemplateId,
                            Count = 1,
                            ExpireTime = SelectablePackageDefinitionResolver.ResolveItemExpirationUnixTime(request.SelectedItemTemplateId),
                        };
                        FileLogger.Log($"  [SelectablePackage] WARN: selected avatar item=0x{request.SelectedItemTemplateId:X8} accepted by equipment metadata but missing from parsed package rewards item=0x{packageItem.ItemTemplateId:X8}");
                    }

                    if (reward.ExpireTime > 0 && reward.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                    {
                        FileLogger.Log($"  [SelectablePackage] REJECT: selected reward item=0x{reward.ItemTemplateId:X8} expired at {reward.ExpireTime}");
                        return false;
                    }

                    var metadata = ItemMetadataResolver.Resolve(reward.ItemTemplateId);
                    if (metadata.ItemKind == "special")
                    {
                        FileLogger.Log($"  [SelectablePackage] REJECT: selected reward item=0x{reward.ItemTemplateId:X8} has unsupported metadata");
                        return false;
                    }

                    if (SelectablePackageDefinitionResolver.IsAvatarEquipment(reward.ItemTemplateId))
                    {
                        var targetSlot = FindEmptySlot(connection, transaction, InventoryListType.Avatar, 0, 500);
                        if (targetSlot < 0)
                        {
                            FileLogger.Log($"  [SelectablePackage] REJECT: no avatar slot for selected item=0x{reward.ItemTemplateId:X8}");
                            return false;
                        }

                        InsertAvatarItem(
                            connection,
                            transaction,
                            CreateDefaultAvatarItem((short)targetSlot, reward.ItemTemplateId, request.SelectionFlag));
                        grantedItems.Add(new PackageGrantedItem
                        {
                            ListType = InventoryListType.Avatar,
                            SlotIndex = (short)targetSlot,
                            ItemTemplateId = reward.ItemTemplateId,
                            DisplayCount = 1,
                            Durability = 0,
                        });
                        addedAvatarCount++;
                    }
                    else if (!TryInsertPackageReward(connection, transaction, reward, ref addedMainItemCount, ref addedPetCount, grantedItems))
                    {
                        FileLogger.Log($"  [SelectablePackage] REJECT: cannot insert selected reward item=0x{reward.ItemTemplateId:X8} count={reward.Count}");
                        return false;
                    }

                    rewardForResult = reward;
                }

                if (rewardForResult == null)
                    rewardForResult = new PackageRewardEntry { ItemTemplateId = request.SelectedItemTemplateId, Count = Math.Max(1, request.AvatarChoices.Count) };

                if (packageItem.StackCount > 1)
                    UpdateStackCount(connection, transaction, packageItem.ItemUid, packageItem.StackCount - 1);
                else
                    DeleteItem(connection, transaction, packageItem.ItemUid);

                WriteOpenSelectablePackageAuditLog(connection, transaction, packageItem, rewardForResult, addedMainItemCount, addedPetCount);
                transaction.Commit();

                result = new SelectablePackageOpenResult
                {
                    SlotIndex = request.SlotIndex,
                    PackageItemTemplateId = packageItem.ItemTemplateId,
                    RewardItemTemplateId = rewardForResult.ItemTemplateId,
                    AddedMainItemCount = addedMainItemCount,
                    AddedAvatarCount = addedAvatarCount,
                    AddedPetCount = addedPetCount,
                };
                result.GrantedItems.AddRange(grantedItems);
                return true;
            }
        }

        private static bool DefinitionHasAvatarReward(SelectablePackageDefinition definition)
        {
            if (definition == null || definition.Rewards == null)
                return false;

            foreach (var reward in definition.Rewards)
            {
                if (SelectablePackageDefinitionResolver.IsAvatarEquipment(reward.ItemTemplateId))
                    return true;
            }

            return false;
        }

        public bool TryBuyItem(int itemTemplateId, int buyCount, out InventoryMutationResult result)
        {
            result = null;
            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata.ItemKind == "special")
                return false;

            if (!CanMoveToListType(metadata.ItemKind, InventoryListType.Main))
                return false;

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                if (metadata.IsMaterialExchange)
                {
                    var wallet = LoadWallet(connection, transaction);
                    var totalGoldCost = metadata.BuyGold * buyCount;
                    var totalMaterialCost = metadata.NeedMaterialCount * buyCount;
                    if (wallet.Gold < totalGoldCost)
                    {
                        FileLogger.Log($"  [BuyItem] REJECT: need {totalGoldCost} gold, have {wallet.Gold}");
                        return false;
                    }

                    var materialItem = FindItemByTemplateId(connection, transaction, InventoryListType.Main, metadata.NeedMaterialId);
                    if (materialItem == null || materialItem.StackCount < totalMaterialCost)
                    {
                        FileLogger.Log($"  [BuyItem] REJECT: need {totalMaterialCost}x item {metadata.NeedMaterialId}, have {materialItem?.StackCount ?? 0}");
                        return false;
                    }

                    short matTargetSlot;
                    var targetItem = FindItemByTemplateId(connection, transaction, InventoryListType.Main, itemTemplateId);
                    if (targetItem == null)
                    {
                        int matSlotStart, matSlotEnd;
                        metadata.GetSlotRange(out matSlotStart, out matSlotEnd);
                        var emptySlot = FindEmptySlot(connection, transaction, InventoryListType.Main, matSlotStart, matSlotEnd);
                        if (emptySlot < 0)
                        {
                            FileLogger.Log($"  [BuyItem] REJECT: no empty slot for material exchange item {itemTemplateId}");
                            return false;
                        }
                        UpdateStackCount(connection, transaction, materialItem.ItemUid, materialItem.StackCount - totalMaterialCost);
                        InsertCharacterItem(connection, transaction, InventoryListType.Main, (short)emptySlot,
                            itemTemplateId, metadata.ItemKind, buyCount, buyCount,
                            metadata.Durability, 0, 0, 0, 0, 0, "{}");
                        matTargetSlot = (short)emptySlot;
                    }
                    else
                    {
                        UpdateStackCount(connection, transaction, materialItem.ItemUid, materialItem.StackCount - totalMaterialCost);
                        UpdateStackCount(connection, transaction, targetItem.ItemUid, targetItem.StackCount + buyCount);
                        matTargetSlot = targetItem.SlotIndex;
                    }
                    var newMaterialCount = materialItem.StackCount - totalMaterialCost;

                    if (totalGoldCost > 0)
                        UpdateWallet(connection, transaction, wallet.Gold - totalGoldCost, wallet.Coin);
                    var goldAfterBuy = wallet.Gold - totalGoldCost;
                    WriteBuyAuditLog(connection, transaction, itemTemplateId, matTargetSlot, totalGoldCost, 0);
                    transaction.Commit();

                    result = new InventoryMutationResult
                    {
                        ListType = InventoryListType.Main,
                        SlotIndex = matTargetSlot,
                        ItemTemplateId = itemTemplateId,
                        RemainingStackCount = buyCount,
                        InstanceValue = buyCount,
                        Durability = 0,
                        UpdatedGold = goldAfterBuy,
                        UpdatedSp = wallet.Sp,
                        UpdatedCoin = wallet.Coin,
                        RequestedCount = (short)buyCount,
                        AppliedCount = (short)buyCount,
                        CostItemTemplateId = metadata.NeedMaterialId,
                        CostItemNewStackCount = newMaterialCount,
                        CostItemSlotIndex = materialItem.SlotIndex,
                    };
                    return true;
                }

                var walletCheck = LoadWallet(connection, transaction);
                if (walletCheck.Gold < metadata.BuyGold || walletCheck.Coin < metadata.BuyCoin)
                    return false;

                // For stackable items, try to stack onto existing item first
                if (metadata.IsStackable)
                {
                    var existingItem = FindItemByTemplateId(connection, transaction, InventoryListType.Main, itemTemplateId);
                    if (existingItem != null)
                    {
                        var totalCostGold = metadata.BuyGold * buyCount;
                        var totalCostCoin = metadata.BuyCoin * buyCount;
                        if (walletCheck.Gold < totalCostGold || walletCheck.Coin < totalCostCoin)
                            return false;
                        UpdateStackCount(connection, transaction, existingItem.ItemUid, existingItem.StackCount + buyCount);
                        var updGold = walletCheck.Gold - totalCostGold;
                        var updCoin = walletCheck.Coin - totalCostCoin;
                        if (totalCostGold > 0 || totalCostCoin > 0)
                            UpdateWallet(connection, transaction, updGold, updCoin);
                        WriteBuyAuditLog(connection, transaction, itemTemplateId, existingItem.SlotIndex, totalCostGold, totalCostCoin);
                        transaction.Commit();

                        result = new InventoryMutationResult
                        {
                            ListType = InventoryListType.Main,
                            SlotIndex = existingItem.SlotIndex,
                            ItemTemplateId = itemTemplateId,
                            RemainingStackCount = buyCount,
                            InstanceValue = buyCount,
                            Durability = 0,
                            UpdatedGold = updGold,
                            UpdatedSp = walletCheck.Sp,
                            UpdatedCoin = updCoin,
                            RequestedCount = (short)buyCount,
                            AppliedCount = (short)buyCount,
                        };
                        return true;
                    }
                }

                var effectiveCount = metadata.IsStackable ? buyCount : 1;
                var totalBuyGold = metadata.BuyGold * effectiveCount;
                var totalBuyCoin = metadata.BuyCoin * effectiveCount;
                if (walletCheck.Gold < totalBuyGold || walletCheck.Coin < totalBuyCoin)
                    return false;

                int slotStart, slotEnd;
                metadata.GetSlotRange(out slotStart, out slotEnd);
                var targetSlot = FindEmptySlot(connection, transaction, InventoryListType.Main, slotStart, slotEnd);
                if (targetSlot < 0)
                    return false;

                var qualitySeed = GenerateInstanceValue(itemTemplateId, targetSlot);
                var buyStackCount = metadata.IsStackable ? effectiveCount : qualitySeed;
                var buyInstanceValue = metadata.IsStackable ? effectiveCount : qualitySeed;
                InsertCharacterItem(
                    connection,
                    transaction,
                    InventoryListType.Main,
                    (short)targetSlot,
                    itemTemplateId,
                    metadata.ItemKind,
                    buyStackCount,
                    buyInstanceValue,
                    metadata.Durability,
                    0,
                    0,
                    0,
                    metadata.IsStackable ? 0 : -1,
                    0,
                    "{}");

                var updatedGold = walletCheck.Gold - totalBuyGold;
                var updatedCoin = walletCheck.Coin - totalBuyCoin;
                UpdateWallet(connection, transaction, updatedGold, updatedCoin);
                WriteBuyAuditLog(connection, transaction, itemTemplateId, (short)targetSlot, totalBuyGold, totalBuyCoin);
                transaction.Commit();

                result = new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = (short)targetSlot,
                    ItemTemplateId = itemTemplateId,
                    RemainingStackCount = effectiveCount,
                    InstanceValue = buyInstanceValue,
                    Durability = metadata.Durability,
                    UpdatedGold = updatedGold,
                    UpdatedSp = walletCheck.Sp,
                    UpdatedCoin = updatedCoin,
                    RequestedCount = (short)effectiveCount,
                    AppliedCount = (short)effectiveCount,
                };
                return true;
            }
        }

        private const int QuickSlotStart = 3;
        private const int QuickSlotEnd = 8;

        // 宠物栏(list 7)"宠物"本体分页槽段(category 5): slot 0..139 共 140 格(实测计数)。
        // 其后 宠物装备=140..188(cat6)、宠物耗品=189..237(cat7)。新购宠物从本页首格开始填。
        private const int PetInventorySlotStart = 0;
        private const int PetInventorySlotEnd = 139;

        public bool TryPickupItem(int itemTemplateId, int stackCount, out short assignedSlot)
        {
            assignedSlot = -1;
            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata.ItemKind == "special")
                return false;

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                bool isConsumable = metadata.IsStackable
                    && metadata.StackableType != null
                    && metadata.StackableType.IndexOf("[waste]", System.StringComparison.OrdinalIgnoreCase) >= 0;

                if (metadata.IsStackable)
                {
                    if (isConsumable)
                    {
                        var existingQuick = FindItemByTemplateIdInRange(connection, transaction, InventoryListType.Main, itemTemplateId, QuickSlotStart, QuickSlotEnd);
                        if (existingQuick != null && (metadata.StackLimit <= 0 || existingQuick.StackCount + stackCount <= metadata.StackLimit))
                        {
                            UpdateStackCount(connection, transaction, existingQuick.ItemUid, existingQuick.StackCount + stackCount);
                            transaction.Commit();
                            assignedSlot = existingQuick.SlotIndex;
                            return true;
                        }
                    }

                    var existing = FindItemByTemplateId(connection, transaction, InventoryListType.Main, itemTemplateId);
                    if (existing != null && (metadata.StackLimit <= 0 || existing.StackCount + stackCount <= metadata.StackLimit))
                    {
                        UpdateStackCount(connection, transaction, existing.ItemUid, existing.StackCount + stackCount);
                        transaction.Commit();
                        assignedSlot = existing.SlotIndex;
                        return true;
                    }
                }

                int slotStart, slotEnd;
                metadata.GetSlotRange(out slotStart, out slotEnd);

                if (isConsumable)
                {
                    var quickSlot = FindEmptySlot(connection, transaction, InventoryListType.Main, QuickSlotStart, QuickSlotEnd);
                    if (quickSlot >= 0)
                    {
                        InsertCharacterItem(
                            connection, transaction, InventoryListType.Main, (short)quickSlot,
                            itemTemplateId, metadata.ItemKind, stackCount, stackCount,
                            metadata.Durability, 0, 0, 0, 0, 0, "{}");
                        transaction.Commit();
                        assignedSlot = (short)quickSlot;
                        return true;
                    }
                }

                var targetSlot = FindEmptySlot(connection, transaction, InventoryListType.Main, slotStart, slotEnd);
                if (targetSlot < 0)
                    return false;

                var qualitySeed = GenerateInstanceValue(itemTemplateId, targetSlot);
                var dbStackCount = metadata.IsStackable ? stackCount : qualitySeed;
                var dbInstanceValue = metadata.IsStackable ? stackCount : qualitySeed;
                InsertCharacterItem(
                    connection, transaction, InventoryListType.Main, (short)targetSlot,
                    itemTemplateId, metadata.ItemKind, dbStackCount, dbInstanceValue,
                    metadata.Durability, 0, 0, 0, metadata.IsStackable ? 0 : -1, 0, "{}");
                transaction.Commit();
                assignedSlot = (short)targetSlot;
                return true;
            }
        }

        public bool TrySellItem(InventoryListType listType, short slotIndex, short sellCount, out InventoryMutationResult result)
        {
            result = null;
            if (!IsSupportedDeleteOrSellListType(listType))
            {
                FileLogger.Log($"  [SellItem] REJECT: unsupported listType={listType}");
                return false;
            }

            var dbListType = MapToDbListType(listType);
            FileLogger.Log($"  [SellItem] wireListType={listType} dbListType={dbListType} slot={slotIndex} count={sellCount}");

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var item = LoadItemRecord(connection, transaction, dbListType, slotIndex);
                if (item == null)
                {
                    FileLogger.Log($"  [SellItem] FAIL: no item at dbListType={dbListType} slot={slotIndex}");
                    return false;
                }

                var metadata = ItemMetadataResolver.Resolve(item.ItemTemplateId);
                var appliedCount = NormalizeRemovalCount(item, sellCount);
                if (item.ItemKind == "stackable" && appliedCount < item.StackCount)
                {
                    UpdateStackCount(connection, transaction, item.ItemUid, item.StackCount - appliedCount);
                }
                else
                {
                    DeleteItem(connection, transaction, item.ItemUid);
                }

                var wallet = LoadWallet(connection, transaction);
                var goldDelta = metadata.SellGold * appliedCount;
                var updatedGold = wallet.Gold + goldDelta;
                UpdateWallet(connection, transaction, updatedGold, wallet.Coin);
                WriteSellAuditLog(connection, transaction, item, appliedCount, goldDelta);
                transaction.Commit();

                result = new InventoryMutationResult
                {
                    ListType = listType,
                    SlotIndex = slotIndex,
                    ItemTemplateId = item.ItemTemplateId,
                    RemainingStackCount = Math.Max(0, item.StackCount - appliedCount),
                    InstanceValue = item.InstanceValue,
                    Durability = item.Durability,
                    UpdatedGold = updatedGold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = wallet.Coin,
                    RequestedCount = sellCount,
                    AppliedCount = (short)appliedCount,
                };
                return true;
            }
        }

        public bool TryEnchantByBead(EnchantByBeadCommand command, out EnchantByBeadResult result)
        {
            if (command == null)
            {
                result = EnchantByBeadResult.Error(null, EnchantByBeadResult.ErrorInvalidBead);
                return false;
            }

            result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidBead);

            // 从主背包取宝珠和目标装备；先只放开已确认的空间。
            if (command.BeadListType != InventoryListType.Main || command.TargetListType != InventoryListType.Main)
            {
                FileLogger.Log($"  [EnchantByBead] REJECT: unsupported space bead={command.BeadListType} target={command.TargetListType}");
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorUnsupported);
                return false;
            }

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var bead = LoadItemRecord(connection, transaction, InventoryListType.Main, command.BeadSlotIndex);
                if (bead == null || bead.StackCount <= 0)
                {
                    FileLogger.Log($"  [EnchantByBead] REJECT: invalid bead slot={command.BeadSlotIndex} itemKind={bead?.ItemKind ?? "null"}");
                    result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidBead);
                    return false;
                }

                var target = LoadItemRecord(connection, transaction, InventoryListType.Main, command.TargetSlotIndex);
                if (target == null || target.ItemKind != "equipment")
                {
                    FileLogger.Log($"  [EnchantByBead] REJECT: invalid target slot={command.TargetSlotIndex} itemKind={target?.ItemKind ?? "null"}");
                    result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);
                    return false;
                }

                var enchantUpgradeCount = ReadEnchantUpgradeCount(bead.ExtraJson);
                if (!ItemMetadataResolver.TryValidateEnchantByBeadTarget(bead.ItemTemplateId, target.ItemTemplateId, enchantUpgradeCount, out var enchantCardItemId, out var rejectReason))
                {
                    var errorCode = rejectReason != null && rejectReason.StartsWith("target", StringComparison.Ordinal)
                        ? EnchantByBeadResult.ErrorInvalidTarget
                        : EnchantByBeadResult.ErrorUnsupported;
                    FileLogger.Log($"  [EnchantByBead] REJECT: bead=0x{bead.ItemTemplateId:X8} target=0x{target.ItemTemplateId:X8} upgrade={enchantUpgradeCount} reason={rejectReason}");
                    result = EnchantByBeadResult.Error(command, errorCode);
                    return false;
                }

                var targetCommon = LoadCommonItem(connection, transaction, InventoryListType.Main, command.TargetSlotIndex);
                if (targetCommon == null)
                {
                    result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);
                    return false;
                }

                targetCommon.PrefixData0E = NormalizeBytes(targetCommon.PrefixData0E, 8);

                // 装备 common entry 的 +0x0E 前 4 字节承载附魔卡片 index，后 1 字节承载 86 卡片升级次数。
                BitConverter.GetBytes(enchantCardItemId).CopyTo(targetCommon.PrefixData0E, 0);
                targetCommon.PrefixData0E[4] = enchantUpgradeCount;
                UpdateCommonExtraJson(connection, transaction, target.ItemUid, targetCommon);

                var remainingBeadCount = bead.StackCount - 1;
                CommonInventoryItem beadCommon;
                if (remainingBeadCount > 0)
                {
                    UpdateStackCount(connection, transaction, bead.ItemUid, remainingBeadCount);
                    beadCommon = LoadCommonItem(connection, transaction, InventoryListType.Main, command.BeadSlotIndex);
                    if (beadCommon == null)
                        beadCommon = CreateEmptyCommonItem(command.BeadSlotIndex);
                }
                else
                {
                    DeleteItem(connection, transaction, bead.ItemUid);
                    beadCommon = CreateEmptyCommonItem(command.BeadSlotIndex);
                }

                WriteDeleteAuditLog(connection, transaction, bead, 1);
                WriteEnchantAuditLog(connection, transaction, bead, target, enchantCardItemId, enchantUpgradeCount);
                transaction.Commit();

                FileLogger.Log($"  [EnchantByBead] OK: beadSlot={command.BeadSlotIndex} targetSlot={command.TargetSlotIndex} enchantCard=0x{enchantCardItemId:X8} upgrade={enchantUpgradeCount} beadLeft={Math.Max(0, remainingBeadCount)}");
                result = EnchantByBeadResult.Ok(command, targetCommon, beadCommon, enchantCardItemId);
                return true;
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

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var source = LoadItemRecord(connection, transaction, dbSrcList, request.SourceSlotIndex);
                var destination = LoadItemRecord(connection, transaction, dbDstList, request.DestinationSlotIndex);

                FileLogger.Log($"  [MoveItem] source={(source != null ? $"uid={source.ItemUid} kind={source.ItemKind} tmpl=0x{source.ItemTemplateId:X8}" : "null")}, destination={(destination != null ? $"uid={destination.ItemUid} kind={destination.ItemKind} tmpl=0x{destination.ItemTemplateId:X8}" : "null")}");

                if (request.DestinationListType == InventoryListType.Equipment)
                {
                    var outcome = HandleEquipSlotMove(connection, transaction, request, source, dbSrcList);
                    bool changed = outcome == EquipOutcome.Equipped || outcome == EquipOutcome.Unequipped;
                    if (changed)
                        transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount, mutated: changed);
                    result.AckError = outcome == EquipOutcome.ReverseError;
                    return true;
                }
                if (request.SourceListType == InventoryListType.Equipment)
                {
                    bool ok = HandleUnequipFromSlot(connection, transaction, request.SourceSlotIndex);
                    if (ok) transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount, mutated: ok);
                    return true;
                }

                if (source == null)
                {
                    if (destination != null)
                    {
                        FileLogger.Log($"  [MoveItem] MOVE(empty-src): dst uid={destination.ItemUid} tmpl=0x{destination.ItemTemplateId:X8} → ({dbSrcList},{request.SourceSlotIndex})");
                        UpdateItemPosition(connection, transaction, destination.ItemUid, dbSrcList, request.SourceSlotIndex);
                        WriteAuditLog(connection, transaction, "move_itemspace", destination, dbSrcList, request.SourceSlotIndex, request.MoveCount);
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
                    UpdateStackCount(connection, transaction, destination.ItemUid, destination.StackCount + moveCount);

                    if (moveCount == source.StackCount)
                        DeleteItem(connection, transaction, source.ItemUid);
                    else
                        UpdateStackCount(connection, transaction, source.ItemUid, source.StackCount - moveCount);

                    WriteAuditLog(connection, transaction, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
                    transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount);
                    return true;
                }

                if (source.ItemKind == "stackable" && moveCount > 0 && moveCount < source.StackCount && destination == null)
                {
                    UpdateStackCount(connection, transaction, source.ItemUid, source.StackCount - moveCount);
                    InsertSplitItem(connection, transaction, source, dbDstList, request.DestinationSlotIndex, moveCount);
                    WriteAuditLog(connection, transaction, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
                    transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount);
                    return true;
                }

                if (destination == null)
                {
                    FileLogger.Log($"  [MoveItem] MOVE: src uid={source.ItemUid} kind={source.ItemKind} tmpl=0x{source.ItemTemplateId:X8} → ({dbDstList},{request.DestinationSlotIndex})");
                    UpdateItemPosition(connection, transaction, source.ItemUid, dbDstList, request.DestinationSlotIndex);
                    WriteAuditLog(connection, transaction, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
                    transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount);
                    return true;
                }

                if (!CanSwap(source, destination))
                    return false;

                FileLogger.Log($"  [MoveItem] SWAP: src uid={source.ItemUid} kind={source.ItemKind} tmpl=0x{source.ItemTemplateId:X8} ↔ dst uid={destination.ItemUid} kind={destination.ItemKind} tmpl=0x{destination.ItemTemplateId:X8}");
                SwapItems(connection, transaction, source, destination);
                WriteAuditLog(connection, transaction, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
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

            using (var connection = OpenConnection())
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

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private void MigrateAccountCargoFromPacketTemplates(SqliteConnection connection)
        {
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM character_items WHERE owner_scope = 'account' AND list_type = 12;";
                if (Convert.ToInt32(check.ExecuteScalar()) > 0)
                    return;
            }
            byte[] body = null;
            int cid = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT pt.character_id, pt.body FROM packet_templates pt WHERE pt.noti_type = 13 AND pt.occurrence_index = (SELECT MAX(ps.occurrence_index) FROM packet_sequence ps WHERE ps.character_id = pt.character_id AND ps.noti_type = 13 AND ps.kind = 1);";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var b = r[1] as byte[];
                        if (b != null && b.Length > 0 && b[0] == (byte)InventoryListType.AccountCargo)
                        {
                            body = b;
                            cid = r.GetInt32(0);
                            break;
                        }
                    }
                }
            }
            if (body == null || body.Length < 9) return;
            int itemCount = BitConverter.ToUInt16(body, 3);
            if (itemCount == 0) return;

            int accountId = 1;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT account_id FROM characters WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", cid);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value) accountId = Convert.ToInt32(result);
            }

            using (var tx = connection.BeginTransaction())
            {
                int offset = 9;
                for (int i = 0; i < itemCount && offset + 84 <= body.Length; i++)
                {
                    var entry = CharacterItemListSnapshot.Slice(body, offset, 84);
                    var item = new CommonInventoryItem
                    {
                        SlotIndex = BitConverter.ToInt16(entry, 0),
                        ItemTemplateId = BitConverter.ToInt32(entry, 2),
                        CountOrInstanceValue = BitConverter.ToInt32(entry, 6),
                        ExtData0 = entry[10],
                        Durability = BitConverter.ToUInt16(entry, 11),
                        SealFlag = entry[13],
                        PrefixData0E = CharacterItemListSnapshot.Slice(entry, 14, 8),
                        Marker16 = BitConverter.ToInt32(entry, 22),
                        MiddleData1A = CharacterItemListSnapshot.Slice(entry, 26, 17),
                        ExpireTime = BitConverter.ToInt32(entry, 43),
                        TailData2F = CharacterItemListSnapshot.Slice(entry, 47, 37),
                    };
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'account', @ownerId, @characterId, @listType, @slotIndex, @templateId, @itemKind,
    @stackCount, @instanceValue, @durability, @sealFlag, 0, @expireTime, @marker16,
    0, @extraJson);";
                        cmd.Parameters.AddWithValue("@ownerId", accountId);
                        cmd.Parameters.AddWithValue("@characterId", cid);
                        cmd.Parameters.AddWithValue("@listType", (int)InventoryListType.AccountCargo);
                        cmd.Parameters.AddWithValue("@slotIndex", item.SlotIndex);
                        cmd.Parameters.AddWithValue("@templateId", item.ItemTemplateId);
                        cmd.Parameters.AddWithValue("@itemKind", InferCommonItemKind(item));
                        cmd.Parameters.AddWithValue("@stackCount", item.CountOrInstanceValue);
                        cmd.Parameters.AddWithValue("@instanceValue", item.CountOrInstanceValue);
                        cmd.Parameters.AddWithValue("@durability", item.Durability);
                        cmd.Parameters.AddWithValue("@sealFlag", item.SealFlag);
                        cmd.Parameters.AddWithValue("@expireTime", item.ExpireTime);
                        cmd.Parameters.AddWithValue("@marker16", item.Marker16);
                        cmd.Parameters.AddWithValue("@extraJson", SerializeCommon(item));
                        cmd.ExecuteNonQuery();
                    }
                    offset += 84;
                }
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE account_cargo_state SET item_count = @ic WHERE account_id = @aid;";
                    cmd.Parameters.AddWithValue("@ic", itemCount);
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        private bool HasSeedData(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(1) FROM character_items WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
        }

        private void SeedInitialSnapshot(SqliteConnection connection, CharacterItemListSnapshot snapshot)
        {
            using (var transaction = connection.BeginTransaction())
            {
                UpsertContainerState(connection, transaction, InventoryListType.Main, snapshot.MainListParam16);
                UpsertContainerState(connection, transaction, InventoryListType.Avatar, snapshot.AvatarListParam16);
                UpsertContainerState(connection, transaction, InventoryListType.PersonalCargo, snapshot.PersonalCargoListParam16);
                UpsertContainerState(connection, transaction, InventoryListType.Pet, 0);
                UpsertAccountCargoState(connection, transaction, snapshot.AccountCargoState);

                foreach (var item in snapshot.MainItems)
                    InsertCommonItem(connection, transaction, InventoryListType.Main, item);

                foreach (var item in snapshot.AvatarItems)
                    InsertAvatarItem(connection, transaction, item);

                foreach (var item in snapshot.PersonalCargoItems)
                    InsertCommonItem(connection, transaction, InventoryListType.PersonalCargo, item);

                foreach (var item in snapshot.PetItems)
                    InsertPetItem(connection, transaction, item);

                foreach (var item in snapshot.AccountCargoItems)
                    InsertAccountCargoItem(connection, transaction, item);

                transaction.Commit();
            }
        }

        public void SaveEquipListBlob(byte[] blob)
        {
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT OR REPLACE INTO equipped_items (character_id, equip_list_blob) VALUES (@cid, @blob)";
                command.Parameters.AddWithValue("@cid", DefaultCharacterId);
                command.Parameters.AddWithValue("@blob", blob);
                command.ExecuteNonQuery();
            }
        }

        private List<MakeEquipListCodec.Entry> LoadEquipEntriesTx(SqliteConnection connection, SqliteTransaction transaction)
        {
            var entries = new List<MakeEquipListCodec.Entry>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT slot, item_id, raw_entry FROM character_equipped_entries WHERE character_id = @cid ORDER BY slot";
                cmd.Parameters.AddWithValue("@cid", DefaultCharacterId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        entries.Add(new MakeEquipListCodec.Entry { Slot = r.GetInt32(0), ItemId = r.GetInt32(1), Raw = (byte[])r.GetValue(2) });
                }
            }
            return entries;
        }

        private void SaveEquipEntriesTx(SqliteConnection connection, SqliteTransaction transaction, List<MakeEquipListCodec.Entry> entries)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM character_equipped_entries WHERE character_id = @cid";
                cmd.Parameters.AddWithValue("@cid", DefaultCharacterId);
                cmd.ExecuteNonQuery();
            }
            foreach (var e in entries)
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "INSERT INTO character_equipped_entries(character_id, slot, item_id, raw_entry) VALUES(@cid, @s, @iid, @raw)";
                    cmd.Parameters.AddWithValue("@cid", DefaultCharacterId);
                    cmd.Parameters.AddWithValue("@s", e.Slot);
                    cmd.Parameters.AddWithValue("@iid", e.ItemId);
                    cmd.Parameters.AddWithValue("@raw", e.Raw);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private void InsertUnequippedEntry(SqliteConnection connection, SqliteTransaction transaction, int itemTemplateId, byte[] rawEntry)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT OR REPLACE INTO unequipped_entries (character_id, item_template_id, raw_entry) VALUES (@cid, @tid, @raw)";
                cmd.Parameters.AddWithValue("@cid", DefaultCharacterId);
                cmd.Parameters.AddWithValue("@tid", itemTemplateId);
                cmd.Parameters.AddWithValue("@raw", rawEntry);
                cmd.ExecuteNonQuery();
            }
        }

        private byte[] LoadUnequippedEntry(SqliteConnection connection, SqliteTransaction transaction, int itemTemplateId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT raw_entry FROM unequipped_entries WHERE character_id = @cid AND item_template_id = @tid";
                cmd.Parameters.AddWithValue("@cid", DefaultCharacterId);
                cmd.Parameters.AddWithValue("@tid", itemTemplateId);
                return cmd.ExecuteScalar() as byte[];
            }
        }

        private void DeleteUnequippedEntry(SqliteConnection connection, SqliteTransaction transaction, int itemTemplateId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM unequipped_entries WHERE character_id = @cid AND item_template_id = @tid";
                cmd.Parameters.AddWithValue("@cid", DefaultCharacterId);
                cmd.Parameters.AddWithValue("@tid", itemTemplateId);
                cmd.ExecuteNonQuery();
            }
        }

        private MakeEquipListCodec.DisplayFields? LoadDisplayFieldsFromCharacterItem(
            SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slotIndex)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"SELECT item_template_id, stack_count, durability, extra_json
                                    FROM character_items WHERE character_id=@cid AND list_type=@lt AND slot_index=@si";
                cmd.Parameters.AddWithValue("@cid", DefaultCharacterId);
                cmd.Parameters.AddWithValue("@lt", (int)listType);
                cmd.Parameters.AddWithValue("@si", (int)slotIndex);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    var extraJson = reader.IsDBNull(3) ? "{}" : reader.GetString(3);
                    var prefix = ReadHexValue(extraJson, "prefixData0E", 8);
                    var tail = ReadHexValue(extraJson, "tailData2F", 37);
                    var jewelHex = ReadRawStringValue(extraJson, "jewelSocket");
                    var f = new MakeEquipListCodec.DisplayFields
                    {
                        InstanceValue = unchecked((uint)reader.GetInt32(1)),
                        Durability = (ushort)reader.GetInt32(2),
                        Reinforce = (byte)ReadIntValue(extraJson, "extData0"),
                        Enchant = prefix.Length >= 4 ? BitConverter.ToUInt32(prefix, 0) : 0,
                        EnchantUpgradeCount = prefix.Length >= 5 ? prefix[4] : (byte)0,
                        AmplifyType = prefix.Length >= 6 ? prefix[5] : (byte)0,
                        AmplifyValue = prefix.Length >= 8 ? BitConverter.ToUInt16(prefix, 6) : (ushort)0,
                    };
                    // emblem from tail[0..8]
                    if (tail.Length > 0)
                    {
                        int ec = tail[0];
                        int embLen = 1 + ec * 4;
                        if (embLen <= tail.Length)
                        {
                            f.Emblem = new byte[embLen];
                            Buffer.BlockCopy(tail, 0, f.Emblem, 0, embLen);
                        }
                    }
                    // equipEffect from tail[9..10]
                    if (tail.Length >= 11)
                        f.Rune = BitConverter.ToUInt16(tail, 9);
                    // seal from tail[11..]
                    if (tail.Length > 11)
                    {
                        f.SealCount = tail[11];
                        f.SealTypes = new byte[3];
                        f.SealVal1s = new byte[3];
                        f.SealVal2s = new byte[3];
                        for (int i = 0; i < f.SealCount && i < 3; i++)
                        {
                            if (12 + i < tail.Length) f.SealTypes[i] = tail[12 + i];
                            if (15 + i < tail.Length) f.SealVal1s[i] = tail[15 + i];
                            if (18 + i < tail.Length) f.SealVal2s[i] = tail[18 + i];
                        }
                        if (f.SealCount > 0 && 21 < tail.Length)
                        {
                            int sealTailLen = 2; // genuineUpgrade + check
                            if (21 + 1 < tail.Length && tail[22] != 0xFF)
                                sealTailLen += 4;
                            f.SealTail = new byte[Math.Min(sealTailLen, tail.Length - 21)];
                            Buffer.BlockCopy(tail, 21, f.SealTail, 0, f.SealTail.Length);
                        }
                    }
                    // forging from tail[27]
                    if (tail.Length > 27)
                        f.Forging = tail[27];
                    // jewelSocket
                    if (!string.IsNullOrEmpty(jewelHex))
                    {
                        f.JewelSocket = new byte[jewelHex.Length / 2];
                        for (int i = 0; i < f.JewelSocket.Length; i++)
                            f.JewelSocket[i] = Convert.ToByte(jewelHex.Substring(i * 2, 2), 16);
                    }
                    return f;
                }
            }
        }

        /// <summary>
        ///
        ///
        /// </summary>
        private bool HandleUnequipFromSlot(SqliteConnection connection, SqliteTransaction transaction, int equipSlot)
        {
            var entries = LoadEquipEntriesTx(connection, transaction);
            var removed = entries.Find(e => e.Slot == equipSlot);
            if (removed == null)
            {
                FileLogger.Log($"  [EquipMove] UNEQUIP(src): slot {equipSlot} not in equip list (no-op)");
                return false;
            }
            entries.Remove(removed);
            SaveEquipEntriesTx(connection, transaction, entries);
            InsertUnequippedEntry(connection, transaction, removed.ItemId, removed.Raw);
            FileLogger.Log($"  [EquipMove] UNEQUIP(src): removed slot {equipSlot} itemId=0x{removed.ItemId:X8}, cached entry");
            return true;
        }

        private EquipOutcome HandleEquipSlotMove(SqliteConnection connection, SqliteTransaction transaction,
            InventoryMoveRequest request, ItemRecord mainSource, InventoryListType dbSrcList)
        {
            var entries = LoadEquipEntriesTx(connection, transaction);

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
                entries.Remove(removed);
                SaveEquipEntriesTx(connection, transaction, entries);
                InsertUnequippedEntry(connection, transaction, removed.ItemId, removed.Raw);
                InsertEquipToContainer(connection, transaction, dbSrcList, request.SourceSlotIndex, removed.ItemId, removed.Raw);
                FileLogger.Log($"  [EquipMove] UNEQUIP: removed equip slot {equipSlot} itemId=0x{removed.ItemId:X8}, cached + {dbSrcList} slot {request.SourceSlotIndex}");
                return EquipOutcome.Unequipped;
            }
            else
            {
                int wantId = request.SourceInstanceValue;
                var existing = entries.Find(e => e.Slot == equipSlot);
                if (equipSlot == 12 && existing != null && existing.ItemId == wantId)
                {
                    FileLogger.Log($"  [EquipMove] slot {equipSlot} 已是 0x{wantId:X8} (称号 P2 反转包) -> ReverseError");
                    return EquipOutcome.ReverseError;
                }

                byte[] entryRaw;
                var cachedRaw = LoadUnequippedEntry(connection, transaction, wantId);
                if (cachedRaw != null)
                {
                    entryRaw = MakeEquipListCodec.SetSlotByte(cachedRaw, equipSlot);
                    var fields = LoadDisplayFieldsFromCharacterItem(connection, transaction, dbSrcList, request.SourceSlotIndex);
                    if (fields != null)
                    {
                        // unequipped_entries 可能是附魔前的旧 raw；穿戴前用当前背包记录覆盖动态附魔/增幅字段。
                        ApplyEquipmentPrefixFields(entryRaw, fields.Value);
                    }
                }
                else if (equipSlot == 12)
                {
                    var template = FindEntryTemplate(connection, transaction, entries, equipSlot);
                    if (template == null)
                    {
                        FileLogger.Log($"  [EquipMove] EQUIP: slot {equipSlot} want 0x{wantId:X8} — 无缓存且无同槽模板 (no-op)");
                        return EquipOutcome.NoOp;
                    }
                    entryRaw = MakeEquipListCodec.BuildEntryFromTemplate(template, equipSlot, wantId);
                    FileLogger.Log($"  [EquipMove] EQUIP: 称号 slot12 0x{wantId:X8} 用模板构造 entry ({entryRaw.Length}B)");
                }
                else
                {
                    var fields = LoadDisplayFieldsFromCharacterItem(connection, transaction, dbSrcList, request.SourceSlotIndex);
                    if (fields == null)
                    {
                        FileLogger.Log($"  [EquipMove] EQUIP: slot {equipSlot} want 0x{wantId:X8} — 无缓存且无DB记录 (no-op)");
                        return EquipOutcome.NoOp;
                    }
                    entryRaw = MakeEquipListCodec.BuildEntryFromDisplayFields(equipSlot, wantId, fields.Value);
                    FileLogger.Log($"  [EquipMove] EQUIP: slot {equipSlot} 0x{wantId:X8} 从DB字段构造 entry ({entryRaw.Length}B)");
                }

                DeleteCharacterItemSlot(connection, transaction, dbSrcList, request.SourceSlotIndex);

                if (existing != null)
                {
                    entries.Remove(existing);
                    InsertUnequippedEntry(connection, transaction, existing.ItemId, existing.Raw);
                    InsertEquipToContainer(connection, transaction, dbSrcList, request.SourceSlotIndex, existing.ItemId, existing.Raw);
                    FileLogger.Log($"  [EquipMove] REPLACE: slot {equipSlot} old 0x{existing.ItemId:X8} -> {dbSrcList} slot {request.SourceSlotIndex}");
                }

                var newEntry = new MakeEquipListCodec.Entry { Slot = equipSlot, ItemId = wantId, Raw = entryRaw };
                int insertAt = entries.FindIndex(e => e.Slot > equipSlot);
                if (insertAt < 0) entries.Add(newEntry); else entries.Insert(insertAt, newEntry);
                SaveEquipEntriesTx(connection, transaction, entries);
                DeleteUnequippedEntry(connection, transaction, wantId);
                FileLogger.Log($"  [EquipMove] EQUIP: slot {equipSlot} itemId=0x{wantId:X8} ({(cachedRaw != null ? "cache" : "template")})");
                return EquipOutcome.Equipped;
            }
        }

        private static void ApplyEquipmentPrefixFields(byte[] raw, MakeEquipListCodec.DisplayFields fields)
        {
            if (raw == null || raw.Length < 24)
                return;

            BitConverter.GetBytes(fields.Enchant).CopyTo(raw, 16);
            raw[20] = fields.EnchantUpgradeCount;
            raw[21] = fields.AmplifyType;
            BitConverter.GetBytes(fields.AmplifyValue).CopyTo(raw, 22);
        }

        private void InsertEquipToContainer(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slot, int itemId, byte[] entryRaw)
        {
            if (listType == InventoryListType.Pet)
            {
                int serial = entryRaw != null && entryRaw.Length >= 28 ? BitConverter.ToInt32(entryRaw, 24) : 0;
                InsertCharacterItem(connection, transaction, InventoryListType.Pet, slot, itemId, "pet",
                    stackCount: 0, instanceValue: 0, durability: 0, sealFlag: 0, optionValue: 0,
                    expireTime: 0, marker16: 0, petSerialOrHandle: serial, extraJson: "{}");
                return;
            }
            //   durability(entry+10)     → 84B Durability(+11)
            //   enchant(entry+16,u32)    → 84B PrefixData0E[0..3] (enchantIndex +14)
            //   enchantUpgrade(entry+20) → 84B PrefixData0E[4] (卡片升级次数)
            //   amplifyType(entry+21)    → 84B PrefixData0E[5] (+19)
            //   amplifyValue(entry+22)   → 84B PrefixData0E[6..7] (+20)
            ushort dur = 0;
            int countOrIv = itemId;
            string extraJson = "{}";
            if (entryRaw != null && entryRaw.Length >= 24)
            {
                var f = MakeEquipListCodec.ParseDisplayFields(entryRaw);
                dur = f.Durability;
                countOrIv = unchecked((int)f.InstanceValue);
                var prefix = new byte[8];
                BitConverter.GetBytes(f.Enchant).CopyTo(prefix, 0);
                prefix[4] = f.EnchantUpgradeCount;
                prefix[5] = f.AmplifyType;
                BitConverter.GetBytes(f.AmplifyValue).CopyTo(prefix, 6);
                var tail = new byte[37];
                if (f.Emblem != null && f.Emblem.Length > 0)
                    Buffer.BlockCopy(f.Emblem, 0, tail, 0, Math.Min(f.Emblem.Length, 9));
                BitConverter.GetBytes(f.Rune).CopyTo(tail, 9); // 84B offset56 → TailData2F[9]
                tail[27] = f.Forging;                           // 84B genuineUpgrade offset74 → TailData2F[27]
                if (f.SealCount > 0 && f.SealTypes != null)
                {
                    tail[11] = f.SealCount;
                    for (int si = 0; si < f.SealCount && si < 3; si++)
                    {
                        tail[12 + si] = f.SealTypes[si];  // 84B offset 59,60,61
                        tail[15 + si] = f.SealVal1s[si];  // 84B offset 62,63,64
                        tail[18 + si] = f.SealVal2s[si];  // 84B offset 65,66,67
                    }
                    // seal tail → TailData2F[21+] (84B offset 68+)
                    if (f.SealTail != null)
                        for (int si = 0; si < f.SealTail.Length && 21 + si < tail.Length; si++)
                            tail[21 + si] = f.SealTail[si];
                }
                string jewelJson = (f.JewelSocket != null && f.JewelSocket.Length > 0)
                    ? ",\"jewelSocket\":\"" + BitConverter.ToString(f.JewelSocket).Replace("-", "") + "\""
                    : "";
                extraJson = "{\"extData0\":" + f.Reinforce
                    + ",\"prefixData0E\":\"" + BitConverter.ToString(prefix).Replace("-", "")
                    + "\",\"tailData2F\":\"" + BitConverter.ToString(tail).Replace("-", "") + "\""
                    + jewelJson + "}";
            }
            InsertCharacterItem(connection, transaction, listType, slot, itemId, "equipment",
                stackCount: countOrIv, instanceValue: 0, durability: dur, sealFlag: 0, optionValue: 0,
                expireTime: 0, marker16: -1, petSerialOrHandle: 0, extraJson: extraJson);
        }

        private byte[] FindEntryTemplate(SqliteConnection connection, SqliteTransaction transaction, List<MakeEquipListCodec.Entry> entries, int slot)
        {
            var existing = entries.Find(e => e.Slot == slot);
            if (existing != null) return existing.Raw;
            return LoadCachedEntryBySlot(connection, transaction, slot);
        }

        private byte[] LoadCachedEntryBySlot(SqliteConnection connection, SqliteTransaction transaction, int slot)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT raw_entry FROM unequipped_entries WHERE character_id = @cid";
                cmd.Parameters.AddWithValue("@cid", DefaultCharacterId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var raw = reader[0] as byte[];
                        if (raw != null && raw.Length > 0 && raw[0] == (byte)slot)
                            return raw;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// </summary>
        public void SeedNewCharacterEquipment((short slot, int itemId)[] equipment)
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var entries = LoadEquipEntriesTx(connection, transaction);
                foreach (var (slot, itemId) in equipment)
                {
                    var meta = ItemMetadataResolver.Resolve(itemId);
                    if (meta == null)
                        throw new System.IO.InvalidDataException(
                            $"[SeedEquip] 初始装备 itemId={itemId} 不在 PVF 装备表 — 创建数据错误, 不静默跳过");

                    var fields = new MakeEquipListCodec.DisplayFields
                    {
                        InstanceValue = 999999998u,
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
                SaveEquipEntriesTx(connection, transaction, entries);
                transaction.Commit();
            }
        }

        private  Dictionary<InventoryListType, ushort> LoadContainerState(SqliteConnection connection)
        {
            var states = new Dictionary<InventoryListType, ushort>();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT list_type, list_param16
FROM character_container_state
WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        states[(InventoryListType)reader.GetInt32(0)] = Convert.ToUInt16(reader.GetInt32(1), CultureInfo.InvariantCulture);
                }
            }

            return states;
        }

        private static ushort GetListParam(Dictionary<InventoryListType, ushort> states, InventoryListType listType)
        {
            return states.TryGetValue(listType, out var value) ? value : (ushort)0;
        }

        private  AccountCargoStateSnapshot LoadAccountCargoState(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT selection_key, value32, item_count
FROM account_cargo_state
WHERE account_id = @accountId;";
                command.Parameters.AddWithValue("@accountId", DefaultAccountId);

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

        private  ushort CountAccountCargoItems(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(1)
FROM character_items
WHERE owner_scope = 'account' AND owner_id = @accountId AND list_type = @listType;";
                command.Parameters.AddWithValue("@accountId", DefaultAccountId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.AccountCargo);
                return Convert.ToUInt16(Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
        }

        private  void UpsertContainerState(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, ushort listParam16)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, @listType, @listParam16);";
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@listParam16", listParam16);
                command.ExecuteNonQuery();
            }
        }

        private  void UpsertAccountCargoState(SqliteConnection connection, SqliteTransaction transaction, AccountCargoStateSnapshot state)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO account_cargo_state (account_id, selection_key, value32, item_count, updated_at)
VALUES (@accountId, @selectionKey, @value32, @itemCount, CURRENT_TIMESTAMP);";
                command.Parameters.AddWithValue("@accountId", DefaultAccountId);
                command.Parameters.AddWithValue("@selectionKey", state.SelectionKey);
                command.Parameters.AddWithValue("@value32", state.Value32);
                command.Parameters.AddWithValue("@itemCount", state.ItemCount);
                command.ExecuteNonQuery();
            }
        }

        private  void InsertCommonItem(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, CommonInventoryItem item)
        {
            InsertCharacterItem(connection, transaction, listType, item.SlotIndex, item.ItemTemplateId, InferCommonItemKind(item), item.CountOrInstanceValue, item.CountOrInstanceValue, item.Durability, item.SealFlag, 0, item.ExpireTime, item.Marker16, 0, SerializeCommon(item));
        }

        private  void InsertAvatarItem(SqliteConnection connection, SqliteTransaction transaction, AvatarInventoryItem item)
        {
            InsertCharacterItem(connection, transaction, InventoryListType.Avatar, item.SlotIndex, item.AvatarItemId, "avatar", 0, 0, 0, 0, item.OptionValue, 0, item.UnknownFixed30, 0, SerializeAvatar(item));
        }

        private static AvatarInventoryItem CreateDefaultAvatarItem(short slotIndex, int avatarItemId, byte optionValue)
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

        private  void InsertPetItem(SqliteConnection connection, SqliteTransaction transaction, PetInventoryItem item)
        {
            InsertCharacterItem(connection, transaction, InventoryListType.Pet, item.SlotIndex, item.CreatureItemId, "pet", 0, 0, 0, 0, 0, 0, 0, item.CreatureSerialOrHandle, SerializePet(item));
        }

        private  void InsertCharacterItem(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slotIndex, int templateId, string itemKind, int stackCount, int instanceValue, ushort durability, byte sealFlag, byte optionValue, int expireTime, int marker16, int petSerialOrHandle, string extraJson)
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
                command.Parameters.AddWithValue("@ownerId", DefaultCharacterId);
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
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

        private void InsertAccountCargoItem(SqliteConnection connection, SqliteTransaction transaction, CommonInventoryItem item)
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
                command.Parameters.AddWithValue("@ownerId", DefaultAccountId);
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.AccountCargo);
                command.Parameters.AddWithValue("@slotIndex", item.SlotIndex);
                command.Parameters.AddWithValue("@templateId", item.ItemTemplateId);
                command.Parameters.AddWithValue("@itemKind", InferCommonItemKind(item));
                command.Parameters.AddWithValue("@stackCount", item.CountOrInstanceValue);
                command.Parameters.AddWithValue("@instanceValue", item.CountOrInstanceValue);
                command.Parameters.AddWithValue("@durability", item.Durability);
                command.Parameters.AddWithValue("@sealFlag", item.SealFlag);
                command.Parameters.AddWithValue("@optionValue", 0);
                command.Parameters.AddWithValue("@expireTime", item.ExpireTime);
                command.Parameters.AddWithValue("@marker16", item.Marker16);
                command.Parameters.AddWithValue("@extraJson", SerializeCommon(item));
                command.ExecuteNonQuery();
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

        private static bool IsSupportedMoveListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Pet;
        }

        private static InventoryListType MapToDbListType(InventoryListType listType)
        {
            if (listType == InventoryListType.Equipment)
                return InventoryListType.Avatar;
            return listType;
        }

            private static bool IsSupportedDeleteOrSellListType(InventoryListType listType)
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

        private static bool CanMoveToListType(string itemKind, InventoryListType destinationListType)
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

        private static int NormalizeRemovalCount(ItemRecord source, short requestedCount)
        {
            if (source.ItemKind != "stackable")
                return 1;

            if (requestedCount <= 0 || requestedCount >= source.StackCount)
                return source.StackCount;

            return requestedCount;
        }

        private  void UpdateStackCount(SqliteConnection connection, SqliteTransaction transaction, long itemUid, int stackCount)
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

        private  void UpdateItemPosition(SqliteConnection connection, SqliteTransaction transaction, long itemUid, InventoryListType listType, short slotIndex)
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

        private  void DeleteItem(SqliteConnection connection, SqliteTransaction transaction, long itemUid)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM character_items WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@itemUid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        private void UpdateCommonExtraJson(SqliteConnection connection, SqliteTransaction transaction, long itemUid, CommonInventoryItem item)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_items
SET extra_json = @extraJson,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@extraJson", SerializeCommon(item));
                command.Parameters.AddWithValue("@itemUid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        private  void DeleteCharacterItemSlot(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM character_items WHERE character_id = @cid AND list_type = @listType AND slot_index = @slot;";
                command.Parameters.AddWithValue("@cid", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slot", slotIndex);
                command.ExecuteNonQuery();
            }
        }

        private  WalletState LoadWallet(SqliteConnection connection, SqliteTransaction transaction)
        {
            var snap = CurrencyService.LoadWallet(connection, transaction, DefaultCharacterId);
            var w = new WalletState { Gold = snap.Gold, Coin = snap.Cera, TokenCera = snap.TokenCera, HappyTokenCera = snap.HappyTokenCera };
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT stack_count FROM character_items WHERE character_id = @cid AND list_type = 0 AND slot_index = 2;";
                cmd.Parameters.AddWithValue("@cid", DefaultCharacterId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    w.Sp = Convert.ToInt32(result);
            }
            return w;
        }

        private  void UpdateWallet(SqliteConnection connection, SqliteTransaction transaction, int gold, int coin)
        {
            CurrencyService.UpdateGold(connection, transaction, DefaultCharacterId, gold);
            CurrencyService.UpdateCera(connection, transaction, DefaultCharacterId, coin);
        }

        // 点券支付模式: Default=欢乐券→代币券→点券瀑布; OnlyCera=仅点券; OnlyCeraPoint=仅欢乐券+代币券。
        private enum CeraPayMode
        {
            Default,
            OnlyCera,
            OnlyCeraPoint,
        }

        private struct CeraShopPaymentPlan
        {
            public bool Ok;
            public int NewGold;
            public int NewCera;
            public int NewTokenCera;
            public int NewHappyTokenCera;
        }

        // 计算扣费后的各币余额。金币与点券分别结算; 点券按 mode 在 {欢乐券, 代币券, 点券} 内瀑布扣减。
        // 若任一币种(在允许的币池内)余额不足, 返回 Ok=false 且不改动余额。
        private static CeraShopPaymentPlan ComputeCeraShopPayment(WalletState w, int goldCost, int ceraCost, CeraPayMode mode)
        {
            var plan = new CeraShopPaymentPlan
            {
                Ok = false,
                NewGold = w.Gold,
                NewCera = w.Coin,
                NewTokenCera = w.TokenCera,
                NewHappyTokenCera = w.HappyTokenCera,
            };

            if (goldCost > 0)
            {
                if (w.Gold < goldCost)
                    return plan;
                plan.NewGold = w.Gold - goldCost;
            }

            if (ceraCost > 0)
            {
                var useHappy = mode != CeraPayMode.OnlyCera;          // OnlyCera 不能用欢乐/代币券
                var useToken = mode != CeraPayMode.OnlyCera;
                var useCera = mode != CeraPayMode.OnlyCeraPoint;       // OnlyCeraPoint 不能用点券

                var remaining = ceraCost;
                int happy = plan.NewHappyTokenCera, token = plan.NewTokenCera, cera = plan.NewCera;
                if (useHappy && remaining > 0) { var t = Math.Min(remaining, happy); happy -= t; remaining -= t; }
                if (useToken && remaining > 0) { var t = Math.Min(remaining, token); token -= t; remaining -= t; }
                if (useCera && remaining > 0) { var t = Math.Min(remaining, cera); cera -= t; remaining -= t; }
                if (remaining > 0)
                    return plan; // 允许的币池内不够付

                plan.NewHappyTokenCera = happy;
                plan.NewTokenCera = token;
                plan.NewCera = cera;
            }

            plan.Ok = true;
            return plan;
        }

        // 落库: 把四种货币的扣费后余额写入。
        private void ApplyCeraShopPayment(SqliteConnection connection, SqliteTransaction transaction, CeraShopPaymentPlan plan)
        {
            CurrencyService.UpdateGold(connection, transaction, DefaultCharacterId, plan.NewGold);
            CurrencyService.UpdateCera(connection, transaction, DefaultCharacterId, plan.NewCera);
            CurrencyService.UpdateTokenCera(connection, transaction, DefaultCharacterId, plan.NewTokenCera);
            CurrencyService.UpdateHappyTokenCera(connection, transaction, DefaultCharacterId, plan.NewHappyTokenCera);
        }

        public bool TryBuyCeraShopItem(int productId, int buyCount, out InventoryMutationResult result)
        {
            result = null;
            FileLogger.Log($"  [CeraShopBuy] clientProductId=0x{productId:X8} ({productId}) buyCount={buyCount}");

            if (buyCount <= 0)
                buyCount = 1;
            if (buyCount > 999)
                buyCount = 999;

            if (!CeraShopProductCatalog.TryResolve(productId, out var product))
            {
                FileLogger.Log($"  [CeraShopBuy] REJECT: product 0x{productId:X8} not found in cerashop.etc");
                return false;
            }

            var itemTemplateId = product.ItemTemplateId;
            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata.ItemKind == "special")
            {
                FileLogger.Log($"  [CeraShopBuy] REJECT: product=0x{productId:X8} maps to unsupported item=0x{itemTemplateId:X8} section={product.Section}");
                return false;
            }

            var itemKind = metadata.ItemKind;
            var isStackable = metadata.IsStackable;
            // 限时时装(avatar): cerashop 第3字段(product.Count)= 时长档位(1-based),
            // 时长(天)与点券价取自时装 .equ 的 [avatar type select]。
            var isAvatar = string.Equals(product.Section, "avatar", StringComparison.OrdinalIgnoreCase);
            // 宠物(creature): 装备类且 .equ 的 [equipment type]=[creature], 应进专用宠物栏(Pet 列表 7),
            // 而不是主背包装备格。判定基于物品本身的 equipment type, 不依赖 cerashop 段名(段内还混有可堆叠饲料)。
            var isCreature = !isAvatar && string.Equals(itemKind, "equipment", StringComparison.Ordinal) && IsCreatureItem(itemTemplateId);
            var avatarDurationDays = 0;
            // 发货数量 = 份数 × 每份数量(cerashop count); 价格 = 每份价 × 份数 (avatar 恒为 1)
            var effectiveCount = (isStackable && !isAvatar) ? Math.Min(999, buyCount * Math.Max(1, product.Count)) : 1;
            // 价格来自 cerashop 三列: 金币 / 胜点(忽略) / 点券。金币与点券一般互斥(只一个非 0)。
            var goldPrice = Math.Max(0, product.GoldPrice);
            var ceraPrice = Math.Max(0, product.CoinPrice);
            if (isAvatar)
            {
                goldPrice = 0; // 时装走点券
                if (AvatarTypeSelectResolver.TryGetOption(itemTemplateId, Math.Max(1, product.Count), out var durDays, out var avatarPrice))
                {
                    avatarDurationDays = durDays;
                    if (avatarPrice > 0)
                        ceraPrice = avatarPrice;
                    FileLogger.Log($"  [CeraShopBuy] avatar item=0x{itemTemplateId:X8} durIndex={product.Count} -> durationDays={durDays} ceraPrice={avatarPrice}");
                }
                else
                {
                    FileLogger.Log($"  [CeraShopBuy] WARN: avatar item=0x{itemTemplateId:X8} 无 [avatar type select] 档位 {product.Count}, 点券价沿用 {ceraPrice}");
                }
            }
            var perUnit = Math.Max(1, buyCount);
            var totalGoldLong = (long)goldPrice * perUnit;
            var totalCeraLong = (long)ceraPrice * perUnit;
            if (totalGoldLong > int.MaxValue || totalCeraLong > int.MaxValue)
            {
                FileLogger.Log($"  [CeraShopBuy] REJECT: cost overflow product=0x{productId:X8} item=0x{itemTemplateId:X8} gold={goldPrice} cera={ceraPrice} buyCount={buyCount}");
                return false;
            }
            var totalGoldCost = (int)totalGoldLong;
            var totalCeraCost = (int)totalCeraLong;
            // 点券支付方式: buy only cera=仅点券; buy only cera point=仅欢乐券+代币券; 否则瀑布(欢乐→代币→点券)。
            var ceraMode = CeraShopProductCatalog.IsBuyOnlyCera(itemTemplateId) ? CeraPayMode.OnlyCera
                : CeraShopProductCatalog.IsBuyOnlyCeraPoint(itemTemplateId) ? CeraPayMode.OnlyCeraPoint
                : CeraPayMode.Default;

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var wallet = LoadWallet(connection, transaction);
                var plan = ComputeCeraShopPayment(wallet, totalGoldCost, totalCeraCost, ceraMode);
                FileLogger.Log($"  [CeraShopBuy] product=0x{productId:X8} -> item=0x{itemTemplateId:X8} section={product.Section} kind={itemKind} count={effectiveCount} gold={totalGoldCost} cera={totalCeraCost} mode={ceraMode} wallet(g={wallet.Gold},c={wallet.Coin},t={wallet.TokenCera},h={wallet.HappyTokenCera}) ok={plan.Ok}");
                if (!plan.Ok)
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: insufficient funds gold={totalGoldCost} cera={totalCeraCost} mode={ceraMode}");
                    return false;
                }
                var goldSpent = totalGoldCost > 0;

                if (isStackable)
                {
                    var existingItem = FindItemByTemplateId(connection, transaction, InventoryListType.Main, itemTemplateId);
                    var stackLimit = metadata.StackLimit;
                    if (existingItem != null && (stackLimit <= 0 || existingItem.StackCount + effectiveCount <= stackLimit))
                    {
                        var newStackCount = existingItem.StackCount + effectiveCount;
                        UpdateStackCount(connection, transaction, existingItem.ItemUid, newStackCount);
                        ApplyCeraShopPayment(connection, transaction, plan);
                        WriteBuyAuditLog(connection, transaction, itemTemplateId, existingItem.SlotIndex, totalGoldCost, totalCeraCost);
                        transaction.Commit();

                        result = new InventoryMutationResult
                        {
                            ListType = InventoryListType.Main,
                            SlotIndex = existingItem.SlotIndex,
                            ItemTemplateId = itemTemplateId,
                            RemainingStackCount = newStackCount,
                            InstanceValue = newStackCount,
                            Durability = 0,
                            UpdatedGold = plan.NewGold,
                            UpdatedSp = wallet.Sp,
                            UpdatedCoin = plan.NewCera,
                            UpdatedTokenCera = plan.NewTokenCera,
                            UpdatedHappyTokenCera = plan.NewHappyTokenCera,
                            GoldSpent = goldSpent,
                            RequestedCount = (short)effectiveCount,
                            AppliedCount = (short)effectiveCount,
                        };
                        return true;
                    }
                }

                int slotStart;
                int slotEnd;
                var insertListType = InventoryListType.Main;
                var insertKind = itemKind;
                var expireTime = isStackable ? 0 : -1;   // -1 = 永久(装备/永久时装)
                if (isAvatar)
                {
                    insertListType = InventoryListType.Avatar;  // 时装进时装库存, 不进主库存装备槽
                    insertKind = "avatar";
                    slotStart = 0;
                    slotEnd = 500;
                    if (avatarDurationDays > 0)
                    {
                        var unixNow = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                        expireTime = (int)Math.Min(int.MaxValue, unixNow + (long)avatarDurationDays * 86400L);
                    }
                }
                else if (isCreature)
                {
                    insertListType = InventoryListType.Pet;  // 宠物进专用宠物栏(list 7), 不进主背包装备格
                    insertKind = "pet";
                    slotStart = PetInventorySlotStart;
                    slotEnd = PetInventorySlotEnd;
                    expireTime = 0;
                }
                else
                {
                    metadata.GetSlotRange(out slotStart, out slotEnd);
                }

                var targetSlot = FindEmptySlot(connection, transaction, insertListType, slotStart, slotEnd);
                if (targetSlot < 0)
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: no empty slot product=0x{productId:X8} item=0x{itemTemplateId:X8} list={insertListType} slotRange={slotStart}-{slotEnd}");
                    return false;
                }

                var petSerial = isCreature ? NextPetSerialOrHandle(connection, transaction) : 0;
                var instanceValue = isStackable ? effectiveCount : (isCreature || isAvatar ? 0 : GenerateInstanceValue(itemTemplateId, targetSlot));
                var durability = (isAvatar || isCreature) ? (ushort)0 : metadata.Durability;
                if (isAvatar)
                {
                    var avatarItem = CreateDefaultAvatarItem((short)targetSlot, itemTemplateId, 0);
                    InsertCharacterItem(
                        connection,
                        transaction,
                        insertListType,
                        avatarItem.SlotIndex,
                        avatarItem.AvatarItemId,
                        insertKind,
                        0,
                        0,
                        durability,
                        0,
                        avatarItem.OptionValue,
                        expireTime,
                        avatarItem.UnknownFixed30,
                        0,
                        SerializeAvatar(avatarItem));
                }
                else
                {
                    InsertCharacterItem(
                        connection,
                        transaction,
                        insertListType,
                        (short)targetSlot,
                        itemTemplateId,
                        insertKind,
                        isCreature ? 0 : effectiveCount,
                        instanceValue,
                        durability,
                        0,
                        0,
                        expireTime,
                        0,
                        petSerial,
                        "{}");
                }

                ApplyCeraShopPayment(connection, transaction, plan);
                WriteBuyAuditLog(connection, transaction, itemTemplateId, (short)targetSlot, totalGoldCost, totalCeraCost);
                transaction.Commit();

                result = new InventoryMutationResult
                {
                    ListType = insertListType,
                    SlotIndex = (short)targetSlot,
                    ItemTemplateId = itemTemplateId,
                    RemainingStackCount = effectiveCount,
                    InstanceValue = instanceValue,
                    Durability = durability,
                    UpdatedGold = plan.NewGold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = plan.NewCera,
                    UpdatedTokenCera = plan.NewTokenCera,
                    UpdatedHappyTokenCera = plan.NewHappyTokenCera,
                    GoldSpent = goldSpent,
                    RequestedCount = (short)effectiveCount,
                    AppliedCount = (short)effectiveCount,
                };
                return true;
            }
        }

        private ItemRecord FindItemByTemplateId(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, int templateId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value, durability
FROM character_items
WHERE character_id = @characterId AND list_type = @listType AND item_template_id = @templateId
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@templateId", templateId);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new ItemRecord
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

        private ItemRecord FindItemByTemplateIdInRange(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, int templateId, int slotStart, int slotEnd)
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
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@templateId", templateId);
                command.Parameters.AddWithValue("@slotStart", slotStart);
                command.Parameters.AddWithValue("@slotEnd", slotEnd);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new ItemRecord
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

        private  int FindEmptySlot(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, int slotStart = 0, int slotEnd = -1)
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
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        occupiedSlots.Add(reader.GetInt32(0));
                }
            }

            var maxSlot = slotEnd >= 0 ? slotEnd : (listType == InventoryListType.Main ? 359 : 199);
            for (var slot = slotStart; slot <= maxSlot; slot++)
            {
                if (!occupiedSlots.Contains(slot))
                    return slot;
            }

            return -1;
        }

        private static int GenerateInstanceValue(int itemTemplateId, int slotIndex)
        {
            return 999999998;
        }

        // 宠物判定: 物品在 equipment.lst 且 .equ 的 [equipment type] 为 [creature]。
        // CreatureExtraResolver 对不在 equipment.lst 的物品会抛异常, 这里吞掉返回 false。
        private static bool IsCreatureItem(int itemTemplateId)
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

        // 为新购宠物生成栏内唯一 serial/handle (取当前角色宠物栏 max+1, 从 1 起)。
        private int NextPetSerialOrHandle(SqliteConnection connection, SqliteTransaction transaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COALESCE(MAX(pet_serial_or_handle), 0) + 1
FROM character_items
WHERE character_id = @characterId AND list_type = @listType;";
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.Pet);
                var next = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                return next < 1 ? 1 : next;
            }
        }

        private  void InsertSplitItem(SqliteConnection connection, SqliteTransaction transaction, ItemRecord source, InventoryListType listType, short slotIndex, int moveCount)
        {
            InsertCharacterItem(
                connection,
                transaction,
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

        private  void SwapItems(SqliteConnection connection, SqliteTransaction transaction, ItemRecord source, ItemRecord destination)
        {
            UpdateItemPosition(connection, transaction, source.ItemUid, source.ListType, short.MinValue);
            UpdateItemPosition(connection, transaction, destination.ItemUid, source.ListType, source.SlotIndex);
            UpdateItemPosition(connection, transaction, source.ItemUid, destination.ListType, destination.SlotIndex);
        }

        private  ItemRecord LoadItemRecord(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json
FROM character_items
WHERE character_id = @characterId AND list_type = @listType AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return ReadItemRecord(reader);
                }
            }
        }

        private CommonInventoryItem LoadCommonItem(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json
FROM character_items
WHERE character_id = @characterId AND list_type = @listType AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return ReadCommonItem(reader, reader.IsDBNull(12) ? "{}" : reader.GetString(12));
                }
            }
        }

        private  List<ItemRecord> LoadItemsByListType(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType)
        {
            var items = new List<ItemRecord>();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_uid, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json
FROM character_items
WHERE character_id = @characterId AND list_type = @listType
ORDER BY slot_index;";
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)listType);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        items.Add(ReadItemRecord(reader));
                }
            }

            return items;
        }

        private  ItemRecord ReadItemRecord(SqliteDataReader reader)
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

        private  void WriteAuditLog(SqliteConnection connection, SqliteTransaction transaction, string actionName, ItemRecord source, InventoryListType destinationListType, short destinationSlotIndex, int moveCount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO item_audit_log (
    owner_scope, owner_id, character_id, action_name, list_type, slot_index, item_uid,
    item_template_id, delta_stack_count, payload_json)
VALUES (
    'character', @ownerId, @characterId, @actionName, @listType, @slotIndex, @itemUid,
    @itemTemplateId, @deltaStackCount, @payloadJson);";
                command.Parameters.AddWithValue("@ownerId", DefaultCharacterId);
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@actionName", actionName);
                command.Parameters.AddWithValue("@listType", (int)destinationListType);
                command.Parameters.AddWithValue("@slotIndex", destinationSlotIndex);
                command.Parameters.AddWithValue("@itemUid", source.ItemUid);
                command.Parameters.AddWithValue("@itemTemplateId", source.ItemTemplateId);
                command.Parameters.AddWithValue("@deltaStackCount", moveCount);
                command.Parameters.AddWithValue("@payloadJson", "{\"srcListType\":" + (int)source.ListType + ",\"srcSlotIndex\":" + source.SlotIndex + ",\"dstListType\":" + (int)destinationListType + ",\"dstSlotIndex\":" + destinationSlotIndex + "}");
                command.ExecuteNonQuery();
            }
        }

        private  void WriteDeleteAuditLog(SqliteConnection connection, SqliteTransaction transaction, ItemRecord source, int deleteCount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO item_audit_log (
    owner_scope, owner_id, character_id, action_name, list_type, slot_index, item_uid,
    item_template_id, delta_stack_count, payload_json)
VALUES (
    'character', @ownerId, @characterId, 'delete_item', @listType, @slotIndex, @itemUid,
    @itemTemplateId, @deltaStackCount, @payloadJson);";
                command.Parameters.AddWithValue("@ownerId", DefaultCharacterId);
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)source.ListType);
                command.Parameters.AddWithValue("@slotIndex", source.SlotIndex);
                command.Parameters.AddWithValue("@itemUid", source.ItemUid);
                command.Parameters.AddWithValue("@itemTemplateId", source.ItemTemplateId);
                command.Parameters.AddWithValue("@deltaStackCount", -deleteCount);
                command.Parameters.AddWithValue("@payloadJson", "{\"deleteCount\":" + deleteCount + "}");
                command.ExecuteNonQuery();
            }
        }

        private void WriteEnchantAuditLog(SqliteConnection connection, SqliteTransaction transaction, ItemRecord bead, ItemRecord target, int enchantCardItemId, byte enchantUpgradeCount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO item_audit_log (
    owner_scope, owner_id, character_id, action_name, list_type, slot_index, item_uid,
    item_template_id, delta_stack_count, payload_json)
VALUES (
    'character', @ownerId, @characterId, 'enchant_by_bead', @listType, @slotIndex, @itemUid,
    @itemTemplateId, 0, @payloadJson);";
                command.Parameters.AddWithValue("@ownerId", DefaultCharacterId);
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)target.ListType);
                command.Parameters.AddWithValue("@slotIndex", target.SlotIndex);
                command.Parameters.AddWithValue("@itemUid", target.ItemUid);
                command.Parameters.AddWithValue("@itemTemplateId", target.ItemTemplateId);
                command.Parameters.AddWithValue("@payloadJson",
                    "{\"beadItemUid\":" + bead.ItemUid
                    + ",\"beadItemTemplateId\":" + bead.ItemTemplateId
                    + ",\"beadSlotIndex\":" + bead.SlotIndex
                    + ",\"enchantCardItemId\":" + enchantCardItemId
                    + ",\"enchantUpgradeCount\":" + enchantUpgradeCount
                    + "}");
                command.ExecuteNonQuery();
            }
        }

        private  void WriteBuyAuditLog(SqliteConnection connection, SqliteTransaction transaction, int itemTemplateId, short slotIndex, int buyGold, int buyCoin)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO item_audit_log (
    owner_scope, owner_id, character_id, action_name, list_type, slot_index,
    item_template_id, delta_stack_count, payload_json)
VALUES (
    'character', @ownerId, @characterId, 'buy_item', @listType, @slotIndex,
    @itemTemplateId, 1, @payloadJson);";
                command.Parameters.AddWithValue("@ownerId", DefaultCharacterId);
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)InventoryListType.Main);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.Parameters.AddWithValue("@itemTemplateId", itemTemplateId);
                command.Parameters.AddWithValue("@payloadJson", "{\"buyGold\":" + buyGold + ",\"buyCoin\":" + buyCoin + "}");
                command.ExecuteNonQuery();
            }
        }

        private void WriteOpenPackageAuditLog(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ItemRecord packageItem,
            int addedAvatarCount,
            int addedMainItemCount,
            int addedPetCount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO item_audit_log (
    owner_scope, owner_id, character_id, action_name, list_type, slot_index, item_uid,
    item_template_id, delta_stack_count, payload_json)
VALUES (
    'character', @ownerId, @characterId, 'open_avatar_package', @listType, @slotIndex, @itemUid,
    @itemTemplateId, -1, @payloadJson);";
                command.Parameters.AddWithValue("@ownerId", DefaultCharacterId);
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)packageItem.ListType);
                command.Parameters.AddWithValue("@slotIndex", packageItem.SlotIndex);
                command.Parameters.AddWithValue("@itemUid", packageItem.ItemUid);
                command.Parameters.AddWithValue("@itemTemplateId", packageItem.ItemTemplateId);
                command.Parameters.AddWithValue("@payloadJson",
                    "{\"addedAvatarCount\":" + addedAvatarCount
                    + ",\"addedMainItemCount\":" + addedMainItemCount
                    + ",\"addedPetCount\":" + addedPetCount + "}");
                command.ExecuteNonQuery();
            }
        }

        private void WriteOpenSelectablePackageAuditLog(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ItemRecord packageItem,
            PackageRewardEntry reward,
            int addedMainItemCount,
            int addedPetCount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO item_audit_log (
    owner_scope, owner_id, character_id, action_name, list_type, slot_index, item_uid,
    item_template_id, delta_stack_count, payload_json)
VALUES (
    'character', @ownerId, @characterId, 'open_selectable_package', @listType, @slotIndex, @itemUid,
    @itemTemplateId, -1, @payloadJson);";
                command.Parameters.AddWithValue("@ownerId", DefaultCharacterId);
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)packageItem.ListType);
                command.Parameters.AddWithValue("@slotIndex", packageItem.SlotIndex);
                command.Parameters.AddWithValue("@itemUid", packageItem.ItemUid);
                command.Parameters.AddWithValue("@itemTemplateId", packageItem.ItemTemplateId);
                command.Parameters.AddWithValue("@payloadJson",
                    "{\"rewardItemTemplateId\":" + reward.ItemTemplateId
                    + ",\"rewardCount\":" + reward.Count
                    + ",\"addedMainItemCount\":" + addedMainItemCount
                    + ",\"addedPetCount\":" + addedPetCount + "}");
                command.ExecuteNonQuery();
            }
        }

        private  void WriteSellAuditLog(SqliteConnection connection, SqliteTransaction transaction, ItemRecord source, int sellCount, int goldDelta)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO item_audit_log (
    owner_scope, owner_id, character_id, action_name, list_type, slot_index, item_uid,
    item_template_id, delta_stack_count, payload_json)
VALUES (
    'character', @ownerId, @characterId, 'sell_item', @listType, @slotIndex, @itemUid,
    @itemTemplateId, @deltaStackCount, @payloadJson);";
                command.Parameters.AddWithValue("@ownerId", DefaultCharacterId);
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)source.ListType);
                command.Parameters.AddWithValue("@slotIndex", source.SlotIndex);
                command.Parameters.AddWithValue("@itemUid", source.ItemUid);
                command.Parameters.AddWithValue("@itemTemplateId", source.ItemTemplateId);
                command.Parameters.AddWithValue("@deltaStackCount", -sellCount);
                command.Parameters.AddWithValue("@payloadJson", "{\"sellCount\":" + sellCount + ",\"goldDelta\":" + goldDelta + "}");
                command.ExecuteNonQuery();
            }
        }

        private  void WriteSortAuditLog(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, int affectedCount)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO item_audit_log (
    owner_scope, owner_id, character_id, action_name, list_type, delta_stack_count, payload_json)
VALUES (
    'character', @ownerId, @characterId, 'sort_item', @listType, 0, @payloadJson);";
                command.Parameters.AddWithValue("@ownerId", DefaultCharacterId);
                command.Parameters.AddWithValue("@characterId", DefaultCharacterId);
                command.Parameters.AddWithValue("@listType", (int)listType);
                command.Parameters.AddWithValue("@payloadJson", "{\"affectedCount\":" + affectedCount + "}");
                command.ExecuteNonQuery();
            }
        }

        private  CommonInventoryItem ReadCommonItem(SqliteDataReader reader, string extraJson)
        {
            return new CommonInventoryItem
            {
                SlotIndex = Convert.ToInt16(reader.GetInt32(1), CultureInfo.InvariantCulture),
                ItemTemplateId = reader.GetInt32(2),
                CountOrInstanceValue = reader.GetInt32(4),
                Durability = Convert.ToUInt16(reader.GetInt32(6), CultureInfo.InvariantCulture),
                SealFlag = Convert.ToByte(reader.GetInt32(7), CultureInfo.InvariantCulture),
                ExpireTime = reader.GetInt32(9),
                Marker16 = reader.GetInt32(10),
                ExtData0 = Convert.ToByte(ReadIntValue(extraJson, "extData0"), CultureInfo.InvariantCulture),
                PrefixData0E = ReadHexValue(extraJson, "prefixData0E", 8),
                MiddleData1A = ReadHexValue(extraJson, "middleData1A", 17),
                TailData2F = ReadHexValue(extraJson, "tailData2F", 37),
            };
        }

        private  AvatarInventoryItem ReadAvatarItem(SqliteDataReader reader, string extraJson)
        {
            return new AvatarInventoryItem
            {
                SlotIndex = Convert.ToInt16(reader.GetInt32(1), CultureInfo.InvariantCulture),
                AvatarItemId = reader.GetInt32(2),
                OptionValue = Convert.ToByte(reader.GetInt32(8), CultureInfo.InvariantCulture),
                UnknownFixed30 = reader.GetInt32(10),
                UnknownFixed4 = Convert.ToUInt16(ReadIntValue(extraJson, "unknownFixed4"), CultureInfo.InvariantCulture),
                Reserved0 = ReadHexValue(extraJson, "reserved0", 5),
                Reserved1 = ReadHexValue(extraJson, "reserved1", 71),
                Reserved2 = ReadHexValue(extraJson, "reserved2", 30),
                TailData = ReadHexValue(extraJson, "tailData", 7),
            };
        }

        private AvatarInventoryItem ReadEquipmentAsAvatarItem(SqliteDataReader reader, string extraJson)
        {
            var common = ReadCommonItem(reader, extraJson);
            var buf = new byte[126];
            buf[0] = (byte)(common.SlotIndex & 0xFF);
            buf[1] = (byte)((common.SlotIndex >> 8) & 0xFF);
            buf[2] = (byte)(common.ItemTemplateId & 0xFF);
            buf[3] = (byte)((common.ItemTemplateId >> 8) & 0xFF);
            buf[4] = (byte)((common.ItemTemplateId >> 16) & 0xFF);
            buf[5] = (byte)((common.ItemTemplateId >> 24) & 0xFF);
            buf[6] = (byte)(common.CountOrInstanceValue & 0xFF);
            buf[7] = (byte)((common.CountOrInstanceValue >> 8) & 0xFF);
            buf[8] = (byte)((common.CountOrInstanceValue >> 16) & 0xFF);
            buf[9] = (byte)((common.CountOrInstanceValue >> 24) & 0xFF);
            buf[10] = common.ExtData0;
            buf[11] = (byte)(common.Durability & 0xFF);
            buf[12] = (byte)((common.Durability >> 8) & 0xFF);
            buf[13] = common.SealFlag;
            Array.Copy(common.PrefixData0E, 0, buf, 14, 8);
            buf[22] = (byte)(common.Marker16 & 0xFF);
            buf[23] = (byte)((common.Marker16 >> 8) & 0xFF);
            buf[24] = (byte)((common.Marker16 >> 16) & 0xFF);
            buf[25] = (byte)((common.Marker16 >> 24) & 0xFF);
            Array.Copy(common.MiddleData1A, 0, buf, 26, 17);
            buf[43] = (byte)(common.ExpireTime & 0xFF);
            buf[44] = (byte)((common.ExpireTime >> 8) & 0xFF);
            buf[45] = (byte)((common.ExpireTime >> 16) & 0xFF);
            buf[46] = (byte)((common.ExpireTime >> 24) & 0xFF);
            Array.Copy(common.TailData2F, 0, buf, 47, 37);

            Array.Clear(buf, 6, 78);
            buf[84] = 0x1E;
            buf[118] = 0x04;
            var jewel = ReadHexValue(extraJson, "jewelSocket", 30);
            if (jewel != null && jewel.Length == 30)
                Array.Copy(jewel, 0, buf, 88, 30);

            return new AvatarInventoryItem
            {
                SlotIndex = BitConverter.ToInt16(buf, 0),
                AvatarItemId = BitConverter.ToInt32(buf, 2),
                Reserved0 = CharacterItemListSnapshot.Slice(buf, 6, 5),
                OptionValue = buf[11],
                Reserved1 = CharacterItemListSnapshot.Slice(buf, 12, 71),
                UnknownFixed30 = BitConverter.ToInt32(buf, 83),
                Reserved2 = CharacterItemListSnapshot.Slice(buf, 87, 30),
                UnknownFixed4 = BitConverter.ToUInt16(buf, 117),
                TailData = CharacterItemListSnapshot.Slice(buf, 119, 7),
            };
        }

        private  PetInventoryItem ReadPetItem(SqliteDataReader reader, string extraJson)
        {
            return new PetInventoryItem
            {
                SlotIndex = Convert.ToInt16(reader.GetInt32(1), CultureInfo.InvariantCulture),
                CreatureItemId = reader.GetInt32(2),
                CreatureSerialOrHandle = reader.GetInt32(11),
                TailData0A = ReadHexValue(extraJson, "tailData0A", 74),
            };
        }

        private static string InferCommonItemKind(CommonInventoryItem item)
        {
            if (item.ItemTemplateId <= 0)
                return "special";

            if (item.ExpireTime != 0)
                return "special";

            return item.Marker16 == 0 ? "stackable" : "equipment";
        }

        private static string SerializeCommon(CommonInventoryItem item)
        {
            return "{"
                + "\"extData0\":" + item.ExtData0.ToString(CultureInfo.InvariantCulture)
                + ",\"prefixData0E\":\"" + ToHex(item.PrefixData0E) + "\""
                + ",\"middleData1A\":\"" + ToHex(item.MiddleData1A) + "\""
                + ",\"tailData2F\":\"" + ToHex(item.TailData2F) + "\""
                + "}";
        }

        private static string SerializeAvatar(AvatarInventoryItem item)
        {
            return "{"
                + "\"reserved0\":\"" + ToHex(item.Reserved0) + "\""
                + ",\"reserved1\":\"" + ToHex(item.Reserved1) + "\""
                + ",\"reserved2\":\"" + ToHex(item.Reserved2) + "\""
                + ",\"unknownFixed4\":" + item.UnknownFixed4.ToString(CultureInfo.InvariantCulture)
                + ",\"tailData\":\"" + ToHex(item.TailData) + "\""
                + "}";
        }

        private static string SerializePet(PetInventoryItem item)
        {
            return "{\"tailData0A\":\"" + ToHex(item.TailData0A) + "\"}";
        }

        private static CommonInventoryItem CreateEmptyCommonItem(short slotIndex)
        {
            return new CommonInventoryItem
            {
                SlotIndex = slotIndex,
            };
        }

        private static byte[] NormalizeBytes(byte[] source, int expectedLength)
        {
            var buffer = new byte[expectedLength];
            if (source != null && source.Length > 0)
                Array.Copy(source, 0, buffer, 0, Math.Min(source.Length, expectedLength));
            return buffer;
        }

        private static string ToHex(byte[] data)
        {
            return BitConverter.ToString(data ?? new byte[0]).Replace("-", string.Empty);
        }

        private static int ReadIntValue(string json, string propertyName)
        {
            var token = "\"" + propertyName + "\":";
            var start = json.IndexOf(token, StringComparison.Ordinal);
            if (start < 0)
                return 0;

            start += token.Length;
            var end = json.IndexOfAny(new[] { ',', '}' }, start);
            if (end < 0)
                end = json.Length;

            var valueText = json.Substring(start, end - start);
            return int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }

        private static byte[] ReadHexValue(string json, string propertyName, int expectedLength)
        {
            var token = "\"" + propertyName + "\":\"";
            var start = json.IndexOf(token, StringComparison.Ordinal);
            if (start < 0)
                return new byte[expectedLength];

            start += token.Length;
            var end = json.IndexOf('"', start);
            if (end < 0)
                return new byte[expectedLength];

            var hex = json.Substring(start, end - start);
            return FromHex(hex, expectedLength);
        }

        private static byte ReadEnchantUpgradeCount(string extraJson)
        {
            // 86 附魔卡片升级次数跟随宝珠动态数据保存，写入装备时落到 common entry +0x12。
            var prefix = ReadHexValue(extraJson ?? string.Empty, "prefixData0E", 8);
            return prefix.Length >= 5 ? prefix[4] : (byte)0;
        }

        private static string ReadRawStringValue(string json, string propertyName)
        {
            var token = "\"" + propertyName + "\":\"";
            var start = json.IndexOf(token, StringComparison.Ordinal);
            if (start < 0) return null;
            start += token.Length;
            var end = json.IndexOf('"', start);
            if (end < 0) return null;
            return json.Substring(start, end - start);
        }

        private static byte[] FromHex(string hex, int expectedLength)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return new byte[expectedLength];

            var length = Math.Min(expectedLength, hex.Length / 2);
            var buffer = new byte[expectedLength];
            for (var index = 0; index < length; index++)
                buffer[index] = byte.Parse(hex.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            return buffer;
        }

        private sealed class ItemRecord
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

        private sealed class WalletState
        {
            public int Gold { get; set; }

            public int Sp { get; set; }

            public int Coin { get; set; }

            public int TokenCera { get; set; }

            public int HappyTokenCera { get; set; }
        }
    }
}
