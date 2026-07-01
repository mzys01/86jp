using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public sealed class DisjointItemRequest
    {
        public short TargetSlotIndex { get; set; }

        public InventoryListType ItemSpace { get; set; } = InventoryListType.Main;

        public short DisjointItemSlotIndex { get; set; } = -1;

        public int ContextValue { get; set; }
    }

    public sealed class DisjointItemResult
    {
        public const byte ErrorInvalidRequest = 0x13;
        public const byte ErrorInvalidTarget = 0x13;
        public const byte ErrorInventoryFull = 0x04;

        public DisjointItemRequest Request { get; set; }

        public byte ErrorCode { get; set; } = ErrorInvalidRequest;

        public int SourceItemTemplateId { get; set; }

        public List<DisjointMaterialResult> Materials { get; } = new List<DisjointMaterialResult>();

        public List<CommonInventoryItem> RefreshItems { get; } = new List<CommonInventoryItem>();
    }

    public sealed class DisjointMaterialResult
    {
        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int Count { get; set; }
    }
}
