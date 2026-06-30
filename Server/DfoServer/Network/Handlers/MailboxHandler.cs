using DfoServer.Network.Builders;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class MailboxHandler
    {
        public Task HandleOpenMailbox(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, BuildEmptyMailboxAck()));
        }

        public Task HandleMailboxCommand(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, CommonPacketBodyBuilder.BuildSuccessAck()));
        }

        private static byte[] BuildEmptyMailboxAck()
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteUInt16(0);
            return writer.ToArray();
        }
    }
}
