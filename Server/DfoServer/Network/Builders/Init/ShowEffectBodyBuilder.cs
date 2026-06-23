using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class ShowEffectBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x017B;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var effects = snapshot.InitializationSnapshot.ShowEffects;
            var count = effects?.Count ?? 0;
            body = new byte[1 + count * 5];
            body[0] = (byte)count;
            for (var i = 0; i < count; i++)
            {
                var off = 1 + i * 5;
                body[off] = effects[i].EffectIndex;
                Buffer.BlockCopy(BitConverter.GetBytes(effects[i].DurationSeconds), 0, body, off + 1, 4);
            }
            return true;
        }
    }
}
