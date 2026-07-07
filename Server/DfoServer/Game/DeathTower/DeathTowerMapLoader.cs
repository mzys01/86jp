using System;
using System.Collections.Generic;
using DfoServer.GameWorld;
using PvfLib;
using Dungeon = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Game.DeathTower
{
    // 从 PVF 地图文件加载当层怪物列表(构建 NOTI 143 怪物段)。
    // 读取 [monster] + [ai character] 标签, 按老服 consistMap 逻辑填充 StageMonster。
    public static class DeathTowerMapLoader
    {
        // 随机 APC 池的默认 APC 编号(AICharacter.lst 20002 = 阿拉德初阶圣职者)
        private const int FallbackApcCode = 20002;
        private const int ApcRandomListIndexStart = 64;

        public static List<StageMonster> LoadStageMonsters(DeathTowerSession tower)
        {
            var mapId = tower.GetCurrentMapId();
            var monsters = new List<StageMonster>();

            if (mapId <= 0)
            {
                FileLogger.Log($"[DeathTower] LoadStageMonsters: invalid mapId={mapId} for stage={tower.CurrentStage}");
                return monsters;
            }

            try
            {
                var mapContent = ReadMapContent(mapId);
                if (mapContent == null)
                {
                    FileLogger.Log($"[DeathTower] LoadStageMonsters: map file not found for mapId={mapId}");
                    return monsters;
                }

                var basisLevel = tower.Config.BasisLevel;
                int normalIndex = 0;
                int apcIndex = 0;

                // [monster] 标签: 普通怪物
                var monsterEntries = ParseMonsterTag(mapContent);
                foreach (var entry in monsterEntries)
                {
                    var level = entry.AutoLv != 0
                        ? basisLevel + entry.LevelOffset
                        : entry.LevelOffset;
                    if (level <= 0) level = 1;
                    if (level > 200) level = 200;

                    monsters.Add(new StageMonster
                    {
                        ListIndex = normalIndex++,
                        MonsterUniqueId = tower.NextMonsterSeq(),
                        MonsterIndex = entry.MonsterCode,
                        MonsterLevel = (byte)level,
                        MonsterType = 0,
                        IsBoxMonster = 0,
                        BoxIndex = 0,
                    });
                }

                // [ai character]: 固定 APC, type=5, 独立 apcIndex 计数(不与普通怪共用)
                var apcEntries = ParseAiCharacterTag(mapContent);
                foreach (var apc in apcEntries)
                {
                    monsters.Add(new StageMonster
                    {
                        ListIndex = apcIndex++,
                        MonsterUniqueId = tower.NextMonsterSeq(),
                        MonsterIndex = apc.CharacterId,
                        MonsterLevel = (byte)Math.Max(1, Math.Min(200, basisLevel)),
                        MonsterType = 5,
                        IsBoxMonster = 0,
                        BoxIndex = 0,
                    });
                }

                // [apc random point]: 随机 APC 池, type=5, ListIndex 从 64 开始(老服硬编码)
                if (monsters.Count == 0)
                {
                    var spawnCount = ParseApcRandomSpawnCount(mapContent);
                    var randomApcIdx = ApcRandomListIndexStart;
                    for (int s = 0; s < spawnCount; s++)
                    {
                        monsters.Add(new StageMonster
                        {
                            ListIndex = randomApcIdx++,
                            MonsterUniqueId = tower.NextMonsterSeq(),
                            MonsterIndex = FallbackApcCode,
                            MonsterLevel = (byte)Math.Max(1, Math.Min(200, basisLevel)),
                            MonsterType = 5,
                            IsBoxMonster = 0,
                            BoxIndex = 0,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DeathTower] LoadStageMonsters failed mapId={mapId}: {ex.Message}");
            }

            return monsters;
        }

        private static string ReadMapContent(int mapId)
        {
            var lstFile = LstFile.Parse(PvfArchiveAccessor.ReadText(System.IO.Path.Combine("map", "map.lst")));
            var entry = lstFile.GetById(mapId);
            if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                return null;
            return PvfArchiveAccessor.ReadText(System.IO.Path.Combine("map", entry.FilePath));
        }

        private struct MonsterEntry
        {
            public int MonsterCode;
            public int AutoLv;
            public int LevelOffset;
        }

        private struct ApcEntry
        {
            public int CharacterId;
        }

        private static List<MonsterEntry> ParseMonsterTag(string content)
        {
            var result = new List<MonsterEntry>();
            var tag = "[monster]";
            var idx = content.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return result;

            var endTag = "[/monster]";
            var endIdx = content.IndexOf(endTag, idx, StringComparison.OrdinalIgnoreCase);
            if (endIdx < 0) endIdx = content.Length;

            var section = content.Substring(idx + tag.Length, endIdx - idx - tag.Length);

            // PVF 格式: 所有怪物条目在一行内空格分隔, 每条 10 个 token:
            // monsterCode autoLv levelOffset x y z count spawnInterval `[spawnType]` `[aiType]`
            // 含反引号包裹的标签(如 `[fixed]`), 分割时去掉反引号
            var tokens = section.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            const int tokensPerEntry = 10;
            int i = 0;
            while (i + tokensPerEntry <= tokens.Length)
            {
                var codeStr = tokens[i].Trim('`', '[', ']');
                if (!int.TryParse(codeStr, out var code))
                {
                    i++;
                    continue;
                }

                int.TryParse(tokens[i + 1], out var autoLv);
                int.TryParse(tokens[i + 2], out var levelOffset);

                result.Add(new MonsterEntry
                {
                    MonsterCode = code,
                    AutoLv = autoLv,
                    LevelOffset = levelOffset,
                });

                i += tokensPerEntry;
            }
            return result;
        }

        private static int ParseApcRandomSpawnCount(string content)
        {
            // [monster spawn pos] 首 token = 怪物位置数量; 没有则默认 1
            var tag = "[monster spawn pos]";
            var idx = content.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 1;
            var after = content.Substring(idx + tag.Length, Math.Min(40, content.Length - idx - tag.Length));
            var tokens = after.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0 && int.TryParse(tokens[0], out var count) && count > 0)
                return count;
            return 1;
        }

        private static List<ApcEntry> ParseAiCharacterTag(string content)
        {
            var result = new List<ApcEntry>();
            var tag = "[ai character]";
            var idx = content.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return result;

            var endTag = "[/ai character]";
            var endIdx = content.IndexOf(endTag, idx, StringComparison.OrdinalIgnoreCase);
            if (endIdx < 0) endIdx = content.Length;

            var section = content.Substring(idx + tag.Length, endIdx - idx - tag.Length);

            // PVF 格式: 同一行内空格分隔, 每条 8 个 token:
            // characterId x y z `[type]` `[ai]` flag1 flag2
            var tokens = section.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            const int tokensPerEntry = 8;
            int i = 0;
            while (i + tokensPerEntry <= tokens.Length)
            {
                var idStr = tokens[i].Trim('`', '[', ']');
                if (int.TryParse(idStr, out var charId) && charId > 0)
                    result.Add(new ApcEntry { CharacterId = charId });
                i += tokensPerEntry;
            }

            // 如果 token 数不是 8 的整倍数但至少有 4 个(最小: id x y z), 尝试宽松解析
            if (result.Count == 0 && tokens.Length >= 4)
            {
                var idStr = tokens[0].Trim('`', '[', ']');
                if (int.TryParse(idStr, out var charId) && charId > 0)
                    result.Add(new ApcEntry { CharacterId = charId });
            }

            return result;
        }
    }
}
