using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    
    
    public sealed class LotteryBufferBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x03D8;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            
            
            var src = snapshot.InitializationSnapshot.LotteryBufferBlob;
            var writer = new Network.GamePacketWriter();
            for (int i = 0; i < 204; i++)
                writer.WriteByte(src != null && i < src.Length ? src[i] : (byte)0);
            body = writer.ToArray();
            return true;
        }
    }
}
