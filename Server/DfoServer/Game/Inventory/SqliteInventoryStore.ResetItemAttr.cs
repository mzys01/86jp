using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryResetItemAttr(int characterId, int accountId, ResetItemAttrRequest request, out ResetItemAttrResult result)
        {
            _ = accountId;
            result = CreateResetItemAttrErrorResult(request, ResetItemAttrResult.ErrorInvalidRequest);
            if (request == null
                || request.TargetSlotIndex < 0
                || request.MaterialSlotIndex < 0
                || request.TargetSlotIndex == request.MaterialSlotIndex
                || request.TargetItemTemplateId <= 0)
            {
                return false;
            }

            // Resolve the equipment file before taking SQLite's immediate
            // writer lock.  The first PVF parse can be relatively expensive;
            // the transaction still reloads and validates the target row
            // before applying any mutation.
            ItemMetadata preloadedMetadata = null;
            Exception metadataLoadException = null;
            try
            {
                preloadedMetadata = ItemMetadataResolver.Resolve(request.TargetItemTemplateId);
            }
            catch (Exception ex)
            {
                metadataLoadException = ex;
            }

            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var target = _db.LoadItemRecord(
                        connection,
                        transaction,
                        characterId,
                        InventoryListType.Main,
                        request.TargetSlotIndex);
                    if (target == null
                        || target.ItemTemplateId != request.TargetItemTemplateId
                        || !string.Equals(target.ItemKind, "equipment", StringComparison.Ordinal))
                    {
                        result = CreateResetItemAttrErrorResult(request, ResetItemAttrResult.ErrorInvalidTarget);
                        return false;
                    }

                    if (IsEquipmentItemLocked(connection, transaction, characterId, target))
                    {
                        result = CreateResetItemAttrErrorResult(request, ResetItemAttrResult.ErrorLocked);
                        return false;
                    }

                    var material = _db.LoadItemRecord(
                        connection,
                        transaction,
                        characterId,
                        InventoryListType.Main,
                        request.MaterialSlotIndex);
                    if (material == null
                        || material.StackCount <= 0
                        || !string.Equals(material.ItemKind, "stackable", StringComparison.Ordinal))
                    {
                        result = CreateResetItemAttrErrorResult(request, ResetItemAttrResult.ErrorInvalidMaterial);
                        return false;
                    }

                    if (material.ExpireTime > 0
                        && material.ExpireTime <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    {
                        result = CreateResetItemAttrErrorResult(request, ResetItemAttrResult.ErrorInvalidMaterial);
                        return false;
                    }

                    var stackable = InventoryDbPrimitives.LoadStackableItem(material.ItemTemplateId);
                    if (!ResetItemAttrPolicyResolver.TryResolve(material.ItemTemplateId, stackable, out var policy))
                    {
                        result = CreateResetItemAttrErrorResult(request, ResetItemAttrResult.ErrorInvalidMaterial);
                        return false;
                    }

                    if (metadataLoadException != null)
                    {
                        FileLogger.Log($"  [ResetItemAttr] PVF target resolve failed item=0x{target.ItemTemplateId:X8}: {metadataLoadException.Message}");
                        result = CreateResetItemAttrErrorResult(request, ResetItemAttrResult.ErrorUnsupported);
                        return false;
                    }

                    var metadata = preloadedMetadata;
                    var equipmentType = metadata != null
                        ? EquipmentTypeInfo.ParseOrUnknown(metadata.EquipmentType)
                        : EquipmentType.Unknown;
                    if (metadata == null
                        || !string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal)
                        || !policy.Allows(equipmentType))
                    {
                        result = CreateResetItemAttrErrorResult(request, ResetItemAttrResult.ErrorUnsupported);
                        return false;
                    }

                    var oldQualitySeed = target.StackCount;
                    var newQualitySeed = policy.Mode == ResetItemAttrMode.Highest
                        ? unchecked((int)ItemQuality.TopQualitySeed)
                        : RollStandardQualitySeed(oldQualitySeed);

                    if (!TryConsumeResetMaterial(connection, transaction, characterId, material, out var materialRemaining))
                    {
                        result = CreateResetItemAttrErrorResult(request, ResetItemAttrResult.ErrorInvalidMaterial);
                        return false;
                    }

                    _db.UpdateEquipmentQualitySeed(connection, transaction, target.ItemUid, newQualitySeed);
                    _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, material, 1);
                    _auditLogger.WriteResetItemAttrAuditLog(
                        connection,
                        transaction,
                        characterId,
                        target,
                        material,
                        oldQualitySeed,
                        newQualitySeed);
                    transaction.Commit();

                    result = new ResetItemAttrResult
                    {
                        Request = request,
                        ErrorCode = 0,
                        Mode = policy.Mode,
                        TargetListType = InventoryListType.Main,
                        TargetSlotIndex = target.SlotIndex,
                        TargetItemTemplateId = target.ItemTemplateId,
                        MaterialSlotIndex = material.SlotIndex,
                        MaterialItemTemplateId = material.ItemTemplateId,
                        MaterialRemainingCount = materialRemaining,
                        OldQualitySeed = oldQualitySeed,
                        NewQualitySeed = newQualitySeed,
                    };
                    return true;
                }
            }
        }

        private static int RollStandardQualitySeed(int currentQualitySeed)
        {
            var topQualitySeed = unchecked((int)ItemQuality.TopQualitySeed);
            var qualitySeed = ServerRandom.Next(1, topQualitySeed);
            if (qualitySeed == currentQualitySeed)
                qualitySeed = qualitySeed + 1 < topQualitySeed ? qualitySeed + 1 : qualitySeed - 1;
            return qualitySeed;
        }

        private bool TryConsumeResetMaterial(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ItemRecord material,
            out int remainingCount)
        {
            remainingCount = 0;
            if (material == null || material.StackCount <= 0)
                return false;

            if (material.StackCount > 1)
            {
                remainingCount = material.StackCount - 1;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
UPDATE character_items
SET stack_count = @remainingCount,
    instance_value = @remainingCount,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @itemUid
  AND item_kind = 'stackable'
  AND stack_count = @expectedCount;";
                    command.Parameters.AddWithValue("@remainingCount", remainingCount);
                    command.Parameters.AddWithValue("@itemUid", material.ItemUid);
                    command.Parameters.AddWithValue("@expectedCount", material.StackCount);
                    return command.ExecuteNonQuery() == 1;
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM character_items
WHERE item_uid = @itemUid
  AND item_kind = 'stackable'
  AND stack_count = 1;";
                command.Parameters.AddWithValue("@itemUid", material.ItemUid);
                if (command.ExecuteNonQuery() != 1)
                    return false;
            }

            DeleteSortItemLock(characterId, connection, transaction, material.ListType, material.SlotIndex);
            return true;
        }

        private static ResetItemAttrResult CreateResetItemAttrErrorResult(ResetItemAttrRequest request, byte errorCode)
        {
            return new ResetItemAttrResult
            {
                Request = request,
                ErrorCode = errorCode,
            };
        }
    }
}
