using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Lottery
{
    public sealed class LotteryItemDefinitionProvider
    {
        private const string UpgradableLegacyType = "[upgradable legacy]";

        private readonly Func<int, PvfLib.StackableItemFile> _itemLoader;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<int, LotteryItemDefinition> _cache
            = new Dictionary<int, LotteryItemDefinition>();

        public LotteryItemDefinitionProvider(Func<int, PvfLib.StackableItemFile> itemLoader = null)
        {
            _itemLoader = itemLoader ?? InventoryDbPrimitives.LoadStackableItem;
        }

        public bool TryGet(int itemTemplateId, out LotteryItemDefinition definition)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(itemTemplateId, out definition))
                    return true;
            }

            if (!TryBuild(itemTemplateId, _itemLoader(itemTemplateId), out definition))
                return false;

            lock (_cacheLock)
                _cache[itemTemplateId] = definition;
            return true;
        }

        internal static bool TryBuild(
            int itemTemplateId,
            PvfLib.StackableItemFile stackable,
            out LotteryItemDefinition definition)
        {
            definition = null;
            if (itemTemplateId <= 0 || stackable == null)
                return false;

            var stackableType = NormalizeStackableType(stackable.StackableType);
            if (!stackableType.Equals(UpgradableLegacyType, StringComparison.OrdinalIgnoreCase))
                return false;

            IReadOnlyList<PvfLib.BoosterRewardEntry> rewardPool = stackable.UpgradableLegacyRewards;

            var validRewards = (rewardPool ?? Array.Empty<PvfLib.BoosterRewardEntry>())
                .Where(reward => reward != null
                    && reward.ItemId > 0
                    && reward.Count > 0
                    && reward.Weight > 0)
                .Select(CloneReward)
                .ToList();
            if (validRewards.Count == 0)
                return false;

            definition = new LotteryItemDefinition
            {
                ItemTemplateId = itemTemplateId,
                StackableType = stackableType,
                GoldCost = Math.Max(0, stackable.LotteryUseCost),
                RewardPool = validRewards,
            };
            return true;
        }

        private static PvfLib.BoosterRewardEntry CloneReward(PvfLib.BoosterRewardEntry reward)
        {
            return new PvfLib.BoosterRewardEntry
            {
                RewardKind = reward.RewardKind,
                Group = reward.Group,
                DrawCount = Math.Max(1, reward.DrawCount),
                ItemId = reward.ItemId,
                Weight = reward.Weight,
                Count = Math.Max(1, reward.Count),
            };
        }

        internal static string NormalizeStackableType(string stackableType)
        {
            if (string.IsNullOrWhiteSpace(stackableType))
                return string.Empty;

            var text = stackableType.Trim();
            var first = text.IndexOf('`');
            if (first >= 0)
            {
                var second = text.IndexOf('`', first + 1);
                if (second > first)
                    return text.Substring(first + 1, second - first - 1).Trim();
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
