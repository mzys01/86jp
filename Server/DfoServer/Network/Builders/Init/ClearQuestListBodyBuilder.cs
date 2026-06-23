using DfoServer.Game.SelectCharacter;
using System;

namespace DfoServer.Network.Builders
{
    public sealed class ClearQuestListBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0164;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            var payloadLen = init.CharacInvisibleFalgsPayloadLen;
            body = new byte[4 + payloadLen];
            Buffer.BlockCopy(BitConverter.GetBytes(payloadLen), 0, body, 0, 4);
            foreach (var entry in init.CharacInvisibleFalgs)
            {
                if (entry.SlotIndex < payloadLen)
                    body[4 + entry.SlotIndex] = entry.FlagValue;
            }
            return true;
        }
    }
}
