using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Lottery
{
    public sealed class LotteryItemDefinitionProvider
    {
        private readonly Func<int, PvfLib.StackableItemFile> _itemLoader;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<int, LotteryItemDefinition> _cache
            = new Dictionary<int, LotteryItemDefinition>();

        public LotteryItemDefinitionProvider(Func<int, PvfLib.StackableItemFile> itemLoader = null)
        {
            _itemLoader = itemLoader ?? StackableItemProvider.Load;
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

            var stackableType = StackableItemProvider.NormalizeType(stackable.StackableType);
            if (!stackableType.Equals(
                    StackableItemProvider.UpgradableLegacyType,
                    StringComparison.OrdinalIgnoreCase))
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
                RequiredMaterial = ResolveRequiredMaterial(itemTemplateId, stackable),
                RewardPool = validRewards,
            };
            return true;
        }

        private static LotteryRequiredMaterial ResolveRequiredMaterial(
            int sourceItemTemplateId,
            PvfLib.StackableItemFile stackable)
        {
            foreach (var item in stackable?.LotteryUseNeedItems ?? Enumerable.Empty<PvfLib.RandomBoxRemovalItemEntry>())
            {
                if (item == null
                    || item.ItemId <= 0
                    || item.Count <= 0
                    || item.ItemId == sourceItemTemplateId)
                {
                    continue;
                }

                return new LotteryRequiredMaterial
                {
                    ItemTemplateId = item.ItemId,
                    Count = item.Count,
                };
            }

            return null;
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
    }
}
