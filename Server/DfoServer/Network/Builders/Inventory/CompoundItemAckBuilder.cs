using DfoServer.Game.Inventory;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class CompoundItemAckBuilder
    {
        public static byte[] Build(CompoundItemRecipeResult result)
        {
            if (result == null || !result.Success)
            {
                return BuildError(
                    result != null && result.ErrorCode != 0 ? result.ErrorCode : (byte)17,
                    result != null ? result.SourceSlotIndex : (short)0);
            }

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt16(result.SourceSlotIndex >= 0 ? result.SourceSlotIndex : (short)0);
            writer.WriteByte((byte)InventoryListType.Main);
            writer.WriteInt32(result.RequestedCount);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode, short sourceSlotIndex = 0)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteInt16(sourceSlotIndex >= 0 ? sourceSlotIndex : (short)0);
            writer.WriteByte((byte)InventoryListType.Main);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }
    }
}
