using System;
using System.Collections.Generic;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Network;

namespace DfoServer.Game.DeathTower
{
    public readonly struct DeathTowerRewardItem
    {
        public DeathTowerRewardItem(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }
        public int Count { get; }
    }

    public sealed class DeathTowerSettlementResult
    {
        public int ClearedFloorCount { get; set; }
        public uint ExpGained { get; set; }
        public int GoldGained { get; set; }
        public int UpdatedGold { get; set; }
        public byte PreviousLevel { get; set; }
        public byte UpdatedLevel { get; set; }
        public uint UpdatedExp { get; set; }
        public IReadOnlyList<DeathTowerRewardItem> Items { get; set; } = Array.Empty<DeathTowerRewardItem>();
    }

    public sealed class DeathTowerSettlementService
    {
        private readonly IAssetService _assetService;
        private readonly Func<DbScope, int, byte, uint, bool> _persistLevelAndExp;

        public DeathTowerSettlementService(
            IAssetService assetService,
            Func<DbScope, int, byte, uint, bool> persistLevelAndExp = null)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
            _persistLevelAndExp = persistLevelAndExp
                ?? ((scope, characterId, level, exp) => CharacterProgressService.PersistLevelAndExp(
                    scope.Connection,
                    scope.Transaction,
                    characterId,
                    level,
                    exp));
        }

        public DeathTowerSettlementResult Grant(
            EnhancedClientSession session,
            DeathTowerSession tower)
        {
            if (session?.Player == null) throw new ArgumentNullException(nameof(session));
            if (tower == null) throw new ArgumentNullException(nameof(tower));

            var rewardConfig = DeathTowerRewardConfig.Load();
            var clearedFloorCount = Math.Max(1, tower.CurrentStage + 1);
            var previousLevel = session.Player.Level;
            var expGained = CalculateExp(previousLevel, rewardConfig.GetExpWeight(clearedFloorCount));
            var goldGained = CalculateGold(previousLevel, rewardConfig.GoldWeight);
            var updatedExp = AddSaturating(session.Player.Exp, expGained);
            var updatedLevel = ExpTableProvider.ApplyLevelUps(previousLevel, updatedExp);
            var lcg = tower.StageLcg ?? new DnfLcg(tower.StageSeed);
            var rewardRollCount = Math.Min(
                Math.Max(0, tower.Config.MaxClearItemCount),
                rewardConfig.GetRewardCardCount(clearedFloorCount));

            var items = new List<DeathTowerRewardItem>(rewardRollCount);
            var accountId = session.Account?.AccountId ?? 1;
            var updatedGold = 0;
            using (var scope = _assetService.OpenScope(session.Player.CharacterId, accountId))
            {
                if (goldGained > 0)
                    _assetService.GrantGold(scope, goldGained);

                for (var index = 0; index < rewardRollCount; index++)
                {
                    var rarity = rewardConfig.RollItemRarity(lcg);
                    var itemId = MonsterDropConfig.ChooseEquipment(lcg, previousLevel, rarity);
                    if (itemId <= 0)
                        itemId = MonsterDropConfig.ChooseStackable(lcg, previousLevel, rarity);
                    if (itemId <= 0)
                        continue;

                    if (!_assetService.TryAddItem(scope, itemId, 1, out var assignedSlot))
                    {
                        FileLogger.Log($"[DeathTower] settlement item skipped: inventory full/unsupported cid={session.Player.CharacterId} item={itemId}");
                        continue;
                    }

                    items.Add(new DeathTowerRewardItem(itemId, 1));
                }

                updatedGold = _assetService.LoadWallet(scope).Gold;
                if ((expGained > 0 || updatedLevel != previousLevel)
                    && !_persistLevelAndExp(
                        scope,
                        session.Player.CharacterId,
                        updatedLevel,
                        updatedExp))
                {
                    throw new InvalidOperationException(
                        $"Death tower settlement progress write failed for character {session.Player.CharacterId}.");
                }
                scope.Commit();
            }

            session.Player.Exp = updatedExp;
            session.Player.Level = updatedLevel;

            return new DeathTowerSettlementResult
            {
                ClearedFloorCount = clearedFloorCount,
                ExpGained = expGained,
                GoldGained = goldGained,
                UpdatedGold = updatedGold,
                PreviousLevel = previousLevel,
                UpdatedLevel = updatedLevel,
                UpdatedExp = updatedExp,
                Items = items,
            };
        }

        private static uint CalculateExp(byte level, float weight)
        {
            if (weight <= 0)
                return 0;
            var value = ExpTableProvider.GetExpRewardBase(level) * (double)weight;
            if (value <= 0)
                return 0;
            return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private static int CalculateGold(byte level, float weight)
        {
            if (weight <= 0)
                return 0;
            var baseGold = ExpTableProvider.GetMonsterGold(level, out _);
            var value = baseGold * (double)weight;
            if (value <= 0)
                return 0;
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }

        private static uint AddSaturating(uint value, uint add)
        {
            var sum = (ulong)value + add;
            return sum > uint.MaxValue ? uint.MaxValue : (uint)sum;
        }
    }
}
