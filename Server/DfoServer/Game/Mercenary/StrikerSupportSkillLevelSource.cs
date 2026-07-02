using DfoServer.Game.CharacterData;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Mercenary
{
    // 支援兵协议优先使用支援角色真实已学习等级；未学习或未记录时按协议保底为 1。
    internal static class StrikerSupportSkillLevelSource
    {
        public static Dictionary<ushort, byte> LoadLearnedLevels(int characterId)
        {
            var result = new Dictionary<ushort, byte>();
            try
            {
                var progressRepo = new SqliteCharacterProgressRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var snapshot = progressRepo.LoadSkills(characterId);
                if (snapshot == null)
                    return result;

                foreach (var page in snapshot.Pages)
                {
                    foreach (var entry in page.Entries)
                    {
                        if (entry.SkillId == 0 || entry.Level == 0)
                            continue;

                        if (!result.TryGetValue(entry.SkillId, out var existing) || entry.Level > existing)
                            result[entry.SkillId] = entry.Level;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[StrikerSupport] load learned skill levels failed cid={characterId}: {ex.Message}");
            }

            return result;
        }

        public static byte ResolveBaseLevel(int characterId, ushort skillId, ushort strikerSkillId)
        {
            if (characterId <= 0 || skillId == 0)
                return 0;

            return ResolveLearnedLevel(LoadLearnedLevels(characterId), skillId);
        }

        public static byte ResolveBaseLevel(
            IReadOnlyDictionary<ushort, byte> learnedLevels,
            StrikerSkillEntry skill)
        {
            if (skill == null)
                return 0;

            return ResolveLearnedLevel(learnedLevels, (ushort)skill.SkillIndex);
        }

        private static byte ResolveLearnedLevel(IReadOnlyDictionary<ushort, byte> learnedLevels, ushort skillId)
        {
            if (learnedLevels != null
                && learnedLevels.TryGetValue(skillId, out var learnedLevel)
                && learnedLevel > 0)
            {
                return learnedLevel;
            }

            return 1;
        }
    }
}
