using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class MailboxBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0061;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            // All mailbox seed values are 0; 6B = loadedCount(1)+mode(1)+notLoaded(2)+unknown(2)
            body = new byte[6];
            return true;
        }
    }
}
