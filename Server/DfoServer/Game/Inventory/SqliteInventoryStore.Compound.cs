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
    public sealed partial class SqliteInventoryStore
    {
        // 合并装扮(时装合成): 扣掉 slot1/slot2 两件旧时装 + 1 个消耗品(合成器), 在时装栏第一个
        // 空位插入新时装。新时装itemId由 resolveNewItemId(oldItemId1, oldItemId2, consumeMaterialId)
        // 回调计算(在事务内、读到三个真实item之后才调用, 保证概率判定用的是事务内的真实数据)。
        // 返回新时装所在 slot (newSlotOut)。一个事务内完成, 失败回滚。
        public bool TryCompoundAvatar(int characterId, int accountId, short slot1, short slot2, short consumeSlot,
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

            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var item1 = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Avatar, slot1);
                    var item2 = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Avatar, slot2);
                if (item1 == null || item2 == null)
                {
                    FileLogger.Log($"  [CompoundAvatar] REJECT: missing avatar item at slot1={slot1}(found={item1!=null}) slot2={slot2}(found={item2!=null})");
                    return false;
                }

                if (IsEquipmentItemLocked(connection, transaction, characterId, item1)
                    || IsEquipmentItemLocked(connection, transaction, characterId, item2))
                {
                    FileLogger.Log($"  [CompoundAvatar] REJECT: locked avatar slot1={slot1} lock1={item1.EquipmentLockId} slot2={slot2} lock2={item2.EquipmentLockId}");
                    return false;
                }

                var consumeItem = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, consumeSlot);
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

                var avatarExpansion = GetListParam(_equipStore.LoadContainerState(connection, transaction, characterId, accountId), InventoryListType.Avatar);
                var avatarCapacity = 105 + avatarExpansion;

                foreach (var newItemId in newItemIds)
                {
                    var newSlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Avatar, 0, avatarCapacity - 1);
                    if (newSlot < 0)
                    {
                        FileLogger.Log($"  [CompoundAvatar] REJECT: no empty avatar slot (capacity={avatarCapacity})");
                        return false;
                    }

                    _db.InsertCharacterItem(
                        connection,
                        transaction,
                        characterId,
                        InventoryListType.Avatar,
                        (short)newSlot,
                        newItemId,
                        "avatar",
                        0,
                        0,
                        0,
                        0,
                        newOption,
                        0,
                        DefaultAvatarUnknownFixed30,
                        0,
                        CreateDefaultAvatarExtraJson(newItemId));
                    newSlotsOut.Add(newSlot);
                }

                    transaction.Commit();
                    FileLogger.Log($"  [CompoundAvatar] OK: deleted slot{slot1}(item {oldItemId1}) + slot{slot2}(item {oldItemId2}) + " +
                                   $"1x slot{consumeSlot}(template {consumedItemTemplateId}, remain {consumedItemRemainingCount}), " +
                                   $"added items [{string.Join(",", newItemIds)}] at slots [{string.Join(",", newSlotsOut)}]");
                    return true;
                }
            }
        }

        // 8件高级装扮 -> 100%合成指定稀有装扮(克隆装扮合成器, 如"旷古天娇"系列)。
        // 消耗品按请求body里携带的Main列表槛位号精确定位(实测两组不同槛位数据交叉验证得到该字段)。
        // resolveNewItemId: 输入消耗品item_template_id, 由调用方(AbsoluteBindCubeService)按该合成器
        // 的[action type]配置 + 角色职业查表校验/纠正客户端请求的目标itemId; 返回负数表示校验失败。
        public bool TryCompoundAvatarSet(int characterId, int accountId, short[] consumeSlots, int[] expectedItemIds, Func<int, int> resolveNewItemId, byte newOption,
                short consumeStackableSlot,
                out int newSlot, out List<int> oldItemIds, out int newItemId, out int consumedItemTemplateId, out int consumedItemRemainingCount)
        {
            newSlot = -1;
            oldItemIds = new List<int>();
            newItemId = 0;
            consumedItemTemplateId = 0;
            consumedItemRemainingCount = 0;

            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var items = new ItemRecord[consumeSlots.Length];
                    for (int i = 0; i < consumeSlots.Length; i++)
                    {
                        items[i] = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Avatar, consumeSlots[i]);
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

                    if (IsEquipmentItemLocked(connection, transaction, characterId, items[i]))
                    {
                        FileLogger.Log($"  [CompoundAvatarSet] REJECT: locked avatar slot={consumeSlots[i]} lockId={items[i].EquipmentLockId}");
                        return false;
                    }
                }

                var consumeItem = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, consumeStackableSlot);
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
                var avatarExpansion = GetListParam(_equipStore.LoadContainerState(connection, transaction, characterId, accountId), InventoryListType.Avatar);
                var avatarCapacity = 105 + avatarExpansion;
                var emptySlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Avatar, 0, avatarCapacity - 1);
                if (emptySlot < 0)
                {
                    FileLogger.Log($"  [CompoundAvatarSet] REJECT: no empty avatar slot (capacity={avatarCapacity})");
                    return false;
                }

                _db.InsertCharacterItem(
                    connection,
                    transaction,
                    characterId,
                    InventoryListType.Avatar,
                    (short)emptySlot,
                    newItemId,
                    "avatar",
                    0,
                    0,
                    0,
                    0,
                    newOption,
                    0,
                    DefaultAvatarUnknownFixed30,
                    0,
                    CreateDefaultAvatarExtraJson(newItemId));

                    transaction.Commit();
                    newSlot = emptySlot;
                    FileLogger.Log($"  [CompoundAvatarSet] OK: consumed {items.Length} avatar items + 1x slot {consumeStackableSlot}(template {consumeItem.ItemTemplateId}), added item {newItemId} at slot {newSlot}");
                    return true;
                }
            }
        }
    }
}
