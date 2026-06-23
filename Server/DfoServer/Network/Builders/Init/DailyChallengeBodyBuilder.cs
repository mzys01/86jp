using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class DailyChallengeBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0286;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            var groups = init.RacingDungeonGroups;
            var groupFlags = init.RacingDungeonGroupFlags ?? new byte[0];
            var tailIds = init.RacingDungeonTailIds;

            var groupCount = groups?.Count ?? 0;
            var totalEntries = 0;
            for (var i = 0; i < groupCount; i++)
                totalEntries += groups[i].Entries.Count;
            var tailIdCount = tailIds?.Count ?? 0;

            var size = 4 + 4 + groupCount * 8 + totalEntries * 12 + 4 + groupFlags.Length + 4 + tailIdCount * 4;
            body = new byte[size];

            var offset = 0;
            Buffer.BlockCopy(BitConverter.GetBytes(init.RacingDungeonCurrentEnterCount), 0, body, offset, 4);
            offset += 4;
            Buffer.BlockCopy(BitConverter.GetBytes((uint)groupCount), 0, body, offset, 4);
            offset += 4;

            for (var i = 0; i < groupCount; i++)
            {
                var group = groups[i];
                Buffer.BlockCopy(BitConverter.GetBytes(group.GroupId), 0, body, offset, 4);
                offset += 4;
                Buffer.BlockCopy(BitConverter.GetBytes((uint)group.Entries.Count), 0, body, offset, 4);
                offset += 4;
                for (var j = 0; j < group.Entries.Count; j++)
                {
                    var entry = group.Entries[j];
                    Buffer.BlockCopy(BitConverter.GetBytes(entry.TrackLikeId), 0, body, offset, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(entry.ValueA), 0, body, offset + 4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(entry.ValueB), 0, body, offset + 8, 4);
                    offset += 12;
                }
            }

            Buffer.BlockCopy(BitConverter.GetBytes((uint)groupFlags.Length), 0, body, offset, 4);
            offset += 4;
            Buffer.BlockCopy(groupFlags, 0, body, offset, groupFlags.Length);
            offset += groupFlags.Length;

            Buffer.BlockCopy(BitConverter.GetBytes((uint)tailIdCount), 0, body, offset, 4);
            offset += 4;
            for (var i = 0; i < tailIdCount; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(tailIds[i]), 0, body, offset, 4);
                offset += 4;
            }

            return true;
        }
    }
}
