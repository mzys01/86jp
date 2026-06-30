using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public sealed class InventoryMoveRequest
    {
        public InventoryListType SourceListType { get; set; }

        public short SourceSlotIndex { get; set; }

        public int MoveCount { get; set; }

        public int SourceInstanceValue { get; set; }

        public InventoryListType DestinationListType { get; set; }

        public short DestinationSlotIndex { get; set; }

        public int DestinationInstanceValue { get; set; }
    }

    public sealed class InventoryMoveResult
    {
        public InventoryListType SourceListType { get; set; }

        public short SourceSlotIndex { get; set; }

        public int MoveValue32 { get; set; }

        public InventoryListType DestinationListType { get; set; }

        public short DestinationSlotIndex { get; set; }

        public bool Mutated { get; set; }

        public bool AckError { get; set; }
    }

    internal enum EquipOutcome
    {
        Equipped,
        Unequipped,
        ReverseError,
        NoOp,
    }

    public sealed class InventoryMutationResult
    {
        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int RemainingStackCount { get; set; }

        public int InstanceValue { get; set; }

        public ushort Durability { get; set; }

        public int UpdatedGold { get; set; }

        public int UpdatedSp { get; set; }

        public int UpdatedCoin { get; set; }

        public int UpdatedTokenCera { get; set; }

        public int UpdatedHappyTokenCera { get; set; }

        public short RequestedCount { get; set; }

        public short AppliedCount { get; set; }

        // 本次购买是否扣了金币(用于商城回包决定是否刷新主背包 slot0 金币显示)。
        public bool GoldSpent { get; set; }

        // 契约等道具购买即消耗，不入库；为 true 时跳过 ITEM_LIST 更新通知。
        public bool ConsumedOnPurchase { get; set; }

        public int CostItemTemplateId { get; set; }

        public int CostItemNewStackCount { get; set; }

        public short CostItemSlotIndex { get; set; }

        public List<InventoryMutationResult> ExtraResults { get; } = new List<InventoryMutationResult>();
    }

    public sealed class BoosterRewardResult
    {
        public InventoryListType ListType { get; set; } = InventoryListType.Main;

        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int StackCount { get; set; }

        public int GrantedCount { get; set; }
    }

    public sealed class BoosterUseResult
    {
        public short SourceSlotIndex { get; set; }

        public int SourceItemTemplateId { get; set; }

        public int SourceRemainingStackCount { get; set; }

        public int SourceInstanceValue { get; set; }

        public List<BoosterRewardResult> Rewards { get; } = new List<BoosterRewardResult>();
    }

    public sealed class EquipmentSocketMutationResult
    {
        public CommonInventoryItem TargetItem { get; set; }

        public InventoryMutationResult MaterialItem { get; set; }

        public bool MaterialConsumed { get; set; }
    }

    public sealed class EquipmentEmblemApplyRequest
    {
        public short EmblemSlot { get; set; }

        public int EmblemItemTemplateId { get; set; }

        public byte SocketIndex { get; set; }
    }

    public sealed class EquipmentEmblemMutationResult
    {
        public CommonInventoryItem TargetItem { get; set; }

        public bool TargetEquipped { get; set; }

        public List<InventoryMutationResult> ConsumedEmblems { get; } = new List<InventoryMutationResult>();
    }

    public sealed class AvatarSocketMutationResult
    {
        public AvatarInventoryItem TargetItem { get; set; }

        public InventoryMutationResult MaterialItem { get; set; }

        public bool MaterialConsumed { get; set; }
    }

    public sealed class AvatarEmblemMutationResult
    {
        public AvatarInventoryItem TargetItem { get; set; }

        public List<InventoryMutationResult> ConsumedEmblems { get; } = new List<InventoryMutationResult>();
    }
}
