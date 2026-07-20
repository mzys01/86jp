using DfoServer.Game.Inventory;
using System.Collections.Generic;

namespace DfoServer.Game.Lottery
{
    public sealed class LotteryItemDefinition
    {
        public int ItemTemplateId { get; set; }

        public string StackableType { get; set; }

        public int GoldCost { get; set; }

        public LotteryRequiredMaterial RequiredMaterial { get; set; }

        public IReadOnlyList<PvfLib.BoosterRewardEntry> RewardPool { get; set; }
    }

    public sealed class LotteryRequiredMaterial
    {
        public int ItemTemplateId { get; set; }

        public int Count { get; set; }
    }

    public sealed class LotterySourceContext
    {
        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int StackCount { get; set; }
    }

    public sealed class LotteryOpenResult
    {
        public short SourceSlotIndex { get; set; }

        public int SourceItemTemplateId { get; set; }

        public int SourceRemainingStackCount { get; set; }

        public int ConsumedGold { get; set; }

        public int UpdatedGold { get; set; }

        public int ConsumedMaterialItemTemplateId { get; set; }

        public short ConsumedMaterialSlotIndex { get; set; }

        public int ConsumedMaterialCount { get; set; }

        public int ConsumedMaterialRemainingStackCount { get; set; }

        public bool UsedDoubleReward { get; set; }

        public List<LotteryRewardGrant> Rewards { get; } = new List<LotteryRewardGrant>();
    }

    public sealed class LotteryRewardGrant
    {
        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int StackCount { get; set; }

        public int GrantedCount { get; set; }
    }

    internal sealed class LotteryRewardPlan
    {
        internal int ItemTemplateId { get; set; }

        internal int Count { get; set; }
    }
}
