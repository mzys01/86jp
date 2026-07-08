using DfoServer.Game.ItemUpgrade;
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
                        || material.StackCount <= 0
                        || !string.Equals(material.ItemKind, "stackable", StringComparison.Ordinal))
                    {
                        result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidMaterial);
                        return false;
                    }

                    if (!IsValidInvestMaterial(request, material.ItemTemplateId))
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

                    var selectedType = ResolveInvestAmplifyAttributeType(request, material.ItemTemplateId);
                    if (selectedType == AmplifyAttributeType.None)
                    {
                        result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidRequest);
                        return false;
                    }

                    var extra = ItemExtraView.Parse(target.ExtraJson);
                    var currentAmplifyType = extra.Equipment.AmplifyType;
                    var isUnidentified = (currentAmplifyType & UnidentifiedAmplifyFlag) != 0;
                    var currentIdentifiedType = (byte)(currentAmplifyType & 0x7F);
                    if (!CanApplyInvestAction(request.Action, isUnidentified, currentIdentifiedType, extra.Equipment.Upgrade))
                    {
                        result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidTarget);
                        return false;
                    }

                    if (currentIdentifiedType == (byte)selectedType)
                    {
                        result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorSameOption);
                        return false;
                    }

                    var builder = ItemExtraViewBuilder.FromView(extra);
                    builder.Equipment.AmplifyType = (byte)selectedType;
                    builder.Equipment.AmplifyValue = ItemAmplifier.CalculateInitialAttributeValue(metadata.Rarity, selectedType);
                    if (request.Action == InvestItemAmplifyOptionAction.PureGold)
                        builder.Equipment.Upgrade = RollPureGoldAmplifyLevel();

                    var updatedExtra = builder.Build();
                    target.ExtraJson = updatedExtra.Serialize();
                    _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
                    var materialRemaining = ConsumeAmplifyMaterial(connection, transaction, characterId, material);
                    _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, material, 1);
                    _auditLogger.WriteAuditLog(connection, transaction, characterId, "invest_item_amplify_option", target, InventoryListType.Main, target.SlotIndex, builder.Equipment.Upgrade);
                    transaction.Commit();

                    result = new InvestItemAmplifyOptionResult
                    {
                        Request = request,
                        ErrorCode = 0,
                        TargetSlotIndex = target.SlotIndex,
                        MaterialSlotIndex = material.SlotIndex,
                        MaterialRemainingCount = materialRemaining,
                        AmplifyType = builder.Equipment.AmplifyType,
                        AmplifyValue = builder.Equipment.AmplifyValue,
                        AmplifyLevel = builder.Equipment.Upgrade,
                    };
                    return true;
                }
            }
        }

        private static bool IsValidInvestMaterial(InvestItemAmplifyOptionRequest request, int materialItemTemplateId)
        {
            return (request.Action == InvestItemAmplifyOptionAction.Invest
                    && ItemUpgradeTableProvider.IsInvestAmplifyOptionMaterial(materialItemTemplateId))
                || (request.Action == InvestItemAmplifyOptionAction.Twist
                    && ItemUpgradeTableProvider.IsReinvestAmplifyOptionMaterial(materialItemTemplateId))
                || (request.Action == InvestItemAmplifyOptionAction.PureGold
                    && ItemUpgradeTableProvider.IsRandomInvestUpgradeOptionMaterial(materialItemTemplateId));
        }

        private static bool CanApplyInvestAction(
            InvestItemAmplifyOptionAction action,
            bool isUnidentified,
            byte currentIdentifiedType,
            byte currentUpgradeLevel)
        {
            if (action == InvestItemAmplifyOptionAction.Invest)
                return !isUnidentified && currentIdentifiedType == 0;

            if (action == InvestItemAmplifyOptionAction.Twist)
                return !isUnidentified && currentIdentifiedType != 0 && currentUpgradeLevel == 0;

            if (action == InvestItemAmplifyOptionAction.PureGold)
                return !isUnidentified;

            return false;
        }

        private static AmplifyAttributeType ResolveInvestAmplifyAttributeType(InvestItemAmplifyOptionRequest request, int materialItemTemplateId)
        {
            if (request.Action != InvestItemAmplifyOptionAction.Invest)
                return MapInvestOptionToAmplifyType(request.SelectedOption);

            if (!ItemUpgradeTableProvider.TryGetInvestAmplifyOptionType(materialItemTemplateId, out var optionType))
                return AmplifyAttributeType.None;

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

        private static byte RollPureGoldAmplifyLevel()
        {
            var roll = Random.Shared.Next(100);
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
