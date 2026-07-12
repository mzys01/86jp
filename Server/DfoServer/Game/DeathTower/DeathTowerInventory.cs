using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.DeathTower
{
    public sealed class TowerInventoryItem
    {
        public short SlotIndex { get; internal set; }
        public int ItemId { get; internal set; }
        public int Count { get; internal set; }
        public int StackLimit { get; internal set; }
        public bool IsWaste { get; internal set; }
    }

    public sealed class TowerPickupResult
    {
        public ushort SceneSlot { get; internal set; }
        public short DestinationSlot { get; internal set; }
        public int ItemId { get; internal set; }
        public IReadOnlyList<short> ChangedSlots { get; internal set; } = Array.Empty<short>();
    }

    public sealed class TowerInventoryMutation
    {
        public short SlotIndex { get; internal set; }
        public int ItemId { get; internal set; }
        public int RemainingCount { get; internal set; }
        public IReadOnlyList<short> ChangedSlots { get; internal set; } = Array.Empty<short>();
    }

    public sealed class TowerInventoryMoveResult
    {
        public short SourceSlot { get; internal set; }
        public short DestinationSlot { get; internal set; }
        public int MoveValue32 { get; internal set; }
        public IReadOnlyList<short> ChangedSlots { get; internal set; } = Array.Empty<short>();
    }

    internal static class DeathTowerItemSlotPolicy
    {
        internal static bool IsWaste(ItemMetadata metadata)
            => string.Equals(GetTypeFamily(metadata), "waste", StringComparison.OrdinalIgnoreCase);

        internal static int ResolveStackLimit(ItemMetadata metadata)
        {
            if (metadata == null || !metadata.IsStackable)
                return 1;
            return metadata.StackLimit > 0 ? metadata.StackLimit : int.MaxValue;
        }

        internal static IReadOnlyList<short> GetAllocationOrder(ItemMetadata metadata)
        {
            var result = new List<short>();
            if (IsWaste(metadata))
            {
                AppendRange(result, 3, 8);
                AppendRange(result, 65, 120);
                return result;
            }

            GetSlotRange(metadata, out var start, out var end);
            AppendRange(result, start, end);
            return result;
        }

        internal static bool IsSlotAllowed(ItemMetadata metadata, short slot)
        {
            if (IsWaste(metadata))
                return (slot >= 3 && slot <= 8) || (slot >= 65 && slot <= 120);

            GetSlotRange(metadata, out var start, out var end);
            return slot >= start && slot <= end;
        }

        private static void GetSlotRange(ItemMetadata metadata, out short start, out short end)
        {
            if (metadata == null)
            {
                start = 65;
                end = 120;
                return;
            }
            metadata.GetSlotRange(out var resolvedStart, out var resolvedEnd);
            start = (short)resolvedStart;
            end = (short)resolvedEnd;
        }

        private static string GetTypeFamily(ItemMetadata metadata)
        {
            var tag = GetTypeTag(metadata);
            var separator = tag.IndexOfAny(new[] { ' ', '\t' });
            return separator > 0 ? tag.Substring(0, separator) : tag;
        }

        private static string GetTypeTag(ItemMetadata metadata)
        {
            var raw = (metadata?.StackableType ?? string.Empty).Replace("`", string.Empty).Trim();
            var start = raw.IndexOf('[');
            var end = start >= 0 ? raw.IndexOf(']', start + 1) : -1;
            return start >= 0 && end > start
                ? raw.Substring(start + 1, end - start - 1).Trim()
                : raw.Trim('[', ']', ' ');
        }

        private static void AppendRange(ICollection<short> result, int start, int end)
        {
            for (var slot = start; slot <= end; slot++)
                result.Add((short)slot);
        }
    }
}
