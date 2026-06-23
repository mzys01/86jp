using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class TitleBookListBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0166;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var chunks = snapshot.InitializationSnapshot.AchievementChunks;
            if (occurrenceIndex < 0 || occurrenceIndex >= chunks.Count)
            {
                
                var w = new Network.GamePacketWriter();
                w.WriteByte(0); w.WriteUInt16(0);
                w.WriteInt32(occurrenceIndex); w.WriteInt32(0);
                body = w.ToArray();
                return true;
            }

            var chunk = chunks[occurrenceIndex];
            var writer = new GamePacketWriter();
            writer.WriteByte(chunk.ModeByte);
            writer.WriteUInt16(chunk.OwnerId16);
            writer.WriteInt32(chunk.ChunkIndex);
            writer.WriteInt32(chunk.Entries.Count);
            foreach (var entry in chunk.Entries)
            {
                writer.WriteUInt16(entry.AchievementId);
                writer.WriteInt32(entry.ValueA);
                writer.WriteInt32(entry.ValueB);
                writer.WriteByte(entry.CategoryByte);
                writer.WriteUInt16(entry.LinkId);
                writer.WriteByte(entry.Flag0);
                writer.WriteInt32(entry.ValueC);
                writer.WriteByte(entry.Flag1);
                writer.WriteByte(entry.Flag2);
                writer.WriteUInt16(entry.TailValue);
            }
            body = writer.ToArray();
            return true;
        }
    }
}
