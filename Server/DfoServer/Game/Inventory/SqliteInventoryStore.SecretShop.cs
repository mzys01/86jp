using DfoServer.Game.Currency;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryBuySecretShopItem(
            int characterId,
            int accountId,
            int itemTemplateId,
            int itemCount,
            int goldCost,
            int requiredItemId,
            int requiredItemCount,
            out InventoryMutationResult result)
        {
            result = null;
            if (characterId <= 0 || accountId <= 0 || itemTemplateId <= 0 || itemCount <= 0 || itemCount > short.MaxValue || goldCost < 0)
                return false;
            if (!CurrencyService.IsCubeFragment(itemTemplateId)
                && !ItemMetadataResolver.Resolve(itemTemplateId).IsStackable
                && itemCount != 1)
                return false;

            var usesItemCurrency = requiredItemId > 0;
            if (usesItemCurrency != (requiredItemCount > 0))
                return false;
            if (usesItemCurrency && goldCost != 0)
                return false;

            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var walletBefore = CurrencyService.LoadWallet(connection, transaction, characterId);
            if (!usesItemCurrency && walletBefore.Gold < goldCost)
                return false;

            if (!TryPickupItemCore(
                    connection,
                    transaction,
                    characterId,
                    accountId,
                    itemTemplateId,
                    itemCount,
                    out var assignedSlot))
                return false;

            short costItemSlot = -1;
            var costItemRemaining = -1;
            if (usesItemCurrency)
            {
                var removed = InventoryDbPrimitives.RemoveItemByTemplateId(
                    connection,
                    transaction,
                    characterId,
                    requiredItemId,
                    requiredItemCount);
                if (!removed.HasValue)
                    return false;

                costItemSlot = removed.Value.SlotIndex;
                costItemRemaining = removed.Value.RemainingCount;
            }
            else if (goldCost > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, goldCost))
            {
                return false;
            }

            var walletAfter = CurrencyService.LoadWallet(connection, transaction, characterId);
            var isCubeFragment = CurrencyService.IsCubeFragment(itemTemplateId);
            var record = isCubeFragment
                ? null
                : _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, assignedSlot);
            var itemValue = record?.InstanceValue ?? itemCount;
            var remainingStackCount = record?.StackCount ?? itemCount;
            if (isCubeFragment)
            {
                foreach (var cube in CurrencyService.LoadCubeFragments(connection, transaction, accountId))
                {
                    if (cube.ItemId != itemTemplateId)
                        continue;
                    itemValue = cube.Count;
                    remainingStackCount = cube.Count;
                    break;
                }
            }

            _auditLogger.WriteBuyAuditLog(
                connection,
                transaction,
                characterId,
                itemTemplateId,
                assignedSlot,
                goldCost,
                0);

            transaction.Commit();
            result = new InventoryMutationResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = assignedSlot,
                ItemTemplateId = itemTemplateId,
                RemainingStackCount = remainingStackCount,
                InstanceValue = itemValue,
                Durability = record?.Durability ?? 0,
                ExtData0 = record != null ? InventoryItemView.ForCommon(record).Attr : (byte)0,
                UpdatedGold = walletAfter.Gold,
                UpdatedSp = walletAfter.Sp,
                UpdatedCoin = walletAfter.Cera,
                RequestedCount = checked((short)itemCount),
                AppliedCount = checked((short)itemCount),
                CostItemTemplateId = usesItemCurrency ? requiredItemId : 0,
                CostItemNewStackCount = costItemRemaining,
                CostItemSlotIndex = costItemSlot,
            };
            return true;
        }
    }
}
