using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Builders
{
    public static class LotteryItemAckBuilder
    {
        public static byte[] BuildPhaseStart(short slotIndex, int previewItemTemplateId)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(slotIndex);
            writer.WriteUInt16(0);
            writer.WriteInt32(previewItemTemplateId);
            writer.WriteInt32(previewItemTemplateId);
            return writer.ToArray();
        }

        public static byte[] BuildPhaseStartWithoutPreview()
        {
            return BuildPhaseStart(-1, 0);
        }

        public static byte[] BuildCommonItemResult(
            short sourceSlotIndex,
            CommonInventoryItem rewardItem,
            int displayValue)
        {
            if (rewardItem == null || rewardItem.ItemTemplateId <= 0)
                return BuildError();

            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(sourceSlotIndex);
            writer.WriteInt16(rewardItem.SlotIndex);
            writer.WriteInt32(rewardItem.ItemTemplateId);
            writer.WriteInt32(displayValue);
            writer.WriteUInt16(rewardItem.Durability);
            writer.WriteByte(rewardItem.ExtData0);
            writer.WriteByte(ReadAmplifyType(rewardItem));
            writer.WriteUInt16(ReadAmplifyValue(rewardItem));

            // Native PacketBuf::put_packet(Inven_Item) appends only the
            // variable tail after this summary. The 84-byte inventory-list
            // entry uses a different layout and crashes the client here.
            WriteEmptyEquipmentSocketExtension(writer, rewardItem);
            WriteEmptyInvenItemTail(writer);
            return writer.ToArray();
        }

        public static byte[] BuildAvatarItemResult(
            short sourceSlotIndex,
            AvatarInventoryItem rewardItem)
        {
            if (rewardItem == null || rewardItem.AvatarItemId <= 0)
                return BuildError();

            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(sourceSlotIndex);
            ItemListPacketBuilder.WriteAvatarEntry(writer, rewardItem);
            return writer.ToArray();
        }

        public static byte[] BuildError()
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteInt16(-1);
            writer.WriteUInt16(0);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            return writer.ToArray();
        }

        private static byte ReadAmplifyType(CommonInventoryItem item)
        {
            var prefix = item.PrefixData0E ?? Array.Empty<byte>();
            return prefix.Length >= 6 ? prefix[5] : (byte)0;
        }

        private static ushort ReadAmplifyValue(CommonInventoryItem item)
        {
            var prefix = item.PrefixData0E ?? Array.Empty<byte>();
            return prefix.Length >= 8 ? BitConverter.ToUInt16(prefix, 6) : (ushort)0;
        }

        private static void WriteEmptyInvenItemTail(GamePacketWriter writer)
        {
            writer.WriteByte(0x00); // empty RandomOption packet
            writer.WriteByte(0x00); // upgrade separate
            writer.WriteByte(0x00); // trade restriction
        }

        private static void WriteEmptyEquipmentSocketExtension(
            GamePacketWriter writer,
            CommonInventoryItem item)
        {
            if (ItemMetadataResolver.Resolve(item.ItemTemplateId).IsStackable)
                return;

            writer.WriteByte(0xEF);
            writer.WriteInt32(25);
            writer.WriteZeroBytes(25);
        }
    }
}
