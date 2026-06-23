using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class ServerEventPhaseBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0187;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            
            
            var bitmap = snapshot.InitializationSnapshot.ServerEventPhaseBitmap ?? System.Array.Empty<byte>();
            var writer = new Network.GamePacketWriter();
            writer.WriteInt32(bitmap.Length);
            for (int i = 0; i < bitmap.Length; i++)
                writer.WriteByte(bitmap[i]);
            body = writer.ToArray();
            return true;
        }
    }
}
