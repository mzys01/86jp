using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Quests;
using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public sealed class QuestListBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0015;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            var character = snapshot.CharacterRecord;
            int level = (character != null) ? character.Level : 1;
            int job = (character != null) ? character.Job : 0;
            int growType = (character != null) ? character.GrowType : -1;

            var clearedSet = new HashSet<int>();
            var clearedFlags = new Dictionary<int, int>();
            foreach (var entry in init.CharacInvisibleFalgs)
            {
                if (entry.FlagValue != 0)
                {
                    clearedSet.Add(entry.SlotIndex);
                    clearedFlags[entry.SlotIndex] = entry.FlagValue;
                }
            }

            var questIds = GameWorld.QuestData.ComputeAcceptableQuests(level, job, growType, clearedSet, clearedFlags);

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)level);
            writer.WriteUInt16((ushort)questIds.Count);
            foreach (var questId in questIds)
                writer.WriteUInt16(questId);
            body = writer.ToArray();
            return true;
        }
    }
}
