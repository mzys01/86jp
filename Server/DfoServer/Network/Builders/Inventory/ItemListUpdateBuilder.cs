using DfoServer.Game.Inventory;
using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class ItemListUpdateBuilder
    {
        public static byte[] BuildCommonUpdates(IReadOnlyList<CommonInventoryItem> items)
        {
            var writer = new GamePacketWriter();

            // NOTI 14 的增量刷新头: 0 表示普通物品更新，随后是本次刷新的物品 entry 数。
            writer.WriteByte(0x00);
            writer.WriteUInt16((ushort)(items != null ? items.Count : 0));

            if (items != null)
            {
                foreach (var item in items)
                    ItemListPacketBuilder.WriteCommonEntry(writer, item);
            }

            return writer.ToArray();
        }

        public static byte[] BuildCompactCommonUpdates(IReadOnlyList<InventoryMutationResult> items)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(0x00);
            writer.WriteUInt16((ushort)(items != null ? items.Count : 0));

            if (items != null)
            {
                foreach (var item in items)
                {
                    writer.WriteInt16(item.SlotIndex);
                    writer.WriteInt32(item.ItemTemplateId);
                    writer.WriteInt32(item.RemainingStackCount);
                    writer.WriteZeroBytes(0x4A);
                }
            }

            return writer.ToArray();
        }

        public static byte[] BuildPetUpdates(IReadOnlyList<PetInventoryItem> items)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte((byte)InventoryListType.Pet);
            writer.WriteUInt16((ushort)(items != null ? items.Count : 0));

            if (items != null)
            {
                foreach (var item in items)
                    ItemListPacketBuilder.WritePetEntry(writer, item);
            }

            return writer.ToArray();
        }

        public static byte[] BuildAvatarUpdates(IReadOnlyList<AvatarInventoryItem> items)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte((byte)InventoryListType.Avatar);
            writer.WriteUInt16((ushort)(items != null ? items.Count : 0));

            if (items != null)
            {
                foreach (var item in items)
                    ItemListPacketBuilder.WriteAvatarEntry(writer, item);
            }

            return writer.ToArray();
        }
    }
}
