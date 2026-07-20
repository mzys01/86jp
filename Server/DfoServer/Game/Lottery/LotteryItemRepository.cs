using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Lottery
{
    internal sealed class LotterySourceRecord
    {
        internal LotterySourceRecord(SqliteInventoryStore.ItemRecord inventoryRecord)
        {
            InventoryRecord = inventoryRecord
                ?? throw new ArgumentNullException(nameof(inventoryRecord));
        }

        internal SqliteInventoryStore.ItemRecord InventoryRecord { get; }

        internal short SlotIndex => InventoryRecord.SlotIndex;

        internal int ItemTemplateId => InventoryRecord.ItemTemplateId;

        internal int StackCount => InventoryRecord.StackCount;
    }

    internal sealed class LotteryMaterialRecord
    {
        internal LotteryMaterialRecord(SqliteInventoryStore.ItemRecord inventoryRecord)
        {
            InventoryRecord = inventoryRecord
                ?? throw new ArgumentNullException(nameof(inventoryRecord));
        }

        internal SqliteInventoryStore.ItemRecord InventoryRecord { get; }

        internal short SlotIndex => InventoryRecord.SlotIndex;

        internal int ItemTemplateId => InventoryRecord.ItemTemplateId;

        internal int StackCount => InventoryRecord.StackCount;
    }

    public sealed class LotteryItemRepository
    {
        private readonly SqliteInventoryStore _inventoryStore;
        private readonly IAssetService _assetService;
        private readonly InventoryAuditLogger _auditLogger = new InventoryAuditLogger();

        public LotteryItemRepository(
            SqliteInventoryStore inventoryStore,
            IAssetService assetService)
        {
            _inventoryStore = inventoryStore
                ?? throw new ArgumentNullException(nameof(inventoryStore));
            _assetService = assetService
                ?? throw new ArgumentNullException(nameof(assetService));
        }

        internal DbScope OpenScope(
            int characterId,
            int accountId,
            bool deferred = true)
        {
            return new DbScope(
                _inventoryStore.ConnectionString,
                characterId,
                accountId,
                deferred);
        }

        internal bool TryLoadSource(
            DbScope scope,
            short slotIndex,
            out LotterySourceRecord source)
        {
            source = null;
            if (scope == null || slotIndex < 0)
                return false;

            var inventoryRecord = _inventoryStore._db.LoadItemRecord(
                scope.Connection,
                scope.Transaction,
                scope.CharacterId,
                InventoryListType.Main,
                slotIndex);
            if (inventoryRecord == null || inventoryRecord.StackCount <= 0)
                return false;

            source = new LotterySourceRecord(inventoryRecord);
            return true;
        }

        internal int LoadGold(DbScope scope)
            => _assetService.LoadWallet(scope).Gold;

        internal bool TrySpendGold(DbScope scope, int amount)
            => amount <= 0 || _assetService.TrySpendGold(scope, amount);

        internal bool TryLoadMaterial(
            DbScope scope,
            LotteryRequiredMaterial requiredMaterial,
            out LotteryMaterialRecord material)
        {
            material = null;
            if (scope == null || requiredMaterial == null || requiredMaterial.ItemTemplateId <= 0 || requiredMaterial.Count <= 0)
                return true;

            var metadata = ItemMetadataResolver.Resolve(requiredMaterial.ItemTemplateId);
            metadata.GetSlotRange(out var slotStart, out var slotEnd);
            var record = _inventoryStore._db.FindItemByTemplateIdInRange(
                    scope.Connection,
                    scope.Transaction,
                    scope.CharacterId,
                    InventoryListType.Main,
                    requiredMaterial.ItemTemplateId,
                    SqliteInventoryStore.QuickSlotStart,
                    SqliteInventoryStore.QuickSlotEnd)
                ?? _inventoryStore._db.FindItemByTemplateIdInRange(
                    scope.Connection,
                    scope.Transaction,
                    scope.CharacterId,
                    InventoryListType.Main,
                    requiredMaterial.ItemTemplateId,
                    slotStart,
                    slotEnd);
            if (record == null || record.StackCount < requiredMaterial.Count)
                return false;

            material = new LotteryMaterialRecord(record);
            return true;
        }

        internal bool TryConsumeSource(
            DbScope scope,
            LotterySourceRecord source,
            int count)
        {
            if (scope == null
                || source == null
                || count <= 0
                || source.StackCount < count)
            {
                return false;
            }

            var remaining = source.StackCount - count;
            if (remaining > 0)
            {
                _inventoryStore._db.UpdateStackCount(
                    scope.Connection,
                    scope.Transaction,
                    source.InventoryRecord.ItemUid,
                    remaining);
            }
            else
            {
                _inventoryStore._db.DeleteItem(
                    scope.Connection,
                    scope.Transaction,
                    source.InventoryRecord.ItemUid);
            }

            return true;
        }

        internal bool TryConsumeMaterial(
            DbScope scope,
            LotteryMaterialRecord material,
            int count)
        {
            if (scope == null || material == null || count <= 0 || material.StackCount < count)
                return false;

            var remaining = material.StackCount - count;
            if (remaining > 0)
            {
                _inventoryStore._db.UpdateStackCount(
                    scope.Connection,
                    scope.Transaction,
                    material.InventoryRecord.ItemUid,
                    remaining);
            }
            else
            {
                _inventoryStore._db.DeleteItem(
                    scope.Connection,
                    scope.Transaction,
                    material.InventoryRecord.ItemUid);
            }

            return true;
        }

        internal bool TryGrantReward(
            DbScope scope,
            int itemTemplateId,
            int count,
            out IReadOnlyList<LotteryRewardGrant> grants)
        {
            grants = Array.Empty<LotteryRewardGrant>();
            if (scope == null || itemTemplateId <= 0 || count <= 0)
                return false;

            if (!_inventoryStore._db.TryAddBoosterRewardItems(
                    scope.Connection,
                    scope.Transaction,
                    scope.CharacterId,
                    scope.AccountId,
                    itemTemplateId,
                    count,
                    out var inventoryResults))
            {
                return false;
            }

            grants = inventoryResults
                .Where(result => result != null)
                .Select(result => new LotteryRewardGrant
                {
                    ListType = result.ListType,
                    SlotIndex = result.SlotIndex,
                    ItemTemplateId = result.ItemTemplateId,
                    StackCount = result.StackCount,
                    GrantedCount = result.GrantedCount,
                })
                .ToList();
            return grants.Count > 0;
        }

        internal void WriteAudit(
            DbScope scope,
            LotterySourceRecord source,
            LotteryMaterialRecord material,
            int materialCount,
            IReadOnlyList<LotteryRewardGrant> grants)
        {
            _auditLogger.WriteDeleteAuditLog(
                scope.Connection,
                scope.Transaction,
                scope.CharacterId,
                source.InventoryRecord,
                1);
            if (material != null && materialCount > 0)
            {
                _auditLogger.WriteDeleteAuditLog(
                    scope.Connection,
                    scope.Transaction,
                    scope.CharacterId,
                    material.InventoryRecord,
                    materialCount);
            }

            foreach (var grant in grants ?? Array.Empty<LotteryRewardGrant>())
            {
                _auditLogger.WriteBuyAuditLog(
                    scope.Connection,
                    scope.Transaction,
                    scope.CharacterId,
                    grant.ItemTemplateId,
                    grant.SlotIndex,
                    0,
                    0);
            }
        }
    }
}
