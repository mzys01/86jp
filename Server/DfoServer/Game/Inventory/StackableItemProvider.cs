using PvfLib;
using System;

namespace DfoServer.Game.Inventory
{
    internal static class StackableItemProvider
    {
        internal const string UpgradableLegacyType = "[upgradable legacy]";
        internal const string RandomUpgradableLegacyType = "[random upgradable legacy]";

        internal static StackableItemFile Load(int itemTemplateId)
            => itemTemplateId > 0
                ? InventoryDbPrimitives.LoadStackableItem(itemTemplateId)
                : null;

        internal static bool IsLegacyContainer(int itemTemplateId)
        {
            var stackable = Load(itemTemplateId);
            if (stackable == null)
                return false;

            var type = NormalizeType(stackable.StackableType);
            return type.Equals(UpgradableLegacyType, StringComparison.OrdinalIgnoreCase)
                || type.Equals(RandomUpgradableLegacyType, StringComparison.OrdinalIgnoreCase);
        }

        internal static string NormalizeType(string stackableType)
        {
            if (string.IsNullOrWhiteSpace(stackableType))
                return string.Empty;

            var text = stackableType.Trim();
            var firstQuote = text.IndexOf('`');
            if (firstQuote >= 0)
            {
                var secondQuote = text.IndexOf('`', firstQuote + 1);
                if (secondQuote > firstQuote)
                    return text.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Trim();
            }

            var bracketStart = text.IndexOf('[');
            if (bracketStart >= 0)
            {
                var bracketEnd = text.IndexOf(']', bracketStart + 1);
                if (bracketEnd > bracketStart)
                    return text.Substring(bracketStart, bracketEnd - bracketStart + 1).Trim();
            }

            return text.Replace("`", string.Empty).Trim();
        }
    }
}
