using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryInvestItemAmplifyOption(int characterId, int accountId, InvestItemAmplifyOptionRequest request, out InvestItemAmplifyOptionResult result)
        {
            result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidRequest);
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
                        result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidTarget);
                        return false;
                    }

                    if (IsEquipmentItemLocked(connection, transaction, characterId, target))
                    {
                        result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorLocked);
                        return false;
                    }

                    var material = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.MaterialSlotIndex);
                    if (material == null
                        || material.ItemTemplateId != request.MaterialItemTemplateId
                        || material.StackCount <= 0)
                    {
                        result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidMaterial);
                        return false;
                    }

                    if (!TryResolveInvestMaterial(request, material.ItemTemplateId, out var configuredOptionType, out var materialCount)
                        || material.StackCount < materialCount)
                    {
                        result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidMaterial);
                        return false;
                    }

                    var metadata = ItemMetadataResolver.Resolve(target.ItemTemplateId);
                    if (!CanUseOutworldVigorItem(target, metadata))
                    {
                        result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorUnsupported);
                        return false;
                    }

                    var selectedType = ResolveInvestAmplifyAttributeType(request, configuredOptionType);
                    if (selectedType == AmplifyAttributeType.None)
                    {
                        result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidRequest);
                        return false;
                    }

                    var targetView = InventoryItemView.ForCommon(target);
                    var currentAmplifyType = targetView.AmplifyType;
                    var isUnidentified = (currentAmplifyType & UnidentifiedAmplifyFlag) != 0;
                    var currentIdentifiedType = (byte)(currentAmplifyType & 0x7F);
                    if (!CanApplyInvestAction(request.Action, isUnidentified, currentIdentifiedType, targetView.Upgrade, out var actionErrorCode))
                    {
                        result = CreateInvestAmplifyErrorResult(request, actionErrorCode);
                        return false;
                    }

                    if (currentIdentifiedType == (byte)selectedType)
                    {
                        result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorSameOption);
                        return false;
                    }

                    targetView.AmplifyType = (byte)selectedType;
                    targetView.AmplifyValue = ItemAmplifier.CalculateInitialAttributeValue(metadata.Rarity, selectedType);
                    if (request.Action == InvestItemAmplifyOptionAction.PureGold)
                        targetView.Upgrade = RollPureGoldAmplifyLevel(material.ItemTemplateId);

                    _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
                    var materialRemaining = ConsumeAmplifyMaterial(connection, transaction, characterId, material, materialCount);
                    _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, material, materialCount);
                    _auditLogger.WriteAuditLog(connection, transaction, characterId, "invest_item_amplify_option", target, InventoryListType.Main, target.SlotIndex, targetView.Upgrade);
                    transaction.Commit();

                    result = new InvestItemAmplifyOptionResult
                    {
                        Request = request,
                        ErrorCode = 0,
                        TargetSlotIndex = target.SlotIndex,
                        MaterialSlotIndex = material.SlotIndex,
                        MaterialRemainingCount = materialRemaining,
                        AmplifyType = targetView.AmplifyType,
                        AmplifyValue = targetView.AmplifyValue,
                        AmplifyLevel = targetView.Upgrade,
                    };
                    return true;
                }
            }
        }

        private static bool TryResolveInvestMaterial(
            InvestItemAmplifyOptionRequest request,
            int materialItemTemplateId,
            out PvfLib.AmplifyOptionType optionType,
            out int materialCount)
        {
            optionType = PvfLib.AmplifyOptionType.None;
            materialCount = 0;
            if (request == null)
                return false;

            if (request.Action == InvestItemAmplifyOptionAction.Invest)
                return ItemUpgradeTableProvider.TryGetInvestAmplifyOption(materialItemTemplateId, out optionType, out materialCount);

            if (request.Action == InvestItemAmplifyOptionAction.Twist)
                return ItemUpgradeTableProvider.TryGetReinvestAmplifyOption(materialItemTemplateId, out optionType, out materialCount);

            if (request.Action == InvestItemAmplifyOptionAction.PureGold)
                return ItemUpgradeTableProvider.TryGetRandomInvestUpgradeOption(materialItemTemplateId, out optionType, out materialCount);

            return false;
        }

        private static bool CanApplyInvestAction(
            InvestItemAmplifyOptionAction action,
            bool isUnidentified,
            byte currentIdentifiedType,
            byte currentUpgradeLevel,
            out byte errorCode)
        {
            errorCode = InvestItemAmplifyOptionResult.ErrorInvalidTarget;

            if (action == InvestItemAmplifyOptionAction.Invest)
            {
                if (isUnidentified || currentIdentifiedType != 0)
                {
                    errorCode = InvestItemAmplifyOptionResult.ErrorAlreadyHasAmplifyOption;
                    return false;
                }

                if (currentUpgradeLevel != 0)
                {
                    errorCode = InvestItemAmplifyOptionResult.ErrorAlreadyUpgraded;
                    return false;
                }

                return true;
            }

            if (action == InvestItemAmplifyOptionAction.Twist)
            {
                if (isUnidentified || currentIdentifiedType == 0)
                {
                    errorCode = InvestItemAmplifyOptionResult.ErrorNoAmplifyOption;
                    return false;
                }

                if (currentUpgradeLevel != 0)
                {
                    errorCode = InvestItemAmplifyOptionResult.ErrorAlreadyUpgraded;
                    return false;
                }

                return true;
            }

            if (action == InvestItemAmplifyOptionAction.PureGold)
            {
                if (isUnidentified)
                {
                    errorCode = InvestItemAmplifyOptionResult.ErrorNoAmplifyOption;
                    return false;
                }

                return true;
            }

            return false;
        }

        private static AmplifyAttributeType ResolveInvestAmplifyAttributeType(InvestItemAmplifyOptionRequest request, PvfLib.AmplifyOptionType optionType)
        {
            if (optionType == PvfLib.AmplifyOptionType.All)
                return MapInvestOptionToAmplifyType(request.SelectedOption);

            return MapConfiguredOptionToAmplifyType(optionType);
        }

        private static AmplifyAttributeType MapConfiguredOptionToAmplifyType(PvfLib.AmplifyOptionType optionType)
        {
            switch (optionType)
            {
                case PvfLib.AmplifyOptionType.PhysicalAttack:
                    return AmplifyAttributeType.Strength;
                case PvfLib.AmplifyOptionType.MagicalAttack:
                    return AmplifyAttributeType.Intelligence;
                case PvfLib.AmplifyOptionType.PhysicalDefense:
                    return AmplifyAttributeType.Vitality;
                case PvfLib.AmplifyOptionType.MagicalDefense:
                    return AmplifyAttributeType.Spirit;
                default:
                    return AmplifyAttributeType.None;
            }
        }

        private static AmplifyAttributeType MapInvestOptionToAmplifyType(byte selectedOption)
        {
            switch (selectedOption)
            {
                case 1:
                    return AmplifyAttributeType.Vitality;
                case 2:
                    return AmplifyAttributeType.Spirit;
                case 3:
                    return AmplifyAttributeType.Strength;
                case 4:
                    return AmplifyAttributeType.Intelligence;
                default:
                    return AmplifyAttributeType.None;
            }
        }

        private static byte RollPureGoldAmplifyLevel(int materialItemTemplateId)
        {
            if (ItemMetadataResolver.TryLoadStackableFile(materialItemTemplateId, out var stackable)
                && stackable.AmplificationRandomValues != null
                && stackable.AmplificationRandomValues.Count > 0)
            {
                var totalWeight = 0;
                foreach (var entry in stackable.AmplificationRandomValues)
                {
                    if (entry != null && entry.Weight > 0)
                        totalWeight += entry.Weight;
                }

                if (totalWeight > 0)
                {
                    var roll = ServerRandom.Next(totalWeight);
                    foreach (var entry in stackable.AmplificationRandomValues)
                    {
                        if (entry == null || entry.Weight <= 0)
                            continue;

                        roll -= entry.Weight;
                        if (roll < 0)
                            return (byte)Math.Max(0, Math.Min(byte.MaxValue, entry.UpgradeLevel));
                    }
                }
            }

            return RollDefaultPureGoldAmplifyLevel();
        }

        private static byte RollDefaultPureGoldAmplifyLevel()
        {
            var roll = ServerRandom.Next(100);
            if (roll < 50)
                return 3;
            if (roll < 80)
                return 4;
            if (roll < 95)
                return 5;
            return 6;
        }

        private static InvestItemAmplifyOptionResult CreateInvestAmplifyErrorResult(InvestItemAmplifyOptionRequest request, byte errorCode)
        {
            return new InvestItemAmplifyOptionResult
            {
                Request = request,
                ErrorCode = errorCode,
            };
        }
    }
}
