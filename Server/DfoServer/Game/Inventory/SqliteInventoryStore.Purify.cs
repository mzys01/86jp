using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        private const byte UnidentifiedAmplifyFlag = 0x80;

        public bool TryPurifyItem(int characterId, int accountId, PurifyItemRequest request, out PurifyItemResult result)
        {
            result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidRequest);
            if (request == null || request.TargetSlotIndex < 0 || request.MaterialSlotIndex < 0)
                return false;

            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.TargetSlotIndex);
                    if (target == null
                        || target.ItemTemplateId != request.TargetItemTemplateId
                        || !string.Equals(target.ItemKind, "equipment", StringComparison.Ordinal))
                    {
                        result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidTarget);
                        return false;
                    }

                    if (IsEquipmentItemLocked(connection, transaction, characterId, target))
                    {
                        result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorLocked);
                        return false;
                    }

                    var material = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.MaterialSlotIndex);
                    if (material == null
                        || material.ItemTemplateId != request.MaterialItemTemplateId
                        || material.StackCount <= 0)
                    {
                        result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidMaterial);
                        return false;
                    }

                    if (!TryResolvePurifyAction(material.ItemTemplateId, out var action, out var materialCount)
                        || material.StackCount < materialCount)
                    {
                        result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidMaterial);
                        return false;
                    }

                    var metadata = ItemMetadataResolver.Resolve(target.ItemTemplateId);
                    if (!CanUseOutworldVigorItem(target, metadata))
                    {
                        result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorUnsupported);
                        return false;
                    }

                    var targetView = InventoryItemView.ForCommon(target);
                    var currentAmplifyType = targetView.AmplifyType;
                    var isUnidentified = (currentAmplifyType & UnidentifiedAmplifyFlag) != 0;
                    if (!isUnidentified)
                    {
                        result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidTarget);
                        return false;
                    }

                    if (action == PurifyItemAction.Purify)
                    {
                        var attributeType = RollAmplifyAttributeType();
                        targetView.AmplifyType = (byte)attributeType;
                        targetView.AmplifyValue = ItemAmplifier.CalculateInitialAttributeValue(metadata.Rarity, attributeType);
                    }
                    else
                    {
                        targetView.AmplifyType = 0;
                        targetView.AmplifyValue = 0;
                    }

                    _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
                    var materialRemaining = ConsumeAmplifyMaterial(connection, transaction, characterId, material, materialCount);
                    _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, material, materialCount);
                    _auditLogger.WriteAuditLog(connection, transaction, characterId,
                        action == PurifyItemAction.Purify ? "purify_outworld_vigor" : "clear_outworld_vigor",
                        target, target.ListType, target.SlotIndex, 0);
                    transaction.Commit();

                    result = new PurifyItemResult
                    {
                        Request = request,
                        ErrorCode = 0,
                        Action = action,
                        TargetSlotIndex = target.SlotIndex,
                        MaterialSlotIndex = material.SlotIndex,
                        MaterialRemainingCount = materialRemaining,
                        AmplifyType = targetView.AmplifyType,
                        AmplifyValue = targetView.AmplifyValue,
                    };
                    return true;
                }
            }
        }

        private static bool TryResolvePurifyAction(int itemTemplateId, out PurifyItemAction action, out int materialCount)
        {
            action = PurifyItemAction.Unknown;
            materialCount = 0;

            if (ItemUpgradeTableProvider.TryGetPurifyMaterialCount(itemTemplateId, out materialCount))
            {
                action = PurifyItemAction.Purify;
                return true;
            }

            if (ItemUpgradeTableProvider.TryGetOutworldVigorClearMaterialCount(itemTemplateId, out materialCount))
            {
                action = PurifyItemAction.Clear;
                return true;
            }

            return false;
        }

        private static bool CanUseOutworldVigorItem(ItemRecord target, ItemMetadata metadata)
        {
            if (target == null || metadata == null)
                return false;

            if (!string.Equals(target.ItemKind, "equipment", StringComparison.Ordinal)
                || !string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal))
                return false;

            return metadata.MinimumLevel >= ItemUpgradeTableProvider.GetAmplifyEquipLevelConst()
                && metadata.Rarity >= 2;
        }

        private static AmplifyAttributeType RollAmplifyAttributeType()
        {
            var types = new[]
            {
                AmplifyAttributeType.Vitality,
                AmplifyAttributeType.Spirit,
                AmplifyAttributeType.Strength,
                AmplifyAttributeType.Intelligence,
            };
            return types[ServerRandom.Next(types.Length)];
        }

        private int ConsumeAmplifyMaterial(SqliteConnection connection, SqliteTransaction transaction, int characterId, ItemRecord material, int consumeCount)
        {
            var remainingCount = material.StackCount - Math.Max(1, consumeCount);
            if (remainingCount > 0)
            {
                _db.UpdateStackCount(connection, transaction, material.ItemUid, remainingCount);
                return remainingCount;
            }

            _db.DeleteItem(connection, transaction, material.ItemUid);
            DeleteSortItemLock(characterId, connection, transaction, material.ListType, material.SlotIndex);
            return 0;
        }

        private static PurifyItemResult CreatePurifyErrorResult(PurifyItemRequest request, byte errorCode)
        {
            return new PurifyItemResult
            {
                Request = request,
                ErrorCode = errorCode,
            };
        }
    }
}
