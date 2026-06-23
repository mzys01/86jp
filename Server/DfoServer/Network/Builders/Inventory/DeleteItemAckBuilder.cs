using DfoServer.Game.Inventory;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class DeleteItemAckBuilder
    {
        public static byte[] Build(InventoryMutationResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte((byte)result.ListType);
            writer.WriteByte(0x01);
            writer.WriteInt16(result.SlotIndex);
            writer.WriteInt32(result.RemainingStackCount);
            writer.WriteInt16(result.AppliedCount);
            return writer.ToArray();
        }
    }
}
