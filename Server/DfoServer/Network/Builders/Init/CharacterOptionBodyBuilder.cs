using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class CharacterOptionBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0187;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var saved = snapshot.InitializationSnapshot.CharacterOptionBlob;
            if (saved != null)
            {
                body = new byte[saved.Length];
                Buffer.BlockCopy(saved, 0, body, 0, saved.Length);
                return true;
            }

            var legacyBitmap = snapshot.InitializationSnapshot.ServerEventPhaseBitmap ?? Array.Empty<byte>();
            var writer = new GamePacketWriter();
            writer.WriteInt32(legacyBitmap.Length);
            writer.WriteBytes(legacyBitmap);
            body = writer.ToArray();
            return true;
        }
    }
}
