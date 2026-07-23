using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryGrowChronicleEquipment(int characterId, int accountId, ChronicleGrowthCommand command, out ChronicleGrowthResult result)
        {
            result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInvalidRequest);
            if (command == null || command.TicketSlotIndex < 0 || command.TargetSlotIndex < 0 || command.Materials.Count != 1)
                return false;

            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var ticket = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, command.TicketSlotIndex);
                    if (ticket == null || ticket.ItemTemplateId != command.TicketItemTemplateId || ticket.StackCount <= 0
                        || !TryResolveChronicleGrowthTicket(ticket.ItemTemplateId, out var ticketFile, out var growth))
                    {
                        result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInsufficientMaterial);
                        return false;
                    }

                    var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, command.TargetSlotIndex);
                    if (target == null || target.ItemTemplateId != command.TargetItemTemplateId
                        || !string.Equals(target.ItemKind, "equipment", StringComparison.Ordinal)
                        || !ItemMetadataResolver.TryLoadEquipmentFile(target.ItemTemplateId, out var equipment))
                    {
                        result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInvalidTarget);
                        return false;
                    }

                    if (IsEquipmentItemLocked(connection, transaction, characterId, target))
                    {
                        result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorLocked);
                        return false;
                    }

                    var targetView = InventoryItemView.ForCommon(target);
                    var currentLevel = equipment.MinimumLevel + targetView.Entry84.EmancipateEquipmentLevel;
                    if (!AllowsChronicleGrowthTarget(growth, ticketFile, equipment, target.ItemTemplateId, targetView, currentLevel))
                    {
                        result = ChronicleGrowthResult.Error(command,
                            currentLevel >= ResolveMaximumLevel(growth) ? ChronicleGrowthResult.ErrorMaximumLevel : ChronicleGrowthResult.ErrorRestricted);
                        return false;
                    }

                    var equipmentType = EquipmentTypeInfo.ParseOrUnknown(equipment.EquipmentType);
                    var hasAmplification = (targetView.AmplifyType & 0x0F) != 0;
                    var reinforceLevel = hasAmplification ? 0 : targetView.Upgrade;
                    var amplifyLevel = hasAmplification ? targetView.Upgrade : 0;
                    // The client treats forging 0..7 as the genuine-grade cost input.
                    // At the completed +8 stage (and above), that input resets to zero.
                    var genuineGrade = ChronicleGrowthCostCalculator.ResolveCostGenuineGrade(targetView.Forging);
                    var requiredFragments = ChronicleGrowthCostCalculator.Calculate(
                        currentLevel, equipmentType, reinforceLevel, amplifyLevel, genuineGrade);

                    var requestedMaterial = command.Materials[0];
                    if (requestedMaterial.ItemTemplateId != ChronicleGrowthCostCalculator.FragmentItemTemplateId
                        || requestedMaterial.SlotIndex == command.TicketSlotIndex)
                    {
                        result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInsufficientMaterial);
                        return false;
                    }

                    var fragments = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, requestedMaterial.SlotIndex);
                    if (fragments == null || fragments.ItemTemplateId != requestedMaterial.ItemTemplateId
                        || fragments.StackCount < requiredFragments)
                    {
                        result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorInsufficientMaterial);
                        return false;
                    }

                    var successWeight = ResolveSuccessWeight(growth, currentLevel);
                    if (successWeight < 0)
                    {
                        result = ChronicleGrowthResult.Error(command, ChronicleGrowthResult.ErrorRestricted);
                        return false;
                    }

                    var roll = ServerRandom.Next(100000);
                    var succeeded = roll < Math.Min(100000, successWeight);
                    var maximumLevel = ResolveMaximumLevel(growth);
                    var newLevel = succeeded
                        ? Math.Min(maximumLevel, currentLevel + growth.UpgradeLevel)
                        : currentLevel;
                    if (succeeded)
                    {
                        targetView.Entry84.EmancipateEquipmentLevel = checked((byte)(newLevel - equipment.MinimumLevel));
                        _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
                    }

                    var ticketRemaining = ConsumeChronicleGrowthItem(connection, transaction, characterId, ticket, 1);
                    var fragmentRemaining = ConsumeChronicleGrowthItem(connection, transaction, characterId, fragments, requiredFragments);
                    _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, ticket, 1);
                    _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, fragments, requiredFragments);
                    _auditLogger.WriteAuditLog(connection, transaction, characterId, "grow_chronicle_equipment",
                        target, target.ListType, target.SlotIndex, newLevel);
                    transaction.Commit();

                    result = new ChronicleGrowthResult
                    {
                        Command = command,
                        ErrorCode = 0,
                        GrowthSucceeded = succeeded,
                        OldLevel = currentLevel,
                        NewLevel = newLevel,
                        RequiredFragmentCount = requiredFragments,
                        SuccessWeight = successWeight,
                        ProbabilityRoll = roll,
                    };
                    result.Consumptions.Add(new ChronicleGrowthConsumption
                    {
                        ListType = InventoryListType.Main,
                        SlotIndex = ticket.SlotIndex,
                        ItemTemplateId = ticket.ItemTemplateId,
                        ConsumedCount = 1,
                        RemainingCount = ticketRemaining,
                    });
                    result.Consumptions.Add(new ChronicleGrowthConsumption
                    {
                        ListType = InventoryListType.Main,
                        SlotIndex = fragments.SlotIndex,
                        ItemTemplateId = fragments.ItemTemplateId,
                        ConsumedCount = requiredFragments,
                        RemainingCount = fragmentRemaining,
                    });
                    return true;
                }
            }
        }

        private static bool TryResolveChronicleGrowthTicket(int itemTemplateId, out StackableItemFile ticket, out EquipmentLevelEmancipateInfo growth)
        {
            growth = null;
            if (!ItemMetadataResolver.TryLoadStackableFile(itemTemplateId, out ticket)
                || ticket.EmancipateTicket < 0
                || ticket.EquipmentLevelEmancipate == null
                || ticket.EquipmentLevelEmancipate.UpgradeLevel <= 0)
                return false;

            growth = ticket.EquipmentLevelEmancipate;
            return true;
        }

        private static bool AllowsChronicleGrowthTarget(
            EquipmentLevelEmancipateInfo growth,
            StackableItemFile ticket,
            EquipmentFile equipment,
            int targetItemTemplateId,
            InventoryItemView targetView,
            int currentLevel)
        {
            if (growth == null || equipment == null || targetView == null
                || growth.IgnoreIndexes.Contains(targetItemTemplateId)
                || currentLevel < growth.Condition.MinimumLevel
                || currentLevel >= ResolveMaximumLevel(growth)
                || (growth.Condition.Rarities.Count > 0 && !growth.Condition.Rarities.Contains(equipment.Rarity)))
                return false;

            var amplified = (targetView.AmplifyType & 0x0F) != 0;
            if (!amplified && ticket.EmancipateGradeMax >= 0 && targetView.Upgrade > ticket.EmancipateGradeMax)
                return false;
            if (amplified && ticket.EmancipateAmplifyMax >= 0 && targetView.Upgrade > ticket.EmancipateAmplifyMax)
                return false;
            var genuineGrade = ChronicleGrowthCostCalculator.ResolveCostGenuineGrade(targetView.Forging);
            if (ticket.EmancipateGenuineGradeMax >= 0 && genuineGrade > ticket.EmancipateGenuineGradeMax)
                return false;
            return true;
        }

        private static int ResolveMaximumLevel(EquipmentLevelEmancipateInfo growth)
            => growth?.Condition?.MaximumLevel > 0 ? growth.Condition.MaximumLevel : 86;

        private static int ResolveSuccessWeight(EquipmentLevelEmancipateInfo growth, int currentLevel)
        {
            if (growth?.Probabilities == null || growth.Probabilities.Count == 0)
                return -1;

            foreach (var entry in growth.Probabilities.OrderBy(entry => entry.MaximumLevel))
            {
                if (currentLevel <= entry.MaximumLevel)
                    return entry.Weight;
            }
            return -1;
        }

        private int ConsumeChronicleGrowthItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, ItemRecord item, int count)
        {
            var remaining = item.StackCount - count;
            if (remaining > 0)
            {
                _db.UpdateStackCount(connection, transaction, item.ItemUid, remaining);
                return remaining;
            }

            _db.DeleteItem(connection, transaction, item.ItemUid);
            DeleteSortItemLock(characterId, connection, transaction, item.ListType, item.SlotIndex);
            return 0;
        }
    }
}
