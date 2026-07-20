using DfoServer.Game.ItemUpgrade;
using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class ResetItemAttrPolicy
    {
        private readonly HashSet<EquipmentType> _allowedEquipmentTypes;

        internal ResetItemAttrPolicy(ResetItemAttrMode mode, bool hasExplicitEquipmentTypes, HashSet<EquipmentType> allowedEquipmentTypes)
        {
            Mode = mode;
            HasExplicitEquipmentTypes = hasExplicitEquipmentTypes;
            _allowedEquipmentTypes = allowedEquipmentTypes ?? new HashSet<EquipmentType>();
        }

        internal ResetItemAttrMode Mode { get; }

        internal bool HasExplicitEquipmentTypes { get; }

        internal bool Allows(EquipmentType equipmentType)
        {
            return IsResettableEquipmentType(equipmentType)
                && (!HasExplicitEquipmentTypes || _allowedEquipmentTypes.Contains(equipmentType));
        }

        internal static bool IsResettableEquipmentType(EquipmentType equipmentType)
        {
            // The canonical ordinary kaleido PVF description explicitly
            // permits titles (base attributes only), while reinforcement
            // deliberately excludes them from IsUpgradeTargetType().
            return EquipmentTypeInfo.IsUpgradeTargetType(equipmentType)
                || equipmentType == EquipmentType.TitleName;
        }
    }

    internal static class ResetItemAttrPolicyResolver
    {
        // The canonical 86-client equipment grade adjustment box is typed as [etc] in PVF.
        internal const int StandardKaleidoBoxItemId = 15;
        internal const int LiberatedKaleidoBoxItemId = 897;

        internal static bool TryResolve(int itemTemplateId, StackableItemFile stackable, out ResetItemAttrPolicy policy)
        {
            policy = null;
            if (stackable == null)
                return false;

            var stackableType = NormalizeToken(stackable.StackableType);
            var isGold = stackableType.IndexOf("gold kaleido", StringComparison.OrdinalIgnoreCase) >= 0;
            var isKaleido = isGold
                || stackableType.IndexOf("kaleido", StringComparison.OrdinalIgnoreCase) >= 0
                || itemTemplateId == StandardKaleidoBoxItemId
                || itemTemplateId == LiberatedKaleidoBoxItemId;
            if (!isKaleido)
                return false;

            var allowedTypes = new HashSet<EquipmentType>();
            var hasExplicitTypes = stackable.UsableEquipTypes != null && stackable.UsableEquipTypes.Count > 0;
            if (hasExplicitTypes)
            {
                foreach (var rawType in stackable.UsableEquipTypes)
                {
                    if (EquipmentTypeInfo.TryParse(rawType, out var equipmentType)
                        && ResetItemAttrPolicy.IsResettableEquipmentType(equipmentType))
                    {
                        allowedTypes.Add(equipmentType);
                    }
                }

                if (allowedTypes.Count == 0)
                    return false;
            }

            policy = new ResetItemAttrPolicy(
                isGold ? ResetItemAttrMode.Highest : ResetItemAttrMode.Random,
                hasExplicitTypes,
                allowedTypes);
            return true;
        }

        private static string NormalizeToken(string value)
        {
            return (value ?? string.Empty).Trim().Trim('`').Trim('[', ']').Trim();
        }
    }
}
