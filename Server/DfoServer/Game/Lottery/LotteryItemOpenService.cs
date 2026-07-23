using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Lottery
{
    public sealed class LotteryItemOpenService
    {
        private readonly LotteryItemRepository _repository;
        private readonly LotteryItemDefinitionProvider _definitions;
        private readonly LotteryDoubleRewardPolicy _doubleRewardPolicy;

        public LotteryItemOpenService(
            LotteryItemRepository repository,
            LotteryItemDefinitionProvider definitions,
            LotteryDoubleRewardPolicy doubleRewardPolicy)
        {
            _repository = repository
                ?? throw new ArgumentNullException(nameof(repository));
            _definitions = definitions
                ?? throw new ArgumentNullException(nameof(definitions));
            _doubleRewardPolicy = doubleRewardPolicy
                ?? throw new ArgumentNullException(nameof(doubleRewardPolicy));
        }

        public bool CanOpen(
            int characterId,
            int accountId,
            short slotIndex,
            out LotterySourceContext sourceContext)
        {
            sourceContext = null;
            using (var scope = _repository.OpenScope(characterId, accountId))
            {
                if (!TryResolveSource(
                        scope,
                        slotIndex,
                        out var source,
                        out var definition))
                {
                    return false;
                }

                if (_repository.LoadGold(scope) < definition.GoldCost)
                    return false;
                if (!_repository.TryLoadMaterial(scope, definition.RequiredMaterial, out _))
                    return false;

                sourceContext = new LotterySourceContext
                {
                    SlotIndex = source.SlotIndex,
                    ItemTemplateId = source.ItemTemplateId,
                    StackCount = source.StackCount,
                };
                return true;
            }
        }

        public bool TryOpen(
            int characterId,
            int accountId,
            short slotIndex,
            bool useDoubleReward,
            out LotteryOpenResult result)
        {
            result = null;
            using (var scope = _repository.OpenScope(
                characterId,
                accountId,
                deferred: false))
            {
                if (!TryResolveSource(
                        scope,
                        slotIndex,
                        out var source,
                        out var definition))
                {
                    return false;
                }

                var currentGold = _repository.LoadGold(scope);
                if (currentGold < definition.GoldCost)
                    return false;
                if (!_repository.TryLoadMaterial(scope, definition.RequiredMaterial, out var material))
                    return false;

                var appliedDoubleReward = useDoubleReward
                    && _doubleRewardPolicy.TryConsume(scope);
                var selectedRewards = RollRewards(definition.RewardPool);
                if (selectedRewards.Count == 0)
                    return false;

                if (!_repository.TryConsumeSource(scope, source, 1))
                    return false;
                if (material != null && !_repository.TryConsumeMaterial(scope, material, definition.RequiredMaterial.Count))
                    return false;
                if (!_repository.TrySpendGold(scope, definition.GoldCost))
                    return false;

                var openResult = new LotteryOpenResult
                {
                    SourceSlotIndex = source.SlotIndex,
                    SourceItemTemplateId = source.ItemTemplateId,
                    SourceRemainingStackCount = Math.Max(0, source.StackCount - 1),
                    ConsumedGold = definition.GoldCost,
                    UpdatedGold = currentGold - definition.GoldCost,
                    ConsumedMaterialItemTemplateId = material?.ItemTemplateId ?? 0,
                    ConsumedMaterialSlotIndex = material?.SlotIndex ?? 0,
                    ConsumedMaterialCount = material == null ? 0 : definition.RequiredMaterial.Count,
                    ConsumedMaterialRemainingStackCount = material == null ? 0 : Math.Max(0, material.StackCount - definition.RequiredMaterial.Count),
                    UsedDoubleReward = appliedDoubleReward,
                };

                var multiplier = appliedDoubleReward ? 2 : 1;
                foreach (var reward in AggregateRewardCopies(selectedRewards, multiplier))
                {
                    if (!_repository.TryGrantReward(
                            scope,
                            reward.ItemTemplateId,
                            reward.Count,
                            out var grants))
                    {
                        return false;
                    }

                    openResult.Rewards.AddRange(grants);
                }

                _repository.WriteAudit(
                    scope,
                    source,
                    material,
                    openResult.ConsumedMaterialCount,
                    openResult.Rewards);
                scope.Commit();
                result = openResult;
                return true;
            }
        }

        private bool TryResolveSource(
            DbScope scope,
            short slotIndex,
            out LotterySourceRecord source,
            out LotteryItemDefinition definition)
        {
            definition = null;
            if (!_repository.TryLoadSource(scope, slotIndex, out source))
                return false;

            return _definitions.TryGet(source.ItemTemplateId, out definition);
        }

        internal static List<PvfLib.BoosterRewardEntry> RollRewards(
            IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            var selected = new List<PvfLib.BoosterRewardEntry>();
            if (rewards == null)
                return selected;

            foreach (var group in rewards.GroupBy(reward => reward.Group))
            {
                var totalWeight = group.Sum(reward => Math.Max(0, reward.Weight));
                if (totalWeight <= 0)
                    continue;

                var drawCount = Math.Max(1, group.Max(reward => reward.DrawCount));
                for (var drawIndex = 0; drawIndex < drawCount; drawIndex++)
                {
                    var roll = Random.Shared.Next(totalWeight);
                    var cumulative = 0;
                    foreach (var reward in group)
                    {
                        cumulative += Math.Max(0, reward.Weight);
                        if (roll >= cumulative)
                            continue;

                        selected.Add(reward);
                        break;
                    }
                }
            }

            return selected;
        }

        private static IEnumerable<LotteryRewardPlan> AggregateRewardCopies(
            IReadOnlyList<PvfLib.BoosterRewardEntry> rewards,
            int multiplier)
        {
            return rewards
                .Where(reward => reward != null && reward.ItemId > 0 && reward.Count > 0)
                .GroupBy(reward => reward.ItemId)
                .Select(group => new LotteryRewardPlan
                {
                    ItemTemplateId = group.Key,
                    Count = group.Sum(reward => Math.Max(1, reward.Count)) * Math.Max(1, multiplier),
                });
        }
    }
}
