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

    public sealed class EquipmentSocketMutationResult
    {
        public CommonInventoryItem TargetItem { get; set; }

        public InventoryMutationResult MaterialItem { get; set; }

        public bool MaterialConsumed { get; set; }
    }

    public sealed class EquipmentEmblemApplyRequest
    {
        public short EmblemSlot { get; set; }

        public int EmblemItemTemplateId { get; set; }

        public byte SocketIndex { get; set; }
    }

    public sealed class EquipmentEmblemMutationResult
    {
        public CommonInventoryItem TargetItem { get; set; }

        public bool TargetEquipped { get; set; }

        public List<InventoryMutationResult> ConsumedEmblems { get; } = new List<InventoryMutationResult>();
    }

    public sealed class AvatarSocketMutationResult
    {
        public AvatarInventoryItem TargetItem { get; set; }

        public InventoryMutationResult MaterialItem { get; set; }

        public bool MaterialConsumed { get; set; }
    }

    public sealed class AvatarEmblemMutationResult
    {
        public AvatarInventoryItem TargetItem { get; set; }

        public List<InventoryMutationResult> ConsumedEmblems { get; } = new List<InventoryMutationResult>();
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

        // 合并装扮(时装合成): 扣掉 slot1/slot2 两件旧时装 + 1 个消耗品(合成器), 在时装栏第一个
        // 空位插入新时装。新时装itemId由 resolveNewItemId(oldItemId1, oldItemId2, consumeMaterialId)
        // 回调计算(在事务内、读到三个真实item之后才调用, 保证概率判定用的是事务内的真实数据)。
        // 返回新时装所在 slot (newSlotOut)。一个事务内完成, 失败回滚。
        public bool TryCompoundAvatar(short slot1, short slot2, short consumeSlot,
                Func<int, int, int, List<int>> resolveNewItemIds, byte newOption,
                out List<int> newSlotsOut, out int oldItemId1, out int oldItemId2, out List<int> newItemIdsOut,
                out int consumedItemTemplateId, out int consumedItemRemainingCount)
        {
            newSlotsOut = new List<int>();
            oldItemId1 = 0;
            oldItemId2 = 0;
            newItemIdsOut = new List<int>();
            consumedItemTemplateId = 0;
            consumedItemRemainingCount = 0;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var item1 = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Avatar, slot1);
                var item2 = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Avatar, slot2);
                if (item1 == null || item2 == null)
                {
                    FileLogger.Log($"  [CompoundAvatar] REJECT: missing avatar item at slot1={slot1}(found={item1!=null}) slot2={slot2}(found={item2!=null})");
                    return false;
                }

                var consumeItem = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Main, consumeSlot);
                if (consumeItem == null || consumeItem.StackCount < 1)
                {
                    FileLogger.Log($"  [CompoundAvatar] REJECT: missing consumable at slot={consumeSlot}");
                    return false;
                }

                oldItemId1 = item1.ItemTemplateId;
                oldItemId2 = item2.ItemTemplateId;
                var newItemIds = resolveNewItemIds(oldItemId1, oldItemId2, consumeItem.ItemTemplateId);
                newItemIdsOut = newItemIds;

                // 台服做法(逆向 CInventory::AddAvatarItem 得到, 0x8509b9e): 删掉slot1/slot2两个槛
                // (reset清空, 不移动其他物品、不紧凑重排), 然后从槽位0开始线性扫描第一个itemId=0的
                // 空槛插入新时装(台服硬编码上限104=105格, 对应台服固定大小时装栏)。
                // 86JP时装栏格数公式(实测确认): 基础105格(固定) + list_param16拓展值(0~105, 每用1张
                // "装扮栏拓展券"+7格) = 总格数(105~210)。character_container_state.list_param16
                // 就是这个拓展值, 不是"已解锁格数"本身, 上限要用 105+该值 才对。
                _db.DeleteItem(connection, transaction, item1.ItemUid);
                _db.DeleteItem(connection, transaction, item2.ItemUid);

                consumedItemTemplateId = consumeItem.ItemTemplateId;
                if (consumeItem.StackCount > 1)
                {
                    consumedItemRemainingCount = consumeItem.StackCount - 1;
                    _db.UpdateStackCount(connection, transaction, consumeItem.ItemUid, consumedItemRemainingCount);
                }
                else
                {
                    consumedItemRemainingCount = 0;
                    _db.DeleteItem(connection, transaction, consumeItem.ItemUid);
                }

                var avatarExpansion = GetListParam(_equipStore.LoadContainerState(connection, transaction, _context.CharacterId, _context.AccountId), InventoryListType.Avatar);
                var avatarCapacity = 105 + avatarExpansion;

                foreach (var newItemId in newItemIds)
                {
                    var newSlot = _db.FindEmptySlot(connection, transaction, _context.CharacterId, InventoryListType.Avatar, 0, avatarCapacity - 1);
                    if (newSlot < 0)
                    {
                        FileLogger.Log($"  [CompoundAvatar] REJECT: no empty avatar slot (capacity={avatarCapacity})");
                        return false;
                    }

                    _db.InsertAvatarItem(connection, transaction, _context.CharacterId, CreateDefaultAvatarItem((short)newSlot, newItemId, newOption));
                    newSlotsOut.Add(newSlot);
                }

                transaction.Commit();
                FileLogger.Log($"  [CompoundAvatar] OK: deleted slot{slot1}(item {oldItemId1}) + slot{slot2}(item {oldItemId2}) + " +
                               $"1x slot{consumeSlot}(template {consumedItemTemplateId}, remain {consumedItemRemainingCount}), " +
                               $"added items [{string.Join(",", newItemIds)}] at slots [{string.Join(",", newSlotsOut)}]");
                return true;
            }
        }

        // 8件高级装扮 -> 100%合成指定稀有装扮(克隆装扮合成器, 如"旷古天娇"系列)。
        // 消耗品按请求body里携带的Main列表槛位号精确定位(实测两组不同槛位数据交叉验证得到该字段)。
        // resolveNewItemId: 输入消耗品item_template_id, 由调用方(AbsoluteBindCubeService)按该合成器
        // 的[action type]配置 + 角色职业查表校验/纠正客户端请求的目标itemId; 返回负数表示校验失败。
        public bool TryCompoundAvatarSet(short[] consumeSlots, int[] expectedItemIds, Func<int, int> resolveNewItemId, byte newOption,
                short consumeStackableSlot,
                out int newSlot, out List<int> oldItemIds, out int newItemId, out int consumedItemTemplateId, out int consumedItemRemainingCount)
        {
            newSlot = -1;
            oldItemIds = new List<int>();
            newItemId = 0;
            consumedItemTemplateId = 0;
            consumedItemRemainingCount = 0;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var items = new ItemRecord[consumeSlots.Length];
                for (int i = 0; i < consumeSlots.Length; i++)
                {
                    items[i] = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Avatar, consumeSlots[i]);
                    if (items[i] == null)
                    {
                        FileLogger.Log($"  [CompoundAvatarSet] REJECT: missing avatar item at slot={consumeSlots[i]}");
                        return false;
                    }

                    // 防改包: 比对客户端声称的 itemId 与 DB 实际物品, 不符则拒绝(防止改包删任意槽位物品)
                    if (expectedItemIds != null && i < expectedItemIds.Length && expectedItemIds[i] != items[i].ItemTemplateId)
                    {
                        FileLogger.Log($"  [CompoundAvatarSet] REJECT: itemId mismatch at slot={consumeSlots[i]} expected=0x{expectedItemIds[i]:X8} actual=0x{items[i].ItemTemplateId:X8}");
                        return false;
                    }
                }

                var consumeItem = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Main, consumeStackableSlot);
                if (consumeItem == null || consumeItem.StackCount < 1)
                {
                    FileLogger.Log($"  [CompoundAvatarSet] REJECT: missing consumable at slot={consumeStackableSlot}");
                    return false;
                }

                newItemId = resolveNewItemId(consumeItem.ItemTemplateId);
                if (newItemId < 0)
                {
                    FileLogger.Log($"  [CompoundAvatarSet] REJECT: resolveNewItemId rejected consumable template={consumeItem.ItemTemplateId}");
                    return false;
                }

                foreach (var item in items)
                {
                    oldItemIds.Add(item.ItemTemplateId);
                    _db.DeleteItem(connection, transaction, item.ItemUid);
                }

                consumedItemTemplateId = consumeItem.ItemTemplateId;
                if (consumeItem.StackCount > 1)
                {
                    consumedItemRemainingCount = consumeItem.StackCount - 1;
                    _db.UpdateStackCount(connection, transaction, consumeItem.ItemUid, consumedItemRemainingCount);
                }
                else
                {
                    consumedItemRemainingCount = 0;
                    _db.DeleteItem(connection, transaction, consumeItem.ItemUid);
                }

                // 同 TryCompoundAvatar: 按台服 AddAvatarItem 算法从槽位0扫描第一个空槛位, 上限按
                // 105(基础) + character_container_state.list_param16(拓展值, 0~105, 每张拓展券+7)。
                var avatarExpansion = GetListParam(_equipStore.LoadContainerState(connection, transaction, _context.CharacterId, _context.AccountId), InventoryListType.Avatar);
                var avatarCapacity = 105 + avatarExpansion;
                var emptySlot = _db.FindEmptySlot(connection, transaction, _context.CharacterId, InventoryListType.Avatar, 0, avatarCapacity - 1);
                if (emptySlot < 0)
                {
                    FileLogger.Log($"  [CompoundAvatarSet] REJECT: no empty avatar slot (capacity={avatarCapacity})");
                    return false;
                }

                _db.InsertAvatarItem(connection, transaction, _context.CharacterId, CreateDefaultAvatarItem((short)emptySlot, newItemId, newOption));

                transaction.Commit();
                newSlot = emptySlot;
                FileLogger.Log($"  [CompoundAvatarSet] OK: consumed {items.Length} avatar items + 1x slot {consumeStackableSlot}(template {consumeItem.ItemTemplateId}), added item {newItemId} at slot {newSlot}");
                return true;
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
        // Client pet inventory pages share list 7 but use separate slot ranges:
        // category 5 = pets, category 6 = pet equipment, category 7 = pet consumables.
        internal const int PetInventorySlotStart = 0;
        internal const int PetInventorySlotEnd = 139;
        internal const int PetEquipmentSlotStart = 140;
        internal const int PetEquipmentSlotEnd = 188;
        internal const int PetConsumableSlotStart = 189;
        internal const int PetConsumableSlotEnd = 237;
        internal const int AvatarEmblemSlotStart = 289;
        internal const int AvatarEmblemSlotEnd = 344;
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

        public bool TryOpenEquipmentSocket(short targetSlotIndex, int targetItemTemplateId, short materialSlotIndex, out EquipmentSocketMutationResult result)
        {
            result = null;
            if (targetItemTemplateId <= 0)
                return false;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var target = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Main, targetSlotIndex);
                if (target == null || target.ItemKind != "equipment" || target.ItemTemplateId != targetItemTemplateId)
                    return false;

                var common = _db.LoadCommonItem(connection, transaction, _context.CharacterId, InventoryListType.Main, targetSlotIndex);
                if (common == null)
                    return false;

                common.TailData2F = NormalizeBytes(common.TailData2F, 37);
                common.JewelSocket = NormalizeBytes(common.JewelSocket, 30);
                NormalizeEquipmentSocketLayout(common);
                RepairJewelSocketTypes(common, targetItemTemplateId);
                var currentOpenCount = CountOpenJewelSockets(common);
                if (currentOpenCount > 0)
                {
                    EnsureVisibleSocketCount(common, currentOpenCount);
                    _db.UpdateCommonExtraJson(connection, transaction, target.ItemUid, common);
                    _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "repair_equipment_socket", target, target.ListType, target.SlotIndex, 0);
                    transaction.Commit();

                    result = new EquipmentSocketMutationResult
                    {
                        TargetItem = common,
                        MaterialConsumed = false,
                    };
                    return true;
                }

                var material = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Main, materialSlotIndex);
                if (material == null || material.StackCount <= 0)
                    return false;

                SetEquipmentSocketOpenFields(common);
                _db.UpdateCommonExtraJson(connection, transaction, target.ItemUid, common);

                var remaining = Math.Max(0, material.StackCount - 1);
                if (remaining > 0)
                    _db.UpdateStackCount(connection, transaction, material.ItemUid, remaining);
                else
                {
                    _db.DeleteItem(connection, transaction, material.ItemUid);
                    DeleteSortItemLock(connection, transaction, material.ListType, material.SlotIndex);
                }

                _auditLogger.WriteDeleteAuditLog(connection, transaction, _context.CharacterId, material, 1);
                _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "open_equipment_socket", target, target.ListType, target.SlotIndex, 0);
                transaction.Commit();

                result = new EquipmentSocketMutationResult
                {
                    TargetItem = common,
                    MaterialItem = new InventoryMutationResult
                    {
                        ListType = material.ListType,
                        SlotIndex = material.SlotIndex,
                        ItemTemplateId = material.ItemTemplateId,
                        RemainingStackCount = remaining,
                        InstanceValue = remaining,
                        Durability = material.Durability,
                        RequestedCount = 1,
                        AppliedCount = 1,
                    },
                    MaterialConsumed = true,
                };
                return true;
            }
        }

        public bool TrySetEquipmentEmblems(short targetSlotIndex, int targetItemTemplateId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out EquipmentEmblemMutationResult result)
        {
            result = null;
            if (emblems == null || emblems.Count == 0)
                return false;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var target = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Main, targetSlotIndex);
                if (target == null || target.ItemKind != "equipment" || target.ItemTemplateId != targetItemTemplateId)
                    return TrySetEquippedEquipmentEmblems(connection, transaction, targetSlotIndex, targetItemTemplateId, emblems, out result);

                var common = _db.LoadCommonItem(connection, transaction, _context.CharacterId, InventoryListType.Main, targetSlotIndex);
                if (common == null)
                    return false;

                common.TailData2F = NormalizeBytes(common.TailData2F, 37);
                common.JewelSocket = NormalizeBytes(common.JewelSocket, 30);
                NormalizeEquipmentSocketLayout(common);
                RepairJewelSocketTypes(common, targetItemTemplateId);

                var openCount = CountOpenJewelSockets(common);
                if (openCount <= 0 && common.TailData2F[0] > 0)
                {
                    var rebuiltCount = Math.Min(2, (int)common.TailData2F[0]);
                    common.JewelSocket = BuildJewelSocketData(targetItemTemplateId);
                    EnsureVisibleSocketCount(common, rebuiltCount);
                    openCount = CountOpenJewelSockets(common);
                    FileLogger.Log($"  [EmblemAttach] repaired missing jewelSocket targetSlot={targetSlotIndex} item=0x{targetItemTemplateId:X8} count={openCount}");
                }

                if (openCount <= 0)
                {
                    FileLogger.Log($"  [EmblemAttach] REJECT: no open sockets targetSlot={targetSlotIndex} item=0x{targetItemTemplateId:X8} tailCount={common.TailData2F[0]} jewel={BitConverter.ToString(common.JewelSocket)}");
                    return false;
                }

                EnsureVisibleSocketCount(common, openCount);

                var consumed = new List<InventoryMutationResult>();
                foreach (var request in emblems)
                {
                    if (request.SocketIndex >= openCount || request.SocketIndex >= 5)
                        return false;

                    var socketType = GetJewelSocketType(common, request.SocketIndex);
                    var emblemType = ItemMetadataResolver.ResolveEmblemSocketType(request.EmblemItemTemplateId);
                    if (!CanAttachEmblemToJewelSocket(socketType, emblemType))
                    {
                        FileLogger.Log($"  [EmblemAttach] REJECT: socketType=0x{socketType:X2} emblemType=0x{emblemType:X2} emblem=0x{request.EmblemItemTemplateId:X8}");
                        return false;
                    }

                    var emblem = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Main, request.EmblemSlot);
                    if (emblem == null || emblem.ItemTemplateId != request.EmblemItemTemplateId || emblem.StackCount <= 0)
                        return false;

                    WriteEmblemToTail(common.TailData2F, request.SocketIndex, request.EmblemItemTemplateId);
                    WriteEmblemToJewelSocket(common.JewelSocket, request.SocketIndex, request.EmblemItemTemplateId);

                    var remaining = Math.Max(0, emblem.StackCount - 1);
                    if (remaining > 0)
                        _db.UpdateStackCount(connection, transaction, emblem.ItemUid, remaining);
                    else
                    {
                        _db.DeleteItem(connection, transaction, emblem.ItemUid);
                        DeleteSortItemLock(connection, transaction, emblem.ListType, emblem.SlotIndex);
                    }

                    _auditLogger.WriteDeleteAuditLog(connection, transaction, _context.CharacterId, emblem, 1);
                    consumed.Add(new InventoryMutationResult
                    {
                        ListType = emblem.ListType,
                        SlotIndex = emblem.SlotIndex,
                        ItemTemplateId = emblem.ItemTemplateId,
                        RemainingStackCount = remaining,
                        InstanceValue = remaining,
                        Durability = emblem.Durability,
                        RequestedCount = 1,
                        AppliedCount = 1,
                    });
                }

                _db.UpdateCommonExtraJson(connection, transaction, target.ItemUid, common);
                _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "set_equipment_emblems", target, target.ListType, target.SlotIndex, emblems.Count);
                transaction.Commit();

                result = new EquipmentEmblemMutationResult
                {
                    TargetItem = common,
                };
                result.ConsumedEmblems.AddRange(consumed);
                return true;
            }
        }

        private bool TrySetEquippedEquipmentEmblems(SqliteConnection connection, SqliteTransaction transaction, short targetSlotIndex, int targetItemTemplateId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out EquipmentEmblemMutationResult result)
        {
            result = null;
            var entry = LoadEquippedEntry(connection, transaction, _context.CharacterId, targetSlotIndex);
            if (entry == null || entry.ItemId != targetItemTemplateId || entry.Raw == null || entry.Raw.Length == 0)
                return false;

            var fields = MakeEquipListCodec.ParseDisplayFields(entry.Raw);
            var openCount = fields.Emblem != null && fields.Emblem.Length > 0 ? fields.Emblem[0] : 0;
            if (openCount <= 0)
            {
                FileLogger.Log($"  [EmblemAttach] REJECT equipped: no open sockets equipSlot={targetSlotIndex} item=0x{targetItemTemplateId:X8}");
                return false;
            }

            var socketType = ResolveJewelSocketType(targetItemTemplateId);
            var consumed = new List<InventoryMutationResult>();
            foreach (var request in emblems)
            {
                if (request.SocketIndex >= openCount || request.SocketIndex >= 5)
                    return false;

                var emblemType = ItemMetadataResolver.ResolveEmblemSocketType(request.EmblemItemTemplateId);
                if (!CanAttachEmblemToJewelSocket(socketType, emblemType))
                {
                    FileLogger.Log($"  [EmblemAttach] REJECT equipped: socketType=0x{socketType:X2} emblemType=0x{emblemType:X2} emblem=0x{request.EmblemItemTemplateId:X8}");
                    return false;
                }

                var emblem = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Main, request.EmblemSlot);
                if (emblem == null || emblem.ItemTemplateId != request.EmblemItemTemplateId || emblem.StackCount <= 0)
                    return false;

                WriteEmblemToEquippedFields(ref fields.Emblem, request.SocketIndex, request.EmblemItemTemplateId);

                var remaining = Math.Max(0, emblem.StackCount - 1);
                if (remaining > 0)
                    _db.UpdateStackCount(connection, transaction, emblem.ItemUid, remaining);
                else
                {
                    _db.DeleteItem(connection, transaction, emblem.ItemUid);
                    DeleteSortItemLock(connection, transaction, emblem.ListType, emblem.SlotIndex);
                }

                _auditLogger.WriteDeleteAuditLog(connection, transaction, _context.CharacterId, emblem, 1);
                consumed.Add(new InventoryMutationResult
                {
                    ListType = emblem.ListType,
                    SlotIndex = emblem.SlotIndex,
                    ItemTemplateId = emblem.ItemTemplateId,
                    RemainingStackCount = remaining,
                    InstanceValue = remaining,
                    Durability = emblem.Durability,
                    RequestedCount = 1,
                    AppliedCount = 1,
                });
            }

            entry.Raw = MakeEquipListCodec.BuildEntryFromDisplayFields(targetSlotIndex, targetItemTemplateId, fields);
            UpdateEquippedEntryRaw(connection, transaction, _context.CharacterId, targetSlotIndex, targetItemTemplateId, entry.Raw);
            FileLogger.Log($"  [EmblemAttach] equipped OK slot={targetSlotIndex} item=0x{targetItemTemplateId:X8} emblems={emblems.Count}");
            transaction.Commit();

            result = new EquipmentEmblemMutationResult
            {
                TargetEquipped = true,
            };
            result.ConsumedEmblems.AddRange(consumed);
            return true;
        }

        public bool TryOpenAvatarSocket(short targetSlotIndex, int targetItemTemplateId, short materialSlotIndex, out AvatarSocketMutationResult result)
        {
            result = null;
            if (targetItemTemplateId <= 0)
                return false;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var target = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Avatar, targetSlotIndex);
                if (target == null || target.ItemKind != "avatar" || target.ItemTemplateId != targetItemTemplateId)
                    return false;

                var avatar = _db.LoadAvatarItem(connection, transaction, _context.CharacterId, targetSlotIndex);
                if (avatar == null)
                    return false;

                avatar.Reserved2 = NormalizeBytes(avatar.Reserved2, 30);
                var expectedSocketTypes = ItemMetadataResolver.ResolveAvatarSocketTypes(targetItemTemplateId);
                var currentOpenCount = CountOpenAvatarSockets(avatar);
                if (currentOpenCount > 0)
                {
                    if (!AvatarSocketLayoutMatches(avatar.Reserved2, expectedSocketTypes))
                    {
                        avatar.Reserved2 = BuildAvatarSocketData(expectedSocketTypes);
                        FileLogger.Log($"  [AvatarSocket] repaired socket layout item=0x{targetItemTemplateId:X8} count={Math.Min(5, expectedSocketTypes != null ? expectedSocketTypes.Count : 0)}");
                    }

                    _db.UpdateAvatarExtraJson(connection, transaction, target.ItemUid, avatar);
                    _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "repair_avatar_socket", target, target.ListType, target.SlotIndex, 0);
                    transaction.Commit();

                    result = new AvatarSocketMutationResult
                    {
                        TargetItem = avatar,
                        MaterialConsumed = false,
                    };
                    return true;
                }

                var material = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Main, materialSlotIndex);
                if (material == null || material.StackCount <= 0)
                    return false;

                if (!TrySetAvatarSocketOpenFields(avatar, expectedSocketTypes))
                {
                    FileLogger.Log($"  [AvatarSocket] REJECT: avatar item=0x{targetItemTemplateId:X8} has no socket definition in [avatar type select]");
                    return false;
                }

                _db.UpdateAvatarExtraJson(connection, transaction, target.ItemUid, avatar);

                var remaining = Math.Max(0, material.StackCount - 1);
                if (remaining > 0)
                    _db.UpdateStackCount(connection, transaction, material.ItemUid, remaining);
                else
                {
                    _db.DeleteItem(connection, transaction, material.ItemUid);
                    DeleteSortItemLock(connection, transaction, material.ListType, material.SlotIndex);
                }

                _auditLogger.WriteDeleteAuditLog(connection, transaction, _context.CharacterId, material, 1);
                _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "open_avatar_socket", target, target.ListType, target.SlotIndex, 0);
                transaction.Commit();

                result = new AvatarSocketMutationResult
                {
                    TargetItem = avatar,
                    MaterialItem = new InventoryMutationResult
                    {
                        ListType = material.ListType,
                        SlotIndex = material.SlotIndex,
                        ItemTemplateId = material.ItemTemplateId,
                        RemainingStackCount = remaining,
                        InstanceValue = remaining,
                        Durability = material.Durability,
                        RequestedCount = 1,
                        AppliedCount = 1,
                    },
                    MaterialConsumed = true,
                };
                return true;
            }
        }

        public bool TrySetAvatarEmblems(short targetSlotIndex, int targetItemTemplateId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out AvatarEmblemMutationResult result)
        {
            result = null;
            if (emblems == null || emblems.Count == 0)
                return false;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var target = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Avatar, targetSlotIndex);
                if (target == null || target.ItemKind != "avatar" || target.ItemTemplateId != targetItemTemplateId)
                    return false;

                var avatar = _db.LoadAvatarItem(connection, transaction, _context.CharacterId, targetSlotIndex);
                if (avatar == null)
                    return false;

                avatar.Reserved2 = NormalizeBytes(avatar.Reserved2, 30);
                var expectedSocketTypes = ItemMetadataResolver.ResolveAvatarSocketTypes(targetItemTemplateId);
                var openCount = CountOpenAvatarSockets(avatar);
                if (openCount <= 0)
                    return false;

                var consumed = new List<InventoryMutationResult>();
                foreach (var request in emblems)
                {
                    if (request.SocketIndex >= openCount || request.SocketIndex >= 5)
                        return false;

                    var socketType = GetAvatarSocketType(avatar, request.SocketIndex);
                    var emblemType = ItemMetadataResolver.ResolveEmblemSocketType(request.EmblemItemTemplateId);
                    if (socketType != 0 && emblemType != 0 && socketType != 0x10 && socketType != 0xEF && socketType != emblemType)
                    {
                        FileLogger.Log($"  [AvatarEmblemAttach] REJECT: socketType=0x{socketType:X2} emblemType=0x{emblemType:X2} emblem=0x{request.EmblemItemTemplateId:X8}");
                        return false;
                    }

                    var emblem = _db.LoadItemRecord(connection, transaction, _context.CharacterId, InventoryListType.Main, request.EmblemSlot);
                    if (emblem == null || emblem.ItemTemplateId != request.EmblemItemTemplateId || emblem.StackCount <= 0)
                        return false;

                    WriteEmblemToAvatarSocket(avatar.Reserved2, request.SocketIndex, request.EmblemItemTemplateId);

                    var remaining = Math.Max(0, emblem.StackCount - 1);
                    if (remaining > 0)
                        _db.UpdateStackCount(connection, transaction, emblem.ItemUid, remaining);
                    else
                    {
                        _db.DeleteItem(connection, transaction, emblem.ItemUid);
                        DeleteSortItemLock(connection, transaction, emblem.ListType, emblem.SlotIndex);
                    }

                    _auditLogger.WriteDeleteAuditLog(connection, transaction, _context.CharacterId, emblem, 1);
                    consumed.Add(new InventoryMutationResult
                    {
                        ListType = emblem.ListType,
                        SlotIndex = emblem.SlotIndex,
                        ItemTemplateId = emblem.ItemTemplateId,
                        RemainingStackCount = remaining,
                        InstanceValue = remaining,
                        Durability = emblem.Durability,
                        RequestedCount = 1,
                        AppliedCount = 1,
                    });
                }

                _db.UpdateAvatarExtraJson(connection, transaction, target.ItemUid, avatar);
                _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "set_avatar_emblems", target, target.ListType, target.SlotIndex, emblems.Count);
                transaction.Commit();

                result = new AvatarEmblemMutationResult
                {
                    TargetItem = avatar,
                };
                result.ConsumedEmblems.AddRange(consumed);
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

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                bool srcIsCargo = request.SourceListType == InventoryListType.AccountCargo;
                bool dstIsCargo = request.DestinationListType == InventoryListType.AccountCargo;

                var source = srcIsCargo
                    ? _db.LoadAccountCargoItemRecord(connection, transaction, _context.AccountId, request.SourceSlotIndex)
                    : _db.LoadItemRecord(connection, transaction, _context.CharacterId, dbSrcList, request.SourceSlotIndex);
                var destination = dstIsCargo
                    ? _db.LoadAccountCargoItemRecord(connection, transaction, _context.AccountId, request.DestinationSlotIndex)
                    : _db.LoadItemRecord(connection, transaction, _context.CharacterId, dbDstList, request.DestinationSlotIndex);

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

                if (srcIsCargo != dstIsCargo && source != null && destination == null)
                {
                    if (srcIsCargo)
                        _db.MoveItemFromAccountCargo(connection, transaction, _context.CharacterId, source, dbDstList, request.DestinationSlotIndex);
                    else
                        _db.MoveItemToAccountCargo(connection, transaction, _context.AccountId, source, request.DestinationSlotIndex);
                    transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount);
                    FileLogger.Log($"  [MoveItem] CARGO_MOVE: {(srcIsCargo ? "cargo→bag" : "bag→cargo")} item=0x{source.ItemTemplateId:X8}");
                    return true;
                }

                if (source == null)
                {
                    if (destination != null)
                    {
                        FileLogger.Log($"  [MoveItem] MOVE(empty-src): dst uid={destination.ItemUid} tmpl=0x{destination.ItemTemplateId:X8} → ({dbSrcList},{request.SourceSlotIndex})");
                        _db.UpdateItemPosition(connection, transaction, destination.ItemUid, dbSrcList, request.SourceSlotIndex);
                        MoveSortItemLock(connection, transaction, dbDstList, request.DestinationSlotIndex, dbSrcList, request.SourceSlotIndex);
                        _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "move_itemspace", destination, dbSrcList, request.SourceSlotIndex, request.MoveCount);
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

                if (CanStack(source, destination) && moveCount > 0)
                {
                    _db.UpdateStackCount(connection, transaction, destination.ItemUid, destination.StackCount + moveCount);

                    if (moveCount == source.StackCount)
                    {
                        _db.DeleteItem(connection, transaction, source.ItemUid);
                        DeleteSortItemLock(connection, transaction, dbSrcList, request.SourceSlotIndex);
                    }
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
                    MoveSortItemLock(connection, transaction, dbSrcList, request.SourceSlotIndex, dbDstList, request.DestinationSlotIndex);
                    _auditLogger.WriteAuditLog(connection, transaction, _context.CharacterId, "move_itemspace", source, dbDstList, request.DestinationSlotIndex, moveCount);
                    transaction.Commit();
                    result = CreateMoveResult(request, request.MoveCount);
                    return true;
                }

                if (!CanSwap(source, destination))
                    return false;

                FileLogger.Log($"  [MoveItem] SWAP: src uid={source.ItemUid} kind={source.ItemKind} tmpl=0x{source.ItemTemplateId:X8} ↔ dst uid={destination.ItemUid} kind={destination.ItemKind} tmpl=0x{destination.ItemTemplateId:X8}");
                _db.SwapItems(connection, transaction, source, destination);
                SwapSortItemLocks(connection, transaction, source.ListType, source.SlotIndex, destination.ListType, destination.SlotIndex);
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
                var lockedSlots = LoadSortItemLockSlots(connection, transaction, listType, start, end);
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

        public bool TryToggleSortItemLock(InventoryListType listType, short slotIndex, out SortItemLockEntry entry)
        {
            entry = null;
            if (!IsSupportedSortListType(listType))
                return false;

            var dbListType = MapToDbListType(listType);
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var item = _db.LoadItemRecord(connection, transaction, _context.CharacterId, dbListType, slotIndex);
                if (item == null)
                    return false;
                if (!CanApplySortItemLock(dbListType, slotIndex))
                {
                    DeleteSortItemLock(connection, transaction, dbListType, slotIndex);
                    transaction.Commit();
                    return false;
                }
                if (LoadSortItemLock(connection, transaction, dbListType, slotIndex).HasValue)
                {
                    DeleteSortItemLock(connection, transaction, dbListType, slotIndex);
                    transaction.Commit();
                    entry = new SortItemLockEntry { ListType = dbListType, SlotIndex = slotIndex, State = 0 };
                    return true;
                }

                UpsertSortItemLock(connection, transaction, dbListType, slotIndex, 1);
                transaction.Commit();
                entry = new SortItemLockEntry { ListType = dbListType, SlotIndex = slotIndex, State = 1 };
                return true;
            }
        }

        public bool TryUnlockSortItemLock(InventoryListType listType, short slotIndex)
        {
            if (!IsSupportedSortListType(listType))
                return false;

            var dbListType = MapToDbListType(listType);
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var item = _db.LoadItemRecord(connection, transaction, _context.CharacterId, dbListType, slotIndex);
                if (item != null && !CanApplySortItemLock(dbListType, slotIndex))
                {
                    DeleteSortItemLock(connection, transaction, dbListType, slotIndex);
                    transaction.Commit();
                    return false;
                }

                DeleteSortItemLock(connection, transaction, dbListType, slotIndex);
                transaction.Commit();
                return true;
            }
        }

        public IReadOnlyList<SortItemLockEntry> LoadSortItemLocks()
        {
            using (var connection = _context.OpenConnection())
            {
                var entries = new List<SortItemLockEntry>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT list_type, slot_index, state
FROM character_sort_item_locks
WHERE character_id = @cid
ORDER BY sort_order;";
                    cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
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

        public IReadOnlyList<SortItemLockEntry> LoadSortItemLocks(InventoryListType listType)
        {
            var dbListType = MapToDbListType(listType);
            using (var connection = _context.OpenConnection())
            {
                var entries = new List<SortItemLockEntry>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT list_type, slot_index, state
FROM character_sort_item_locks
WHERE character_id = @cid AND list_type = @lt
ORDER BY sort_order;";
                    cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
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

        public CommonInventoryItem LoadCommonItemForRefresh(InventoryListType listType, short slotIndex)
        {
            var dbListType = MapToDbListType(listType);
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
                return _db.LoadCommonItem(connection, transaction, _context.CharacterId, dbListType, slotIndex);
        }

        private void UpsertSortItemLock(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slotIndex, byte state)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
INSERT INTO character_sort_item_locks (character_id, sort_order, list_type, slot_index, state)
VALUES (@cid, COALESCE((SELECT MAX(sort_order) + 1 FROM character_sort_item_locks WHERE character_id = @cid), 0), @lt, @slot, @state)
ON CONFLICT(character_id, list_type, slot_index)
DO UPDATE SET state = excluded.state;";
                cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                cmd.Parameters.AddWithValue("@lt", (int)listType);
                cmd.Parameters.AddWithValue("@slot", (int)slotIndex);
                cmd.Parameters.AddWithValue("@state", (int)state);
                cmd.ExecuteNonQuery();
            }
        }

        private void DeleteSortItemLock(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slotIndex)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
DELETE FROM character_sort_item_locks
WHERE character_id = @cid AND list_type = @lt AND slot_index = @slot;";
                cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                cmd.Parameters.AddWithValue("@lt", (int)listType);
                cmd.Parameters.AddWithValue("@slot", (int)slotIndex);
                cmd.ExecuteNonQuery();
            }
        }

        private void MoveSortItemLock(SqliteConnection connection, SqliteTransaction transaction, InventoryListType fromListType, short fromSlotIndex, InventoryListType toListType, short toSlotIndex)
        {
            if (LoadSortItemLock(connection, transaction, toListType, toSlotIndex).HasValue)
                DeleteSortItemLock(connection, transaction, toListType, toSlotIndex);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE character_sort_item_locks
SET list_type = @toLt, slot_index = @toSlot
WHERE character_id = @cid AND list_type = @fromLt AND slot_index = @fromSlot;";
                cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                cmd.Parameters.AddWithValue("@fromLt", (int)fromListType);
                cmd.Parameters.AddWithValue("@fromSlot", (int)fromSlotIndex);
                cmd.Parameters.AddWithValue("@toLt", (int)toListType);
                cmd.Parameters.AddWithValue("@toSlot", (int)toSlotIndex);
                cmd.ExecuteNonQuery();
            }
        }

        private void SwapSortItemLocks(SqliteConnection connection, SqliteTransaction transaction, InventoryListType firstListType, short firstSlotIndex, InventoryListType secondListType, short secondSlotIndex)
        {
            var first = LoadSortItemLock(connection, transaction, firstListType, firstSlotIndex);
            var second = LoadSortItemLock(connection, transaction, secondListType, secondSlotIndex);
            DeleteSortItemLock(connection, transaction, firstListType, firstSlotIndex);
            DeleteSortItemLock(connection, transaction, secondListType, secondSlotIndex);

            if (first.HasValue)
                UpsertSortItemLock(connection, transaction, secondListType, secondSlotIndex, first.Value);
            if (second.HasValue)
                UpsertSortItemLock(connection, transaction, firstListType, firstSlotIndex, second.Value);
        }

        private byte? LoadSortItemLock(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short slotIndex)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT state
FROM character_sort_item_locks
WHERE character_id = @cid AND list_type = @lt AND slot_index = @slot
LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                cmd.Parameters.AddWithValue("@lt", (int)listType);
                cmd.Parameters.AddWithValue("@slot", (int)slotIndex);
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return null;
                return Convert.ToByte(value, CultureInfo.InvariantCulture);
            }
        }

        private HashSet<short> LoadSortItemLockSlots(SqliteConnection connection, SqliteTransaction transaction, InventoryListType listType, short start, short end)
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
                cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
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
                    _db.InsertAccountCargoItem(connection, transaction, _context.AccountId, item);

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
                || listType == InventoryListType.Pet
                || listType == InventoryListType.AccountCargo;
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

        private static void SetEquipmentSocketOpenFields(CommonInventoryItem item)
        {
            if (item == null)
                return;

            item.TailData2F = NormalizeBytes(item.TailData2F, 37);
            item.JewelSocket = BuildJewelSocketData(item.ItemTemplateId);
            EnsureVisibleSocketCount(item, 2);
        }

        private static bool TrySetAvatarSocketOpenFields(AvatarInventoryItem item, IReadOnlyList<byte> socketTypes)
        {
            if (item == null)
                return false;

            if (socketTypes == null || socketTypes.Count == 0)
                return false;

            item.Reserved2 = BuildAvatarSocketData(socketTypes);
            return true;
        }

        private static byte[] BuildAvatarSocketData(IReadOnlyList<byte> socketTypes)
        {
            var data = new byte[30];
            if (socketTypes == null)
                return data;

            var count = Math.Min(5, socketTypes.Count);
            for (var i = 0; i < count; i++)
            {
                var offset = i * 6;
                data[offset] = 0;
                data[offset + 1] = socketTypes[i];
                data[offset + 2] = socketTypes[i] == 0xEF ? (byte)0xFF : (byte)0;
            }
            return data;
        }

        private static byte[] BuildJewelSocketData(int itemTemplateId)
        {
            var data = new byte[30];
            var socketType = ResolveJewelSocketType(itemTemplateId);
            for (var i = 0; i < 2; i++)
            {
                var offset = i * 6;
                data[offset] = socketType;
                data[offset + 1] = 0;
            }
            return data;
        }

        private static byte ResolveJewelSocketType(int itemTemplateId)
        {
            var equipmentType = ItemMetadataResolver.ResolveEquipmentType(itemTemplateId);
            if (string.IsNullOrWhiteSpace(equipmentType))
                return 0x10;

            switch (equipmentType)
            {
                case "[coat]":
                case "[pants]":
                    return 0x04;
                case "[shoulder]":
                case "[amulet]":
                    return 0x02;
                case "[belt]":
                case "[waist]":
                case "[ring]":
                    return 0x01;
                case "[shoes]":
                case "[wrist]":
                    return 0x08;
                default:
                    return 0x10;
            }
        }

        private static int CountOpenJewelSockets(CommonInventoryItem item)
        {
            var data = item?.JewelSocket;
            if (data == null || data.Length < 8)
                return 0;

            var count = 0;
            for (var i = 0; i < 2; i++)
            {
                var offset = i * 6;
                if (offset < data.Length && data[offset] != 0)
                    count++;
            }
            return count;
        }

        private static int CountOpenAvatarSockets(AvatarInventoryItem item)
        {
            var data = item?.Reserved2;
            if (data == null || data.Length < 6)
                return 0;

            var count = 0;
            for (var i = 0; i < 5; i++)
            {
                var offset = i * 6;
                if (IsAvatarSocketOpen(data, offset))
                    count++;
            }
            return count;
        }

        private static byte GetJewelSocketType(CommonInventoryItem item, byte socketIndex)
        {
            var data = NormalizeBytes(item?.JewelSocket, 30);
            var offset = socketIndex * 6;
            if (data == null || offset >= data.Length)
                return 0;
            return data[offset];
        }

        private static bool CanAttachEmblemToJewelSocket(byte socketType, byte emblemType)
        {
            if (socketType == 0 || emblemType == 0)
                return true;

            return (socketType & emblemType) != 0;
        }

        private static byte GetAvatarSocketType(AvatarInventoryItem item, byte socketIndex)
        {
            if (item == null)
                return 0;

            var socketTypes = ItemMetadataResolver.ResolveAvatarSocketTypes(item.AvatarItemId);
            return socketTypes != null && socketIndex < socketTypes.Count ? socketTypes[socketIndex] : (byte)0;
        }

        private static bool IsAvatarSocketOpen(byte[] data, int offset)
        {
            return data != null
                && offset >= 0
                && offset + 5 < data.Length
                && data[offset] == 0
                && data[offset + 1] != 0
                && (data[offset + 1] != 0xEF || data[offset + 2] == 0xFF);
        }

        private static bool AvatarSocketLayoutMatches(byte[] data, IReadOnlyList<byte> socketTypes)
        {
            if (socketTypes == null || socketTypes.Count == 0)
                return false;

            data = NormalizeBytes(data, 30);
            var expectedCount = Math.Min(5, socketTypes.Count);
            for (var i = 0; i < expectedCount; i++)
            {
                var offset = i * 6;
                if (data[offset] != 0 || data[offset + 1] != socketTypes[i])
                    return false;

                if (socketTypes[i] == 0xEF && data[offset + 2] != 0xFF)
                    return false;
            }

            for (var i = expectedCount; i < 5; i++)
            {
                var offset = i * 6;
                if (IsAvatarSocketOpen(data, offset))
                    return false;
            }

            return true;
        }

        private static void WriteEmblemToTail(byte[] tailData2F, byte socketIndex, int emblemItemTemplateId)
        {
            if (tailData2F == null || tailData2F.Length < 37)
                return;

            tailData2F[0] = Math.Max(tailData2F[0], (byte)(socketIndex + 1));
            var offset = 1 + socketIndex * 4;
            if (offset + 4 <= tailData2F.Length)
                BitConverter.GetBytes(emblemItemTemplateId).CopyTo(tailData2F, offset);
        }

        private static void WriteEmblemToEquippedFields(ref byte[] emblemData, byte socketIndex, int emblemItemTemplateId)
        {
            var requiredLength = 1 + (socketIndex + 1) * 4;
            if (emblemData == null || emblemData.Length < requiredLength)
            {
                var resized = new byte[Math.Max(requiredLength, emblemData != null && emblemData.Length > 0 ? emblemData.Length : 1)];
                if (emblemData != null)
                    Buffer.BlockCopy(emblemData, 0, resized, 0, Math.Min(emblemData.Length, resized.Length));
                emblemData = resized;
            }

            emblemData[0] = Math.Max(emblemData[0], (byte)(socketIndex + 1));
            BitConverter.GetBytes(emblemItemTemplateId).CopyTo(emblemData, 1 + socketIndex * 4);
        }

        private static MakeEquipListCodec.Entry LoadEquippedEntry(SqliteConnection connection, SqliteTransaction transaction, int characterId, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT slot, item_id, expire_time, raw_entry
FROM character_equipped_entries
WHERE character_id = @cid AND slot = @slot
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@slot", (int)slotIndex);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new MakeEquipListCodec.Entry
                    {
                        Slot = reader.GetInt32(0),
                        ItemId = reader.GetInt32(1),
                        ExpireTime = reader.GetInt32(2),
                        Raw = (byte[])reader.GetValue(3),
                    };
                }
            }
        }

        private static void UpdateEquippedEntryRaw(SqliteConnection connection, SqliteTransaction transaction, int characterId, short slotIndex, int itemTemplateId, byte[] rawEntry)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_equipped_entries
SET raw_entry = @raw
WHERE character_id = @cid AND slot = @slot AND item_id = @itemId;";
                command.Parameters.AddWithValue("@raw", rawEntry);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@slot", (int)slotIndex);
                command.Parameters.AddWithValue("@itemId", itemTemplateId);
                command.ExecuteNonQuery();
            }
        }

        private static void EnsureVisibleSocketCount(CommonInventoryItem item, int openCount)
        {
            if (item == null || openCount <= 0)
                return;

            item.TailData2F = NormalizeBytes(item.TailData2F, 37);
            item.TailData2F[0] = (byte)Math.Max(item.TailData2F[0], Math.Min(openCount, 2));
            for (var i = 0; i < openCount && i < 2; i++)
            {
                var offset = 1 + i * 4;
                if (offset + 4 <= item.TailData2F.Length && BitConverter.ToInt32(item.TailData2F, offset) == 0)
                    BitConverter.GetBytes(-1).CopyTo(item.TailData2F, offset);
            }
        }

        private static void NormalizeEquipmentSocketLayout(CommonInventoryItem item)
        {
            if (item == null)
                return;

            item.JewelSocket = NormalizeBytes(item.JewelSocket, 30);
            var changed = false;
            for (var i = 0; i < 2; i++)
            {
                var offset = i * 6;
                if (item.JewelSocket[offset] == 0 && IsKnownJewelSocketType(item.JewelSocket[offset + 1]))
                {
                    item.JewelSocket[offset] = item.JewelSocket[offset + 1];
                    item.JewelSocket[offset + 1] = 0;
                    changed = true;
                }
                else if (item.JewelSocket[offset] == 0x02 && IsKnownJewelSocketType(item.JewelSocket[offset + 1]))
                {
                    item.JewelSocket[offset] = item.JewelSocket[offset + 1];
                    item.JewelSocket[offset + 1] = 0;
                    changed = true;
                }
            }

            if (changed)
                EnsureVisibleSocketCount(item, CountOpenJewelSockets(item));
        }

        private static bool RepairJewelSocketTypes(CommonInventoryItem item, int itemTemplateId)
        {
            if (item == null)
                return false;

            item.JewelSocket = NormalizeBytes(item.JewelSocket, 30);
            var expectedType = ResolveJewelSocketType(itemTemplateId);
            var changed = false;
            for (var i = 0; i < 2; i++)
            {
                var offset = i * 6;
                if (offset >= item.JewelSocket.Length || item.JewelSocket[offset] == 0)
                    continue;

                if (item.JewelSocket[offset] != expectedType)
                {
                    item.JewelSocket[offset] = expectedType;
                    item.JewelSocket[offset + 1] = 0;
                    changed = true;
                }
            }

            return changed;
        }

        private static bool IsKnownJewelSocketType(byte socketType)
        {
            return socketType == 0x01
                || socketType == 0x02
                || socketType == 0x04
                || socketType == 0x08
                || socketType == 0x10;
        }

        private static void WriteEmblemToJewelSocket(byte[] jewelSocket, byte socketIndex, int emblemItemTemplateId)
        {
            if (jewelSocket == null || jewelSocket.Length < 30)
                return;

            var offset = socketIndex * 6 + 2;
            if (offset + 4 <= jewelSocket.Length)
                BitConverter.GetBytes(emblemItemTemplateId).CopyTo(jewelSocket, offset);
        }

        private static void WriteEmblemToAvatarSocket(byte[] reserved2, byte socketIndex, int emblemItemTemplateId)
        {
            if (reserved2 == null || reserved2.Length < 30)
                return;

            var offset = socketIndex * 6 + 3;
            if (offset + 3 <= reserved2.Length)
            {
                var bytes = BitConverter.GetBytes(emblemItemTemplateId);
                Buffer.BlockCopy(bytes, 0, reserved2, offset, 3);
            }
        }

        private static byte[] NormalizeBytes(byte[] source, int expectedLength)
        {
            var buffer = new byte[expectedLength];
            if (source != null && source.Length > 0)
                Buffer.BlockCopy(source, 0, buffer, 0, Math.Min(source.Length, expectedLength));
            return buffer;
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
