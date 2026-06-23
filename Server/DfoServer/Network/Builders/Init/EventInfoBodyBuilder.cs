using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class EventInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x006C;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var entries = snapshot.InitializationSnapshot.EventInfoEntries;
            var count = entries?.Count ?? 0;
            body = new byte[2 + count * 14 + 1];
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)count), 0, body, 0, 2);
            for (var i = 0; i < count; i++)
            {
                var off = 2 + i * 14;
                Buffer.BlockCopy(BitConverter.GetBytes(entries[i].RepeatEventIndex), 0, body, off, 2);
                var data = entries[i].EventData;
                if (data != null)
                    Buffer.BlockCopy(data, 0, body, off + 2, Math.Min(data.Length, 12));
            }
            body[body.Length - 1] = snapshot.InitializationSnapshot.EventInfoTailByte;
            return true;
        }
    }
}
