using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class TeleportPacketBuilder
    {
        public static byte[] BuildItemListUpdate(short type, int itemCode, int itemCount)
        {
            var writer = new GamePacketWriter();
            var normalizedItemCode = itemCount > 0 || itemCode <= 0 ? itemCode : -1;
            var normalizedItemCount = itemCount > 0 ? itemCount : 0;

            writer.WriteByte(0x00);
            writer.WriteInt16(0x0001);
            writer.WriteInt16(type);
            writer.WriteInt32(normalizedItemCode);
            writer.WriteInt32(normalizedItemCount);
            writer.WriteZeroBytes(0x4A);
            return writer.ToArray();
        }

        public static byte[] BuildTeleportResponse(short type, int itemCode)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(0x01);
            writer.WriteInt16(type);
            writer.WriteInt32(itemCode);
            return writer.ToArray();
        }
    }
}
