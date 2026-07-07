using DfoServer.Game.DailyReset;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Inventory
{
    public enum LotteryOpenMode
    {
        ConfirmedRegular,
        DirectDoubleReward,
        DirectRegularPhaseStart,
    }

    public sealed class LotteryOpenPlan
    {
        private LotteryOpenPlan(LotteryOpenMode mode, int usedCount, bool hasActiveDoubleReward)
        {
            Mode = mode;
            UsedCount = usedCount;
            HasActiveDoubleReward = hasActiveDoubleReward;
        }

        public LotteryOpenMode Mode { get; }

        public int UsedCount { get; }

        public bool HasActiveDoubleReward { get; }

        public bool ShouldOpenNow => Mode != LotteryOpenMode.DirectRegularPhaseStart;

        public bool ShouldSendRegularPhaseStart => Mode == LotteryOpenMode.DirectRegularPhaseStart;

        public bool UseDoubleReward => Mode == LotteryOpenMode.DirectDoubleReward;

        public bool RefreshPremiumBeforePhaseStart => Mode == LotteryOpenMode.DirectRegularPhaseStart;

        public bool RefreshPremiumAfterOpen => Mode == LotteryOpenMode.DirectDoubleReward;

        public BoosterUseRequest CreateBoosterUseRequest(short slotIndex)
        {
            return new BoosterUseRequest
            {
                SlotIndex = slotIndex,
                RewardMultiplier = UseDoubleReward ? 2 : 1,
                ConsumeLotteryDoubleRewardUse = UseDoubleReward,
            };
        }

        public static LotteryOpenPlan ConfirmedRegular()
            => new LotteryOpenPlan(LotteryOpenMode.ConfirmedRegular, 0, false);

        public static LotteryOpenPlan DirectDoubleReward(int usedCount)
            => new LotteryOpenPlan(LotteryOpenMode.DirectDoubleReward, usedCount, true);

        public static LotteryOpenPlan DirectRegularPhaseStart(int usedCount, bool hasActiveDoubleReward)
            => new LotteryOpenPlan(LotteryOpenMode.DirectRegularPhaseStart, usedCount, hasActiveDoubleReward);
    }

    public sealed class LotteryOpenPlanner
    {
        private readonly DailyResetService _dailyResetService;
        private readonly Func<string> _connectionStringFactory;

        public LotteryOpenPlanner(DailyResetService dailyResetService, Func<string> connectionStringFactory = null)
        {
            _dailyResetService = dailyResetService ?? throw new ArgumentNullException(nameof(dailyResetService));
            _connectionStringFactory = connectionStringFactory
                ?? (() => SqliteDatabaseBootstrap.Initialize(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath));
        }

        public LotteryOpenPlan Resolve(int characterId, int accountId, bool isDirectFastOpen)
        {
            if (!isDirectFastOpen)
                return LotteryOpenPlan.ConfirmedRegular();

            var usedCount = PremiumService.GetLotteryDoubleRewardUsedCount(_dailyResetService, characterId);
            var hasActiveDoubleReward = false;
            if (characterId > 0 && accountId > 0 && usedCount < PremiumService.LotteryDoubleRewardDailyLimit)
            {
                var premiumType = DevilContractCatalog.SlotToPremiumType(PremiumService.LotteryDoubleRewardServiceIndex);
                hasActiveDoubleReward = PremiumService.HasActivePremium(_connectionStringFactory(), accountId, premiumType);
            }

            return ResolveDirectFastOpen(isDirectFastOpen, hasActiveDoubleReward, usedCount);
        }

        public static LotteryOpenPlan ResolveDirectFastOpen(bool isDirectFastOpen, bool hasActiveDoubleReward, int usedCount)
        {
            if (!isDirectFastOpen)
                return LotteryOpenPlan.ConfirmedRegular();

            if (hasActiveDoubleReward && usedCount < PremiumService.LotteryDoubleRewardDailyLimit)
                return LotteryOpenPlan.DirectDoubleReward(usedCount);

            return LotteryOpenPlan.DirectRegularPhaseStart(usedCount, hasActiveDoubleReward);
        }
    }
}
