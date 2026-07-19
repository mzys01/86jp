using System;
using System.Collections.Generic;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Progression;
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
        public uint NormalExpGained { get; set; }
        public uint HonorExpGained { get; set; }
        public bool LeveledUp { get; set; }
        public bool CharacterStateChanged { get; set; }
        public AccountExperienceProgressSummary AccountProgress { get; set; }
        public IReadOnlyList<short> ChangedMainSlots { get; set; } = Array.Empty<short>();
        public IReadOnlyList<DeathTowerRewardItem> Items { get; set; } = Array.Empty<DeathTowerRewardItem>();
        internal ExperienceGrantResult ExperienceGrant { get; set; }
    }

    internal delegate ExperienceGrantResult DeathTowerExperienceGrantInTransaction(
        DbScope scope,
        int characterId,
        int accountId,
        byte currentLevel,
        uint currentExp,
        uint rawGain);

    public sealed class DeathTowerSettlementService
    {
        private readonly IAssetService _assetService;
        private readonly AccountExperienceProgressService _accountExperience;
        private readonly DeathTowerExperienceGrantInTransaction _grantExperienceInTransaction;

        public DeathTowerSettlementService(
            IAssetService assetService,
            AccountExperienceProgressService accountExperience = null)
            : this(assetService, accountExperience, null)
        {
        }

        internal DeathTowerSettlementService(
            IAssetService assetService,
            AccountExperienceProgressService accountExperience,
            DeathTowerExperienceGrantInTransaction grantExperienceInTransaction)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
            _accountExperience = accountExperience
                ?? throw new ArgumentNullException(nameof(accountExperience));
            _grantExperienceInTransaction = grantExperienceInTransaction
                ?? ((scope, characterId, accountId, level, exp, rawGain) =>
                    CharacterExperienceService.GrantInTransaction(
                    scope.Connection,
                    scope.Transaction,
                    characterId,
                    accountId,
                    level,
                    exp,
                    rawGain,
                    normalizeMaxLevelExp: rawGain > 0));
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
            var lcg = tower.StageLcg ?? new DnfLcg(tower.StageSeed);
            var rewardRollCount = Math.Min(
                Math.Max(0, tower.Config.MaxClearItemCount),
                rewardConfig.GetRewardCardCount(clearedFloorCount));

            var items = new List<DeathTowerRewardItem>(rewardRollCount);
            var changedMainSlots = new List<short>(rewardRollCount);
            var changedMainSlotSet = new HashSet<short>();
            var accountId = session.Account?.AccountId ?? 1;
            var updatedGold = 0;
            ExperienceGrantResult expProgress;
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
                    if (changedMainSlotSet.Add(assignedSlot))
                        changedMainSlots.Add(assignedSlot);
                }

                updatedGold = _assetService.LoadWallet(scope).Gold;
                expProgress = _grantExperienceInTransaction(
                    scope,
                    session.Player.CharacterId,
                    accountId,
                    previousLevel,
                    session.Player.Exp,
                    expGained);
                var shouldPersistCharacter = expProgress.LeveledUp
                    || expProgress.NormalExpGain > 0
                    || expProgress.NormalizedMaxLevelExp;
                if (shouldPersistCharacter && !expProgress.Persisted)
                {
                    throw new InvalidOperationException(
                        $"Death tower settlement progress write failed for character {session.Player.CharacterId}.");
                }
                scope.Commit();
            }

            session.Player.Exp = expProgress.NewExp;
            session.Player.Level = expProgress.NewLevel;

            AccountExperienceProgressSummary accountProgress = null;
            if (expProgress.HonorExpGain > 0 && accountId > 0)
            {
                var totals = new AccountExperienceProgressTotals(
                    expProgress.TotalHonorExp,
                    expProgress.TotalGrowthCapsuleExp,
                    expProgress.GrowthCapsuleExpGain);
                try
                {
                    accountProgress = _accountExperience.BuildSummary(accountId, totals);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[DeathTower] committed account progress summary failed: account={accountId} cid={session.Player.CharacterId}: {ex.Message}");
                }
                if (accountProgress != null)
                {
                    expProgress.Honor = accountProgress.Honor;
                    expProgress.GrowthCapsule = accountProgress.GrowthCapsule;
                }
            }

            var characterStateChanged = expProgress.NewLevel != expProgress.PreviousLevel
                || expProgress.NewExp != expProgress.PreviousExp;

            return new DeathTowerSettlementResult
            {
                ClearedFloorCount = clearedFloorCount,
                ExpGained = expGained,
                GoldGained = goldGained,
                UpdatedGold = updatedGold,
                PreviousLevel = previousLevel,
                UpdatedLevel = expProgress.NewLevel,
                NormalExpGained = expProgress.NormalExpGain,
                HonorExpGained = expProgress.HonorExpGain,
                LeveledUp = expProgress.LeveledUp,
                CharacterStateChanged = characterStateChanged,
                AccountProgress = accountProgress,
                ChangedMainSlots = changedMainSlots,
                Items = items,
                ExperienceGrant = expProgress,
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

    }
}
