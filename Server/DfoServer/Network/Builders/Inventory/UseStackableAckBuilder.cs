using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class UseStackableAckBuilder
    {
        public static byte[] BuildSuccess(short slotIndex, byte listType, int instanceValue, int itemCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(slotIndex);
            writer.WriteByte(listType);
            writer.WriteInt32(instanceValue);
            writer.WriteInt32(itemCode);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte listType, int itemCode, int instanceValue)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(listType);
            writer.WriteInt32(itemCode);
            writer.WriteInt32(instanceValue);
            return writer.ToArray();
        }
    }
}
