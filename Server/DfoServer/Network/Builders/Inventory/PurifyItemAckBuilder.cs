using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    public static class PurifyItemAckBuilder
    {
        public static byte[] BuildSuccess(PurifyItemResult result)
        {
            return new[] { (byte)0x01 };
        }

        public static byte[] BuildError(byte errorCode)
        {
            return new[] { (byte)0x00, errorCode };
        }
    }
}
