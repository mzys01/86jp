using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class ServiceNotificationBuilder
    {
        public static byte[] BuildAuctionService(byte serviceType, byte state)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(serviceType);
            writer.WriteByte(state);
            return writer.ToArray();
        }
    }
}