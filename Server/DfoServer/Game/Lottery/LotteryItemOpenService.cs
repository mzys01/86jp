using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Lottery
{
    public sealed class LotteryItemOpenService
    {
        private readonly SqliteInventoryStore _inventoryStore;
        private readonly LotteryItemDefinitionProvider _definitions;
        private readonly LotteryDoubleRewardPolicy _doubleRewardPolicy;
        private readonly InventoryAuditLogger _auditLogger = new InventoryAuditLogger();

        public LotteryItemOpenService(
            SqliteInventoryStore inventoryStore,
            LotteryItemDefinitionProvider definitions,
            LotteryDoubleRewardPolicy doubleRewardPolicy)
        {
            _inventoryStore = inventoryStore
                ?? throw new ArgumentNullException(nameof(inventoryStore));
            _definitions = definitions
                ?? throw new ArgumentNullException(nameof(definitions));
            _doubleRewardPolicy = doubleRewardPolicy
                ?? throw new ArgumentNullException(nameof(doubleRewardPolicy));
        }

        public bool CanOpen(
            int characterId,
            short slotIndex,
            out LotterySourceContext sourceContext)
        {
            sourceContext = null;
            using (var connection = new SqliteConnection(_inventoryStore.ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    if (!TryResolveSource(
                            connection,
                            transaction,
                            characterId,
                            slotIndex,
                            out var source,
                            out var definition))
                    {
                        return false;
                    }

                    if (!HasRequiredGold(connection, transaction, characterId, definition.GoldCost))
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
        }

        public bool TryOpen(
            int characterId,
            int accountId,
            short slotIndex,
            bool useDoubleReward,
            out LotteryOpenResult result)
        {
            result = null;
            using (var connection = new SqliteConnection(_inventoryStore.ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    if (!TryResolveSource(
                            connection,
                            transaction,
                            characterId,
                            slotIndex,
                            out var source,
                            out var definition))
                    {
                        return false;
                    }

                    var wallet = CurrencyService.LoadWallet(connection, transaction, characterId);
                    if (wallet.Gold < definition.GoldCost)
                        return false;

                    var appliedDoubleReward = useDoubleReward
                        && _doubleRewardPolicy.TryConsume(
                            connection,
                            transaction,
                            characterId,
                            accountId);

                    var selectedRewards = RollRewards(definition.RewardPool);
                    if (selectedRewards.Count == 0)
                        return false;

                    if (!ConsumeStackable(connection, transaction, source, 1))
                        return false;
                    if (definition.GoldCost > 0
                        && !CurrencyService.TrySpendGold(
                            connection,
                            transaction,
                            characterId,
                            definition.GoldCost))
                    {
                        return false;
                    }

                    var openResult = new LotteryOpenResult
                    {
                        SourceSlotIndex = source.SlotIndex,
                        SourceItemTemplateId = source.ItemTemplateId,
                        SourceRemainingStackCount = Math.Max(0, source.StackCount - 1),
                        ConsumedGold = definition.GoldCost,
                        UpdatedGold = wallet.Gold - definition.GoldCost,
                        UsedDoubleReward = appliedDoubleReward,
                    };

                    var multiplier = appliedDoubleReward ? 2 : 1;
                    foreach (var reward in AggregateRewardCopies(selectedRewards, multiplier))
                    {
                        if (!_inventoryStore._db.TryAddBoosterRewardItems(
                                connection,
                                transaction,
                                characterId,
                                accountId,
                                reward.ItemId,
                                reward.Count,
                                out var rewardResults))
                        {
                            return false;
                        }

                        openResult.Rewards.AddRange(rewardResults);
                    }

                    _auditLogger.WriteDeleteAuditLog(
                        connection,
                        transaction,
                        characterId,
                        source,
                        1);
                    foreach (var reward in openResult.Rewards)
                    {
                        _auditLogger.WriteBuyAuditLog(
                            connection,
                            transaction,
                            characterId,
                            reward.ItemTemplateId,
                            reward.SlotIndex,
                            0,
                            0);
                    }

                    transaction.Commit();
                    result = openResult;
                    return true;
                }
            }
        }

        private bool TryResolveSource(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short slotIndex,
            out SqliteInventoryStore.ItemRecord source,
            out LotteryItemDefinition definition)
        {
            source = _inventoryStore._db.LoadItemRecord(
                connection,
                transaction,
                characterId,
                InventoryListType.Main,
                slotIndex);
            definition = null;
            if (source == null
                || source.StackCount <= 0
                || !_definitions.TryGet(source.ItemTemplateId, out definition))
            {
                return false;
            }
            return true;
        }

        private static bool HasRequiredGold(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int goldCost)
        {
            return goldCost <= 0
                || CurrencyService.LoadWallet(connection, transaction, characterId).Gold >= goldCost;
        }

        private bool ConsumeStackable(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SqliteInventoryStore.ItemRecord item,
            int count)
        {
            if (item == null || count <= 0 || item.StackCount < count)
                return false;

            var remaining = item.StackCount - count;
            if (remaining > 0)
                _inventoryStore._db.UpdateStackCount(connection, transaction, item.ItemUid, remaining);
            else
                _inventoryStore._db.DeleteItem(connection, transaction, item.ItemUid);
            return true;
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

        private static IEnumerable<PvfLib.BoosterRewardEntry> AggregateRewardCopies(
            IReadOnlyList<PvfLib.BoosterRewardEntry> rewards,
            int multiplier)
        {
            return rewards
                .Where(reward => reward != null && reward.ItemId > 0 && reward.Count > 0)
                .GroupBy(reward => reward.ItemId)
                .Select(group => new PvfLib.BoosterRewardEntry
                {
                    ItemId = group.Key,
                    Count = group.Sum(reward => Math.Max(1, reward.Count)) * Math.Max(1, multiplier),
                    Weight = 10000,
                });
        }
    }
}
