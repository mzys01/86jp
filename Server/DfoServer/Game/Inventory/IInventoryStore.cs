using DfoServer.Game.ExpertJob;
using DfoServer.Game.ItemUpgrade;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public interface IInventoryStore
    {
        IDisposable BeginScope(int characterId, int accountId);

        int CountItem(int itemTemplateId);

        bool TryRemoveItemByTemplateId(int itemTemplateId, out short slotIndex, out InventoryMutationResult result);

        void RunMigrations();

        void EnsureDatabase(CharacterItemListSnapshot seedSnapshot);

        void EnsureContainerState(int characterId);

        CharacterItemListSnapshot LoadCharacterItemListSnapshot();

        int DeleteExpiredRentalEquipment();

        bool TryDeleteItem(InventoryListType listType, short slotIndex, short deleteCount, out InventoryMutationResult result);

        bool TryOpenAvatarPackage(AvatarPackageOpenRequest request, out AvatarPackageOpenResult result);

        bool TryOpenSelectablePackage(SelectablePackageOpenRequest request, out SelectablePackageOpenResult result);

        bool TryUseBoosterItem(BoosterUseRequest request, out BoosterUseResult result);

        bool TryOpenPackage0207(short slotIndex, IReadOnlyList<int> selectedItemTemplateIds, out BoosterUseResult result);

        bool TryBuyItem(int itemTemplateId, int buyCount, out InventoryMutationResult result);

        bool TryPickupRentalWeapon(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int itemTemplateId,
            int expireTime,
            out short assignedSlot,
            out int instanceValue);

        bool TryPickupItem(int itemTemplateId, int stackCount, out short assignedSlot, out int newStackCount);

        bool TrySellItem(InventoryListType listType, short slotIndex, short sellCount, out InventoryMutationResult result);

        bool TryDisjointItem(DisjointItemRequest request, out DisjointItemResult result);

        bool TryEnchantByBead(EnchantByBeadCommand command, out EnchantByBeadResult result);

        bool TryUpgradeItem(ItemUpgradeCommand command, out ItemUpgradeResult result);

        bool TryOpenEquipmentSocket(short targetSlotIndex, int targetItemTemplateId, short materialSlotIndex, out EquipmentSocketMutationResult result);

        bool TrySetEquipmentEmblems(short targetSlotIndex, int targetItemTemplateId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out EquipmentEmblemMutationResult result);

        bool TryOpenAvatarSocket(short targetSlotIndex, int targetItemTemplateId, short materialSlotIndex, out AvatarSocketMutationResult result);

        bool TrySetAvatarEmblems(short targetSlotIndex, int targetItemTemplateId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out AvatarEmblemMutationResult result);

        bool TryCompoundAvatar(short slot1, short slot2, short consumeSlot,
            Func<int, int, int, List<int>> resolveNewItemIds, byte newOption,
            out List<int> newSlotsOut, out int oldItemId1, out int oldItemId2, out List<int> newItemIdsOut,
            out int consumedItemTemplateId, out int consumedItemRemainingCount);

        bool TryCompoundAvatarSet(short[] consumeSlots, int[] expectedItemIds, Func<int, int> resolveNewItemId, byte newOption,
            short consumeStackableSlot,
            out int newSlot, out List<int> oldItemIds, out int newItemId, out int consumedItemTemplateId, out int consumedItemRemainingCount);

        bool TryMoveItem(InventoryMoveRequest request, out InventoryMoveResult result);

        bool TrySortItems(int characterId, InventoryListType listType, byte category);

        bool TryToggleSortItemLock(InventoryListType listType, short slotIndex, out SortItemLockEntry entry);

        bool TryUnlockSortItemLock(InventoryListType listType, short slotIndex);

        IReadOnlyList<SortItemLockEntry> LoadSortItemLocks();

        IReadOnlyList<SortItemLockEntry> LoadSortItemLocks(InventoryListType listType);

        CommonInventoryItem LoadCommonItemForRefresh(InventoryListType listType, short slotIndex);

        void SeedNewCharacterEquipment((short slot, int itemId)[] equipment);

        // 商城购买: paymentMode=0 走点券瀑布扣减, paymentMode=1 走装扮兑换券抵扣(不扣Cera)。
        bool TryBuyCeraShopItem(int productId, int buyCount, int paymentMode, byte attributeValue, out InventoryMutationResult result);
    }
}
