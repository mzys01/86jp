using DfoServer.Game.SelectCharacter;
using System;

namespace DfoServer.Network.Builders
{
    public sealed class MailboxBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0061;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            body = new byte[6];
            body[0] = init.LoadedMailCount;
            body[1] = init.MailboxMode;
            Array.Copy(BitConverter.GetBytes(init.NotLoadedMailCount), 0, body, 2, 2);
            Array.Copy(BitConverter.GetBytes(init.MailboxUnknownCountC), 0, body, 4, 2);
            return true;
        }
    }
}
