using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Mercenary
{
    // 支援兵协议优先使用支援角色已学习等级；未记录时才按 PVF 需求等级兜底。
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

            var learned = LoadLearnedLevels(characterId);
            if (learned.TryGetValue(skillId, out var level) && level > 0)
                return level;

            var support = LoadCharacterSummary(characterId);
            if (support == null)
                return 0;

            var skill = StrikerSkillDataProvider.FindBySkill(support.Job, support.GrowType, skillId, strikerSkillId);
            return ClampLevel(skill?.RequiredLevel ?? 1);
        }

        public static byte ResolveBaseLevel(
            IReadOnlyDictionary<ushort, byte> learnedLevels,
            StrikerSkillEntry skill)
        {
            if (skill == null)
                return 0;

            if (learnedLevels != null
                && learnedLevels.TryGetValue((ushort)skill.SkillIndex, out var learnedLevel)
                && learnedLevel > 0)
            {
                return learnedLevel;
            }

            return ClampLevel(skill.RequiredLevel > 0 ? skill.RequiredLevel : 1);
        }

        private static byte ClampLevel(int level)
        {
            return (byte)Math.Max(1, Math.Min(byte.MaxValue, level));
        }

        private static CharacterSummary LoadCharacterSummary(int characterId)
        {
            try
            {
                var repo = new SqliteCharacterRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var character = repo.GetById(characterId);
                if (character == null)
                    return null;

                return new CharacterSummary
                {
                    Job = character.Job,
                    GrowType = character.GrowType,
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[StrikerSupport] load character summary failed cid={characterId}: {ex.Message}");
                return null;
            }
        }

        private sealed class CharacterSummary
        {
            public int Job { get; set; }
            public int GrowType { get; set; }
        }
    }
}
