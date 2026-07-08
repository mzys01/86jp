using DfoServer.Game.ItemUpgrade;
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
                        || material.StackCount <= 0
                        || !string.Equals(material.ItemKind, "stackable", StringComparison.Ordinal))
                    {
                        result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidMaterial);
                        return false;
                    }

                    var action = ResolvePurifyAction(material.ItemTemplateId);
                    if (action == PurifyItemAction.Unknown)
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

                    var extra = ItemExtraView.Parse(target.ExtraJson);
                    var currentAmplifyType = extra.Equipment.AmplifyType;
                    var isUnidentified = (currentAmplifyType & UnidentifiedAmplifyFlag) != 0;
                    if (!isUnidentified)
                    {
                        result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidTarget);
                        return false;
                    }

                    var builder = ItemExtraViewBuilder.FromView(extra);
                    if (action == PurifyItemAction.Purify)
                    {
                        var attributeType = RollAmplifyAttributeType();
                        builder.Equipment.AmplifyType = (byte)attributeType;
                        builder.Equipment.AmplifyValue = ItemAmplifier.CalculateInitialAttributeValue(metadata.Rarity, attributeType);
                    }
                    else
                    {
                        builder.Equipment.AmplifyType = 0;
                        builder.Equipment.AmplifyValue = 0;
                    }

                    var updatedExtra = builder.Build();
                    target.ExtraJson = updatedExtra.Serialize();
                    _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
                    var materialRemaining = ConsumeAmplifyMaterial(connection, transaction, characterId, material);
                    _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, material, 1);
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
                        AmplifyType = builder.Equipment.AmplifyType,
                        AmplifyValue = builder.Equipment.AmplifyValue,
                    };
                    return true;
                }
            }
        }

        private static PurifyItemAction ResolvePurifyAction(int itemTemplateId)
        {
            if (ItemUpgradeTableProvider.IsPurifyMaterial(itemTemplateId))
                return PurifyItemAction.Purify;

            if (ItemUpgradeTableProvider.IsOutworldVigorClearMaterial(itemTemplateId))
                return PurifyItemAction.Clear;

            return PurifyItemAction.Unknown;
        }

        private static bool CanUseOutworldVigorItem(ItemRecord target, ItemMetadata metadata)
        {
            if (target == null || metadata == null)
                return false;

            if (!string.Equals(target.ItemKind, "equipment", StringComparison.Ordinal)
                || !string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal))
                return false;

            return metadata.MinimumLevel >= 55 && metadata.Rarity >= 2;
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
            return types[Random.Shared.Next(types.Length)];
        }

        private int ConsumeAmplifyMaterial(SqliteConnection connection, SqliteTransaction transaction, int characterId, ItemRecord material)
        {
            var remainingCount = material.StackCount - 1;
            if (remainingCount > 0)
            {
                _db.UpdateStackCount(connection, transaction, material.ItemUid, remainingCount);
                return remainingCount;
            }

            _db.DeleteItem(connection, transaction, material.ItemUid);
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
