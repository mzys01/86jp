using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class TeleportPacketBuilder
    {
        public static byte[] BuildItemListUpdate(short type, int itemCode, int itemCount)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(0x00);
            writer.WriteInt16(0x0001);
            writer.WriteInt16(type);
            writer.WriteInt32(itemCode);
            writer.WriteInt32(itemCount);
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