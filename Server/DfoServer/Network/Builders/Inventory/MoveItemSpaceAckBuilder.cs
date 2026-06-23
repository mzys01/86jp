using DfoServer.Game.Inventory;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class MoveItemSpaceAckBuilder
    {
        public static byte[] Build(InventoryMoveResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte((byte)result.SourceListType);
            writer.WriteInt16(result.SourceSlotIndex);
            writer.WriteInt32(result.MoveValue32);
            writer.WriteByte((byte)result.DestinationListType);
            writer.WriteInt16(result.DestinationSlotIndex);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode, byte srcListType, byte dstListType)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(errorCode);
            writer.WriteByte(srcListType);
            writer.WriteByte(dstListType);
            return writer.ToArray();
        }
    }
}