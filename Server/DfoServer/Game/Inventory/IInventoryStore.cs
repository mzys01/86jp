using DfoServer.Game.ExpertJob;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public interface IInventoryStore
    {
        int CountItem(int characterId, int itemTemplateId);

        void EnsureDatabase(int characterId, int accountId, CharacterItemListSnapshot seedSnapshot);

        void EnsureContainerState(int characterId, int accountId);

        CharacterItemListSnapshot LoadCharacterItemListSnapshot(int characterId, int accountId);

        int DeleteExpiredRentalEquipment(int characterId, int accountId);

        RentalInfoSnapshot RebuildRentalInfoFromInventory(
            int characterId,
            int accountId,
            RentalInfoSnapshot storedRentalInfo);

        bool TryDeleteItem(int characterId, int accountId, InventoryListType listType, short slotIndex, short deleteCount, out InventoryMutationResult result);

        bool TryOpenAvatarPackage(int characterId, int accountId, AvatarPackageOpenRequest request, out AvatarPackageOpenResult result);

        bool TryOpenSelectablePackage(int characterId, int accountId, SelectablePackageOpenRequest request, out SelectablePackageOpenResult result);

        bool TryUseBoosterItem(int characterId, int accountId, BoosterUseRequest request, out BoosterUseResult result);

        bool CanUseBoosterItem(int characterId, int accountId, BoosterUseRequest request);

        bool TryOpenPackage0207(int characterId, int accountId, short slotIndex, IReadOnlyList<int> selectedItemTemplateIds, out BoosterUseResult result);

        bool TryHatchCreatureEgg(int characterId, InventoryListType listType, short slotIndex, int expectedItemTemplateId, out CreatureHatchResult result);

        bool TrySealPetCreature(int characterId, int accountId, PetCreatureSealRequest request, out PetCreatureSealResult result);

        bool TryOpenSealedPetCreatureCapsule(int characterId, int accountId, short slotIndex, out BoosterUseResult result);

        bool TryRenameEquippedPetCreature(int characterId, int accountId, PetCreatureRenameRequest request, out PetCreatureRenameResult result);

        bool TryBuyItem(int characterId, int accountId, int itemTemplateId, int buyCount, out InventoryMutationResult result);

        bool TryBuySecretShopItem(
            int characterId,
            int accountId,
            int itemTemplateId,
            int itemCount,
            int goldCost,
            int requiredItemId,
            int requiredItemCount,
            out InventoryMutationResult result);

        bool TryPickupRentalWeapon(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            int itemTemplateId,
            int expireTime,
            out short assignedSlot,
            out int instanceValue);

        bool TryPickupItem(int characterId, int accountId, int itemTemplateId, int stackCount, out short assignedSlot);

        bool TryPickupItem(int characterId, int accountId, int itemTemplateId, int stackCount, out short assignedSlot, out int newStackCount,
            Action<SqliteConnection, SqliteTransaction> alsoInSameTransaction = null);

        bool TryRemoveItemByTemplateId(int characterId, int accountId, int itemTemplateId, out short slotIndex, out InventoryMutationResult result,
            Action<SqliteConnection, SqliteTransaction> alsoInSameTransaction = null);

        bool TryRepairEquipment(int characterId, int accountId, InventoryListType listType, short slotIndex, bool quickRepair, bool freeRepair, out RepairEquipmentResult result);

        bool TryDepositCargoGold(int characterId, int accountId, int amount, out int newCharGold, out int newCargoGold);

        bool TryWithdrawCargoGold(int characterId, int accountId, int amount, out int newCharGold, out int newCargoGold);

        bool TryCreateAccountCargo(int characterId, int accountId, out InventoryMutationResult costResult, out byte errorCode);

        bool TryUpgradeAccountCargo(int characterId, int accountId, out InventoryMutationResult costResult, out byte errorCode);

        bool TryUpgradePersonalCargo(int characterId, int accountId, out ushort newListParam16, out byte errorCode);

        bool TryUsePersonalCargoUpgradeTicket(
            int characterId,
            int accountId,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            out PersonalCargoUpgradeTicketResult result);

        bool TryUseEquipmentEffectRune(int characterId, int accountId, EquipmentEffectRuneUseRequest request, out EquipmentEffectRuneUseResult result);

        bool TryUseAccountCargoUpgradeTool(
            int characterId,
            int accountId,
            InventoryListType listType,
            short slotIndex,
            out AccountCargoUpgradeToolResult result);

        bool TrySellItem(int characterId, int accountId, InventoryListType listType, short slotIndex, short sellCount, out InventoryMutationResult result);

        bool TryBuyCeraShopItem(int characterId, int accountId, int productId, int buyCount, int paymentMode, byte attributeValue, out InventoryMutationResult result);

        bool TryDisjointItem(int characterId, int accountId, DisjointItemRequest request, out DisjointItemResult result);
        bool TryDisjointAvatar(int characterId, int accountId, AvatarDisjointRequest request, out AvatarDisjointResult result);
        bool TryCompoundEmblems(int characterId, int accountId, EmblemCompoundRequest request, out EmblemCompoundResult result);

        bool TryCompoundItemRecipe(int characterId, int accountId, CompoundItemRecipeRequest request, out CompoundItemRecipeResult result);

        bool TryEnchantByBead(int characterId, int accountId, EnchantByBeadCommand command, out EnchantByBeadResult result);

        bool TryUpgradeItem(int characterId, int accountId, ItemUpgradeCommand command, out ItemUpgradeResult result);

        bool TryPurifyItem(int characterId, int accountId, PurifyItemRequest request, out PurifyItemResult result);

        bool TryInvestItemAmplifyOption(int characterId, int accountId, InvestItemAmplifyOptionRequest request, out InvestItemAmplifyOptionResult result);

        bool TryOpenEquipmentSocket(int characterId, short targetSlotIndex, int targetItemTemplateId, short materialSlotIndex, out EquipmentSocketMutationResult result);

        bool TrySetEquipmentEmblems(int characterId, short targetSlotIndex, int targetItemTemplateId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out EquipmentEmblemMutationResult result);

        bool TryOpenAvatarSocket(int characterId, short targetSlotIndex, int targetItemTemplateId, short materialSlotIndex, out AvatarSocketMutationResult result);

        bool TrySetAvatarEmblems(int characterId, short targetSlotIndex, int targetItemTemplateId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out AvatarEmblemMutationResult result);

        bool TryCompoundAvatar(int characterId, int accountId, short slot1, short slot2, short consumeSlot,
            Func<int, int, int, List<int>> resolveNewItemIds, byte newOption,
            out List<int> newSlotsOut, out int oldItemId1, out int oldItemId2, out List<int> newItemIdsOut,
            out int consumedItemTemplateId, out int consumedItemRemainingCount);

        bool TryCompoundAvatarSet(int characterId, int accountId, short[] consumeSlots, int[] expectedItemIds, Func<int, int> resolveNewItemId, byte newOption,
            short consumeStackableSlot,
            out int newSlot, out List<int> oldItemIds, out int newItemId, out int consumedItemTemplateId, out int consumedItemRemainingCount);

        bool TryMoveItem(int characterId, int accountId, InventoryMoveRequest request, out InventoryMoveResult result);

        bool TrySortItems(int characterId, int accountId, InventoryListType listType, byte category);

        bool TryToggleSortItemLock(int characterId, InventoryListType listType, short slotIndex, out SortItemLockEntry entry);

        bool TryUnlockSortItemLock(int characterId, InventoryListType listType, short slotIndex);

        IReadOnlyList<SortItemLockEntry> LoadSortItemLocks(int characterId);

        IReadOnlyList<SortItemLockEntry> LoadSortItemLocks(int characterId, InventoryListType listType);

        bool TryLockEquipmentItem(int characterId, InventoryListType listType, short slotIndex, out EquipmentItemLockResult result);

        bool TryUnlockEquipmentItem(int characterId, InventoryListType listType, short slotIndex, out EquipmentItemLockResult result);

        bool TryCancelEquipmentItemUnlock(int characterId, InventoryListType listType, short slotIndex, out EquipmentItemLockResult result);

        IReadOnlyList<EquipmentItemLockEntry> LoadEquipmentItemLocks(int characterId);

        IReadOnlyList<EquipmentItemLockEntry> LoadEquipmentItemLocks(int characterId, InventoryListType listType);

        CommonInventoryItem LoadCommonItemForRefresh(int characterId, int accountId, InventoryListType listType, short slotIndex);

        CommonInventoryItem LoadEquipmentCommonItemForRefresh(int characterId, short slotIndex);

        AvatarInventoryItem LoadAvatarItemForRefresh(int characterId, short slotIndex);

        PetInventoryItem LoadPetItemForRefresh(int characterId, short slotIndex);

        void SeedNewCharacterEquipment(int characterId, int accountId, (short slot, int itemId)[] equipment);
    }
}
