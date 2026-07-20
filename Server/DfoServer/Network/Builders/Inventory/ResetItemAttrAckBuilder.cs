using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Builders
{
    public static class ResetItemAttrAckBuilder
    {
        // The generic CMD layer consumes the status byte first. The 0x0051
        // parser then reads the target item locator: itemId:int32,
        // listType:byte and targetSlot:int32.
        public const int SuccessLength = 10;
        public const int ErrorLength = 2;

        public static byte[] BuildSuccess(ResetItemAttrResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            return Build(result.TargetItemTemplateId, result.TargetListType, result.TargetSlotIndex);
        }

        public static byte[] Build(
            int targetItemTemplateId,
            InventoryListType listType,
            short targetSlotIndex)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt32(targetItemTemplateId);
            writer.WriteByte((byte)listType);
            writer.WriteInt32(targetSlotIndex);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode)
        {
            // Match the neighboring equipment commands: a one-byte failure
            // flag followed by the server error code.
            return new byte[] { 0x00, errorCode };
        }
    }
}
