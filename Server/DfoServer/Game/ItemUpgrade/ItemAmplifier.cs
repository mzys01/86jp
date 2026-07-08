using DfoServer.Game.Inventory;

namespace DfoServer.Game.ItemUpgrade
{
    public enum AmplifyAttributeType
    {
        None = 0,
        Vitality = 1,
        Spirit = 2,
        Strength = 3,
        Intelligence = 4,
    }

    public sealed class ItemAmplifierState
    {
        public AmplifyAttributeType AttributeType { get; set; } = AmplifyAttributeType.None;
        public int AttributeValue { get; set; }

        public bool HasAmplifyAttribute => AttributeType != AmplifyAttributeType.None;
        public bool IsIdentified => HasAmplifyAttribute && AttributeValue > 0;
    }

    public static class ItemAmplifier
    {
        public static ushort CalculateInitialAttributeValue(int rarity, AmplifyAttributeType attributeType)
        {
            if (attributeType == AmplifyAttributeType.None)
                return 0;

            return (ushort)ItemUpgradeTableProvider.CalculateInitialAmplifyValue(rarity, 0);
        }

        public static int GetUpgradeAttributeBonus(int currentUpgradeLevel)
        {
            return ItemUpgradeTableProvider.CalculateAmplifyBonusConst(currentUpgradeLevel);
        }

        public static ItemAmplifierState FromItem(InvenItem item)
        {
            if (item == null)
                return new ItemAmplifierState();

            return new ItemAmplifierState
            {
                AttributeType = ToAttributeType(item.AmplifyType),
                AttributeValue = item.AmplifyValue,
            };
        }

        public static bool CanReinforce(InvenItem item)
        {
            return FromItem(item).AttributeType == AmplifyAttributeType.None;
        }

        public static bool CanAmplify(InvenItem item)
        {
            var state = FromItem(item);
            return state.HasAmplifyAttribute && state.IsIdentified;
        }

        private static AmplifyAttributeType ToAttributeType(byte raw)
        {
            return raw >= (byte)AmplifyAttributeType.Vitality && raw <= (byte)AmplifyAttributeType.Intelligence
                ? (AmplifyAttributeType)raw
                : AmplifyAttributeType.None;
        }
    }
}
