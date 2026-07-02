using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld
{
    public class Dungeon
    {
        private static LstFile LoadLstFile(string relativePath)
        {
            var content = PvfArchiveAccessor.ReadText(relativePath);
            return LstFile.Parse(content);
        }

        public static LstFile LoadDungeonLstFile()
        {
            return LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
        }

        private static string ResolveFilePath(LstFile lstFile, int id, string description)
        {
            var entry = lstFile.GetById(id);
            if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                throw new Exception($"未找到{description}编号{id}");

            return entry.FilePath.Replace('/', Path.DirectorySeparatorChar);
        }

        public struct MonsterSumInfo
        {
            public int Code { get; set; }

            public byte Level { get; set; }

            // START_MAP 对象类型。0..3 为怪物，5..8 为 APC/AICharacter，9 为特殊被动对象路径。
            public byte Type { get; set; }

            public bool IsBlocking { get; set; }

            // START_MAP 模板/波次字段。深渊隐藏行使用 map [hellparty] 的 order。
            public ushort TemplateOrder { get; set; }

            // START_MAP 运行序号。为空时按普通 monster/APC 计数自动生成。
            public int? PacketIndex { get; set; }

            // START_MAP 隐藏标记。0 为可见房间对象，1 为深渊隐藏模板行。
            public byte Flag0 { get; set; }

            // 深渊柱子挂接选择器。86 官方柱子路径消费 Flag1 == 0xFF 的 hidden row。
            public byte Flag1 { get; set; }

            // START_MAP 附加状态。当前深渊隐藏行保持 0。
            public int ExtraState { get; set; }

            // 是否为深渊柱子流程挂接的隐藏小队成员。为 true 时死亡走深渊专用掉落分支。
            public bool IsHellPartyActor { get; set; }

            // 深渊小队编号，对应 etc/hellparty.etc 的 [group index]。
            public int HellPartyGroupId { get; set; }

            // 深渊难度：1=A/非常困难，2=B/困难。
            public byte HellPartyDifficulty { get; set; }

            // [difficulty] 第 1 项，最终深渊装备奖励计算次数。
            public int HellRewardRollCount { get; set; }

            // monster/APC 脚本中的 [hell monster] 标记。为 true 时不触发最终装备奖励。
            public bool IsHellMonsterScript { get; set; }
        }

        public struct MazeSumInfo
        {
            public int Index { get; set; }

            public int X { get; set; }

            public int Y { get; set; }

            public List<MonsterSumInfo> Monsters { get; set; }
        }

        public sealed class HellPartyWaveInfo
        {
            public int GroupId { get; set; }
            public int Order { get; set; }
            public List<MonsterSumInfo> Monsters { get; set; } = new List<MonsterSumInfo>();
        }

        public sealed class HellPartyRoomInfo
        {
            public int MapId { get; set; } = -1;
            public int NormalMapId { get; set; } = -1;
            public int X { get; set; }
            public int Y { get; set; }
            public int PillarObjectCode { get; set; }
            public int SpawnX { get; set; }
            public int SpawnY { get; set; }
            public HellPartyDifficultyRule DifficultyRule { get; set; }
            public List<HellPartyWaveInfo> Waves { get; set; } = new List<HellPartyWaveInfo>();

            public bool Found => MapId > 0;
        }

        public static byte GetDungeonBasicLv(int dungeonId)
        {
            var dgnlst = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
            if (dgnlst == null)
                throw new Exception("未能成功解析地下城LST文件 dungeon/dungeon.lst");

            var dgnFilePath = ResolveFilePath(dgnlst, dungeonId, "地下城");

            var dngFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePath)));
            if (dngFile.Mazes == null || dngFile.Mazes.Count == 0)
                throw new Exception("未解析到迷宫信息");

            return (byte)dngFile.BasisLevel;
        }

        public static int GetDungeonMinimumRequiredLevel(int dungeonId)
        {
            try
            {
                var loaded = LoadDungeonFileWithPath(dungeonId);
                if (loaded.File.MinimumRequiredLevel > 0)
                    return loaded.File.MinimumRequiredLevel;

                return loaded.File.BasisLevel;
            }
            catch
            {
                return 0;
            }
        }

        public static int GetMaxDifficultyCount(int dungeonId)
        {
            try
            {
                var dgnlst = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
                var dgnFilePath = ResolveFilePath(dgnlst, dungeonId, "地下城");
                var dngFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePath)));
                if (dngFile.DifficultyLevel != null && dngFile.DifficultyLevel.Length > 0)
                {
                    int count = 0;
                    foreach (var v in dngFile.DifficultyLevel)
                        if (v != 0) count++;
                    return count;
                }
                if (dngFile.DesignateDungeonDifficulty != null && dngFile.DesignateDungeonDifficulty.Length > 0)
                    return 5;
                if (dngFile.Difficulty >= 0)
                    return 5;
                return 0;
            }
            catch { return 0; }
        }

        public static int GetChampionCount(int dungeonId, int difficulty, int mazeIndex, Random rng, out int[] namedMonsterCodes)
        {
            namedMonsterCodes = null;
            try
            {
                var dgnlst = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
                var dgnFilePath = ResolveFilePath(dgnlst, dungeonId, "地下城");
                var dngFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePath)));
                namedMonsterCodes = dngFile.NamedMonster;

                if (dngFile.Champion == null || dngFile.Champion.Length == 0)
                    return 0;

                int diffIdx = difficulty;
                if (diffIdx < 0) diffIdx = 0;
                if (diffIdx >= dngFile.Champion.Length) diffIdx = dngFile.Champion.Length - 1;
                int probBase = dngFile.Champion[diffIdx];

                int adjusted = probBase;
                switch (difficulty)
                {
                    case 1: adjusted = probBase * 150 / 100; break;
                    case 2: adjusted = probBase * 250 / 100; break;
                    case 3: adjusted = probBase * 500 / 100; break;
                }

                int mazeW = 4, mazeH = 5;
                if (dngFile.Mazes != null && mazeIndex >= 0 && mazeIndex < dngFile.Mazes.Count)
                {
                    var m = dngFile.Mazes[mazeIndex];
                    if (m.Width > 0) mazeW = m.Width;
                    if (m.Height > 0) mazeH = m.Height;
                }

                int area = mazeW * mazeH;
                return 100 * adjusted / area > rng.Next(100) ? 1 : 0;
            }
            catch { return 0; }
        }

        public static void PromoteChampions(List<MonsterSumInfo> monsters, int count, Random rng, int[] namedMonsterCodes = null)
        {
            if (count <= 0) return;

            var namedSet = namedMonsterCodes != null && namedMonsterCodes.Length > 0
                ? new HashSet<int>(namedMonsterCodes) : null;

            var normalIndices = new List<int>();
            for (int i = 0; i < monsters.Count; i++)
                if (monsters[i].Type == 0 && (namedSet == null || !namedSet.Contains(monsters[i].Code)))
                    normalIndices.Add(i);

            for (int i = 0; i < count && normalIndices.Count > 0; i++)
            {
                int pick = rng.Next(normalIndices.Count);
                int idx = normalIndices[pick];
                normalIndices.RemoveAt(pick);

                var m = monsters[idx];
                m.Type = 1;
                monsters[idx] = m;
            }
        }

        public static float GetExperienceWeight(int dungeonId)
        {
            try
            {
                var dgnlst = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
                if (dgnlst == null) return 1.0f;
                var dgnFilePath = ResolveFilePath(dgnlst, dungeonId, "地下城");
                var dngFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePath)));
                return dngFile.ExperienceIncreasingPoint >= 0 ? dngFile.ExperienceIncreasingPoint : 1.0f;
            }
            catch
            {
                return 1.0f;
            }
        }

        public static MazeInfo GetDungeonDefaultMaze(int dungeonId)
        {
            var dgnlst = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
            if (dgnlst == null)
                throw new Exception("未能成功解析地下城LST文件 dungeon/dungeon.lst");

            var dgnFilePath = ResolveFilePath(dgnlst, dungeonId, "地下城");

            var dngFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePath)));
            if (dngFile.Mazes == null || dngFile.Mazes.Count == 0)
                throw new Exception("未解析到迷宫信息");

            MazeInfo defaultMaze = null;
            foreach (var maze in dngFile.Mazes)
            {
                if (maze.QuestConnection == null)
                {
                    defaultMaze = maze;
                    break;
                }
            }

            if (defaultMaze == null)
            {
                defaultMaze = dngFile.Mazes[0];
            }

            return defaultMaze;
        }

        private static readonly Random _mazeRng = new Random();
        private static readonly Regex MapCoordinateFileNameRegex =
            new Regex(@"\((?<x>-?\d+)[,.](?<y>-?\d+)\)", RegexOptions.Compiled);
        private static readonly Lazy<Dictionary<int, bool>> _monsterHellFlags =
            new Lazy<Dictionary<int, bool>>(() => LoadHellMonsterFlags("monster/monster.lst", "monster"));
        private static readonly Lazy<Dictionary<int, bool>> _aiCharacterHellFlags =
            new Lazy<Dictionary<int, bool>>(() => LoadHellMonsterFlags("AICharacter/AICharacter.lst", "AICharacter"));
        private static readonly object _namedMonsterCacheLock = new object();
        private static readonly Dictionary<int, HashSet<int>> _namedMonsterCache = new Dictionary<int, HashSet<int>>();

        public static bool IsNamedMonster(int dungeonId, int monsterCode)
        {
            if (dungeonId <= 0 || monsterCode <= 0)
                return false;

            HashSet<int> namedSet;
            lock (_namedMonsterCacheLock)
            {
                if (!_namedMonsterCache.TryGetValue(dungeonId, out namedSet))
                {
                    namedSet = new HashSet<int>();
                    try
                    {
                        var loaded = LoadDungeonFileWithPath(dungeonId);
                        if (loaded.File.NamedMonster != null)
                        {
                            foreach (var code in loaded.File.NamedMonster)
                                if (code > 0) namedSet.Add(code);
                        }
                    }
                    catch { }

                    _namedMonsterCache[dungeonId] = namedSet;
                }
            }

            return namedSet.Contains(monsterCode);
        }

        public static int[] RandomizeBossPosition(int[] bossMap)
        {
            if (bossMap == null || bossMap.Length < 2) return null;
            int pairCount = bossMap.Length / 2;
            if (pairCount <= 1) return new[] { bossMap[0], bossMap[1] };
            int pick = _mazeRng.Next(pairCount);
            return new[] { bossMap[pick * 2], bossMap[pick * 2 + 1] };
        }

        public static (MazeInfo Maze, int Index) SelectDungeonMaze(
            int dungeonId,
            ICollection<int> activeQuestIds = null,
            ICollection<int> relatedQuestIds = null)
        {
            var dgnlst = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
            if (dgnlst == null)
                throw new Exception("未能成功解析地下城LST文件 dungeon/dungeon.lst");

            var dgnFilePath = ResolveFilePath(dgnlst, dungeonId, "地下城");
            var dngFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePath)));
            if (dngFile.Mazes == null || dngFile.Mazes.Count == 0)
                throw new Exception("未解析到迷宫信息");

            var questMazeIndex = FindQuestConnectedMazeIndex(
                dngFile.Mazes,
                activeQuestIds,
                relatedQuestIds,
                out var matchedQuestId,
                out var matchSource);
            if (questMazeIndex >= 0)
            {
                FileLogger.Log($"[Dungeon] SelectMaze: dungeon={dungeonId} matched quest maze #{questMazeIndex} (questId={matchedQuestId} source={matchSource})");
                return (dngFile.Mazes[questMazeIndex], questMazeIndex);
            }

            var candidates = new List<(MazeInfo maze, int index)>();
            for (int i = 0; i < dngFile.Mazes.Count; i++)
            {
                if (dngFile.Mazes[i].QuestConnection == null)
                    candidates.Add((dngFile.Mazes[i], i));
            }

            if (candidates.Count == 0)
                return (dngFile.Mazes[0], 0);

            var pick = candidates[_mazeRng.Next(candidates.Count)];
            return (pick.maze, pick.index);
        }

        internal static int FindQuestConnectedMazeIndex(
            IReadOnlyList<MazeInfo> mazes,
            ICollection<int> primaryQuestIds,
            ICollection<int> fallbackQuestIds,
            out int matchedQuestId,
            out string matchSource)
        {
            matchedQuestId = -1;
            matchSource = string.Empty;

            var primaryMatch = FindQuestConnectedMazeIndex(mazes, primaryQuestIds, out matchedQuestId);
            if (primaryMatch >= 0)
            {
                matchSource = "active";
                return primaryMatch;
            }

            var fallbackMatch = FindQuestConnectedMazeIndex(mazes, fallbackQuestIds, out matchedQuestId);
            if (fallbackMatch >= 0)
            {
                matchSource = "related";
                return fallbackMatch;
            }

            matchedQuestId = -1;
            return -1;
        }

        private static int FindQuestConnectedMazeIndex(
            IReadOnlyList<MazeInfo> mazes,
            ICollection<int> questIds,
            out int matchedQuestId)
        {
            matchedQuestId = -1;
            if (mazes == null || questIds == null || questIds.Count == 0)
                return -1;

            for (int i = 0; i < mazes.Count; i++)
            {
                var qc = mazes[i].QuestConnection;
                if (qc == null || qc.Length < 2)
                    continue;

                if (!questIds.Contains(qc[1]))
                    continue;

                matchedQuestId = qc[1];
                return i;
            }

            return -1;
        }

        public static int[] GetLayeredMapIds(int dungeonId, int x, int y, int mazeIndex)
        {
            var dgnlst = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
            var dgnFilePath = ResolveFilePath(dgnlst, dungeonId, "地下城");
            var dngFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePath)));
            if (dngFile.Mazes == null || dngFile.Mazes.Count == 0)
                return null;
            var maze = (mazeIndex >= 0 && mazeIndex < dngFile.Mazes.Count) ? dngFile.Mazes[mazeIndex] : dngFile.Mazes[0];
            if (maze.MapSpecifications == null) return null;
            foreach (var spec in maze.MapSpecifications)
            {
                if (spec.Type == "layered" && spec.X == x && spec.Y == y && spec.LayeredMapIds != null)
                    return spec.LayeredMapIds;
            }
            return null;
        }

        public static bool IsHellDungeon(int dungeonId)
        {
            try
            {
                var area = WorldMap.GetAreaByDungeonId(dungeonId);
                if (area != null)
                    return area.HellDungeon;

                var loaded = LoadDungeonFileWithPath(dungeonId);
                return loaded.File.GetIntValue("hell dungeon", 0) == 1;
            }
            catch
            {
                return false;
            }
        }

        public static int FindHellMapIdForRoom(int dungeonId, int x, int y, int mazeIndex)
        {
            try
            {
                var loaded = LoadDungeonFileWithPath(dungeonId);
                var dungeonFile = loaded.File;
                if (dungeonFile.Mazes == null || dungeonFile.Mazes.Count == 0)
                    return -1;

                var maze = (mazeIndex >= 0 && mazeIndex < dungeonFile.Mazes.Count)
                    ? dungeonFile.Mazes[mazeIndex]
                    : dungeonFile.Mazes[0];

                var maplst = LoadLstFile(Path.Combine("map", "map.lst"));
                var mapDirCandidates = BuildMapDirCandidates(maplst, maze, loaded.FilePath);

                foreach (var entry in maplst.Entries)
                {
                    if (!IsInCandidateDir(entry.FilePath, mapDirCandidates))
                        continue;

                    var fileName = System.IO.Path.GetFileName(entry.FilePath);
                    if (string.IsNullOrEmpty(fileName)
                        || !fileName.StartsWith("hell_", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (fileName.IndexOf($"({x},{y})", StringComparison.OrdinalIgnoreCase) >= 0
                        || fileName.IndexOf($"({x}.{y})", StringComparison.OrdinalIgnoreCase) >= 0)
                        return entry.Id;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] FindHellMapIdForRoom ERROR: dungeon={dungeonId} room=({x},{y}) {ex.Message}");
            }

            return -1;
        }

        public static HellPartyRoomInfo FindHellMapRoom(int dungeonId, MazeInfo maze, int mazeIndex, byte difficulty)
        {
            if (maze?.MapSpecifications == null)
                return new HellPartyRoomInfo();

            if (maze.SealDoorMapIndex > 0
                && maze.SealDoorPos != null
                && maze.SealDoorPos.Length >= 2)
            {
                var sealX = maze.SealDoorPos[0];
                var sealY = maze.SealDoorPos[1];
                var normalMapId = FindNormalMapIdForRoom(maze, sealX, sealY);
                if (normalMapId > 0)
                {
                    FileLogger.Log($"[Dungeon] HellParty seal door: dungeon={dungeonId} room=({sealX},{sealY}) hellMap={maze.SealDoorMapIndex} normalMap={normalMapId}");
                    return BuildHellPartyRoomInfo(maze.SealDoorMapIndex, normalMapId, sealX, sealY, dungeonId, difficulty);
                }

                FileLogger.Log($"[Dungeon] HellParty seal door ignored: dungeon={dungeonId} room=({sealX},{sealY}) hellMap={maze.SealDoorMapIndex} normalMap missing");
            }

            foreach (var spec in maze.MapSpecifications)
            {
                var hellMapId = FindHellMapIdForRoom(dungeonId, spec.X, spec.Y, mazeIndex);
                if (hellMapId <= 0)
                    continue;

                return BuildHellPartyRoomInfo(hellMapId, spec.Index, spec.X, spec.Y, dungeonId, difficulty);
            }

            return new HellPartyRoomInfo();
        }

        private static int FindNormalMapIdForRoom(MazeInfo maze, int x, int y)
        {
            if (maze?.MapSpecifications == null)
                return -1;

            foreach (var spec in maze.MapSpecifications)
                if (spec.X == x && spec.Y == y && spec.Index > 0)
                    return spec.Index;

            return -1;
        }

        private static HellPartyRoomInfo BuildHellPartyRoomInfo(int mapId, int normalMapId, int x, int y, int dungeonId, byte difficulty)
        {
            try
            {
                var mapFile = LoadMapFile(mapId);
                SpecialPassiveObjectInfo pillar = null;
                foreach (var obj in mapFile.SpecialPassiveObjects)
                {
                    if (pillar == null)
                        pillar = obj;
                    if (obj.HellPartyEntries.Count > 0)
                    {
                        pillar = obj;
                        break;
                    }
                }

                return new HellPartyRoomInfo
                {
                    MapId = mapId,
                    NormalMapId = normalMapId,
                    X = x,
                    Y = y,
                    PillarObjectCode = pillar?.ObjectCode ?? 0,
                    SpawnX = pillar?.X ?? 0,
                    SpawnY = pillar?.Y ?? 0,
                    DifficultyRule = HellPartyData.GetDifficultyRule(difficulty),
                    Waves = BuildHellPartyWaves(mapFile, dungeonId, difficulty),
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] BuildHellPartyRoomInfo ERROR: map={mapId} {ex.Message}");
                return new HellPartyRoomInfo();
            }
        }

        private static List<HellPartyWaveInfo> BuildHellPartyWaves(MapFile mapFile, int dungeonId, byte difficulty)
        {
            var result = new List<HellPartyWaveInfo>();
            var entriesByOrder = new SortedDictionary<int, List<HellPartyMapEntry>>();
            foreach (var obj in mapFile.SpecialPassiveObjects)
            {
                foreach (var entry in obj.HellPartyEntries)
                {
                    if (!entriesByOrder.TryGetValue(entry.Order, out var list))
                    {
                        list = new List<HellPartyMapEntry>();
                        entriesByOrder[entry.Order] = list;
                    }
                    list.Add(entry);
                }
            }

            foreach (var pair in entriesByOrder)
            {
                var candidates = new List<HellPartyMapEntry>();
                foreach (var entry in pair.Value)
                    if (HellPartyData.HasEntries(entry.GroupId, difficulty))
                        candidates.Add(entry);

                var selected = PickHellPartyEntry(candidates);
                if (selected == null)
                    continue;

                var monsters = BuildHellPartyMonsterInfos(selected.GroupId, dungeonId, difficulty);
                if (monsters.Count == 0)
                    continue;

                result.Add(new HellPartyWaveInfo
                {
                    GroupId = selected.GroupId,
                    Order = pair.Key,
                    Monsters = monsters,
                });

                FileLogger.Log($"[Dungeon] HellParty wave: order={pair.Key} group={selected.GroupId} mode={difficulty} monsters={monsters.Count}");
            }

            return result;
        }

        private static List<MonsterSumInfo> BuildHellPartyMonsterInfos(int groupId, int dungeonId, byte difficulty)
        {
            var result = new List<MonsterSumInfo>();
            var groupEntries = HellPartyData.GetEntries(groupId, difficulty);
            var difficultyRule = HellPartyData.GetDifficultyRule(difficulty);
            var rewardRollCount = Math.Max(0, difficultyRule?.RewardRollCount ?? 0);
            foreach (var groupEntry in groupEntries)
            {
                byte type;
                byte level;
                bool isHellMonsterScript;
                if (groupEntry.EntityType == 1)
                {
                    type = 5;
                    if (!TryGetAICharacterLevel(groupEntry.Code, out level))
                    {
                        FileLogger.Log($"[Dungeon] HellParty APC code={groupEntry.Code} not found in AICharacter.lst; fallback to dungeon level");
                        level = GetDungeonBasicLv(dungeonId);
                    }
                    isHellMonsterScript = IsAICharacterHellMonster(groupEntry.Code);
                }
                else
                {
                    type = 0;
                    level = GetDungeonBasicLv(dungeonId);
                    isHellMonsterScript = IsMonsterHellMonster(groupEntry.Code);
                }

                result.Add(new MonsterSumInfo
                {
                    Code = groupEntry.Code,
                    Level = level,
                    Type = type,
                    IsBlocking = true,
                    IsHellPartyActor = true,
                    HellPartyGroupId = groupId,
                    HellPartyDifficulty = difficulty,
                    HellRewardRollCount = rewardRollCount,
                    IsHellMonsterScript = isHellMonsterScript,
                });
            }

            return result;
        }

        private static (DungeonFile File, string FilePath) LoadDungeonFileWithPath(int dungeonId)
        {
            var dgnlst = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
            var dgnFilePath = ResolveFilePath(dgnlst, dungeonId, "dungeon");
            var dungeonFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePath)));
            return (dungeonFile, dgnFilePath);
        }

        private static MapFile LoadMapFile(int mapId)
        {
            var maplst = LoadLstFile(Path.Combine("map", "map.lst"));
            var mapFilePath = ResolveFilePath(maplst, mapId, "map");
            return MapFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("map", mapFilePath)));
        }

        private static List<string> BuildMapDirCandidates(LstFile maplst, MazeInfo maze, string dungeonFilePath)
        {
            var result = new List<string>();

            void AddDirCandidate(string dir)
            {
                if (string.IsNullOrEmpty(dir)) return;
                dir = dir.Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrEmpty(dir)) return;
                foreach (var existing in result)
                    if (string.Equals(existing, dir, StringComparison.OrdinalIgnoreCase)) return;
                result.Add(dir);
            }

            void AddMapId(int mapId)
            {
                var entry = maplst.GetById(mapId);
                if (entry != null && !string.IsNullOrEmpty(entry.FilePath))
                    AddDirCandidate(System.IO.Path.GetDirectoryName(entry.FilePath));
            }

            if (maze.MapSpecifications != null && maplst != null)
            {
                foreach (var spec in maze.MapSpecifications)
                {
                    AddMapId(spec.Index);
                    if (spec.MapCandidates != null)
                        foreach (var id in spec.MapCandidates)
                            AddMapId(id);
                    if (spec.LayeredMapIds != null)
                        foreach (var id in spec.LayeredMapIds)
                            AddMapId(id);
                }
            }

            var dgnDir = System.IO.Path.GetFileNameWithoutExtension(dungeonFilePath);
            AddDirCandidate(dgnDir);
            if (dgnDir != null && dgnDir.StartsWith("tutorial_", StringComparison.OrdinalIgnoreCase))
                AddDirCandidate(dgnDir.Substring("tutorial_".Length));

            if (maplst != null && !string.IsNullOrEmpty(dgnDir))
            {
                foreach (var entry in maplst.Entries)
                {
                    if (entry.FilePath == null) continue;
                    var fileName = System.IO.Path.GetFileName(entry.FilePath);
                    if (fileName != null && fileName.StartsWith(dgnDir, StringComparison.OrdinalIgnoreCase))
                        AddDirCandidate(System.IO.Path.GetDirectoryName(entry.FilePath));
                }
            }

            return result;
        }

        private static bool IsInCandidateDir(string filePath, List<string> candidates)
        {
            if (filePath == null) return false;
            foreach (var dir in candidates)
            {
                if (filePath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)
                    || filePath.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static HellPartyMapEntry PickHellPartyEntry(List<HellPartyMapEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return null;

            var total = 0;
            foreach (var entry in entries)
                if (entry.Rate > 0)
                    total += entry.Rate;

            if (total <= 0)
                return entries[0];

            var roll = _mazeRng.Next(total);
            foreach (var entry in entries)
            {
                if (entry.Rate <= 0)
                    continue;
                if (roll < entry.Rate)
                    return entry;
                roll -= entry.Rate;
            }

            return entries[0];
        }

        private static bool IsMonsterHellMonster(int monsterCode)
        {
            return _monsterHellFlags.Value.TryGetValue(monsterCode, out var value) && value;
        }

        private static bool IsAICharacterHellMonster(int aiCharacterCode)
        {
            return _aiCharacterHellFlags.Value.TryGetValue(aiCharacterCode, out var value) && value;
        }

        private static Dictionary<int, bool> LoadHellMonsterFlags(string lstPath, string baseDir)
        {
            var result = new Dictionary<int, bool>();
            try
            {
                var lst = LstFile.Parse(PvfArchiveAccessor.ReadText(lstPath));
                foreach (var entry in lst.Entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath))
                        continue;

                    string content;
                    try { content = PvfArchiveAccessor.ReadText(Path.Combine(baseDir, entry.FilePath)); }
                    catch { continue; }

                    result[entry.Id] = ParseHellMonsterFlag(content);
                }
                FileLogger.Log($"[Dungeon] HellMonster flags loaded: {baseDir} count={result.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] HellMonster flags load failed: {lstPath} {ex.Message}");
            }
            return result;
        }

        private static bool ParseHellMonsterFlag(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            var match = Regex.Match(content, @"\[hell monster\]\s*([+-]?\d+)", RegexOptions.IgnoreCase);
            return match.Success
                && int.TryParse(match.Groups[1].Value, out var value)
                && value == 1;
        }

        public static MazeSumInfo GetDungeonMapMonsterSummaryInformation(int dungeonId, int x, int y, int mazeIndex = -1, int overrideMapId = -1, int[] bossPos = null)
        {
            if (dungeonId == 5000)
            {
                return new MazeSumInfo
                {
                    X = 0,
                    Y = 0,
                    Index = 36250,
                    Monsters = new List<MonsterSumInfo>(),
                };
            }

            byte dungeonBasicLv = GetDungeonBasicLv(dungeonId);

            MazeInfo defaultMaze;
            if (mazeIndex >= 0)
            {
                var dgnlstM = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
                var dgnFilePathM = ResolveFilePath(dgnlstM, dungeonId, "地下城");
                var dngFileM = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePathM)));
                defaultMaze = (mazeIndex < dngFileM.Mazes.Count) ? dngFileM.Mazes[mazeIndex] : GetDungeonDefaultMaze(dungeonId);
            }
            else
            {
                defaultMaze = GetDungeonDefaultMaze(dungeonId);
            }
            if (x == 0xFF && y == 0xFF)
            {
                x = defaultMaze.StartMap[0];
                y = defaultMaze.StartMap[1];
            }

            if (overrideMapId > 0)
            {
                var maplstO = LoadLstFile(Path.Combine("map", "map.lst"));
                var mapFilePathO = ResolveFilePath(maplstO, overrideMapId, "门");
                var mapFileO = MapFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("map", mapFilePathO)));
                var listO = new List<MonsterSumInfo>();
                foreach (var item in mapFileO.Monsters)
                {
                    if (!item.MonsterId.HasValue || item.MonsterId.Value <= 0)
                    {
                        FileLogger.Log($"[Dungeon] GetDungeonMapMonsterSummaryInformation: skip monster with invalid id in override map={overrideMapId} dungeon={dungeonId}");
                        continue;
                    }
                    var monsterType = (byte)item.Type;
                    if (monsterType > 3)
                    {
                        FileLogger.Log($"[Dungeon] GetDungeonMapMonsterSummaryInformation: clamp monster type {monsterType} to 0 in override map={overrideMapId} dungeon={dungeonId}");
                        monsterType = 0;
                    }
                    int rawMonsterLevel = item.Lv.GetValueOrDefault() != 0
                        ? dungeonBasicLv + item.AutoLv.GetValueOrDefault()
                        : item.AutoLv.GetValueOrDefault();
                    byte monsterLevel = rawMonsterLevel > 0 ? (byte)Math.Min(rawMonsterLevel, 255) : dungeonBasicLv;
                    listO.Add(new MonsterSumInfo
                    {
                        Code = item.MonsterId.Value,
                        Type = monsterType,
                        Level = monsterLevel,
                        IsBlocking = true,
                    });
                }
                foreach (var apc in mapFileO.AICharacters)
                {
                    if (apc.Code <= 0 || !TryGetAICharacterLevel(apc.Code, out var apcLevel))
                    {
                        FileLogger.Log($"[Dungeon] GetDungeonMapMonsterSummaryInformation: skip APC code={apc.Code} not found in override map={overrideMapId} dungeon={dungeonId}");
                        continue;
                    }
                    var apcType = (byte)apc.AIType;
                    if (apcType < 5 || apcType > 8)
                    {
                        FileLogger.Log($"[Dungeon] GetDungeonMapMonsterSummaryInformation: clamp APC type {apcType} to 5 in override map={overrideMapId} dungeon={dungeonId}");
                        apcType = 5;
                    }
                    listO.Add(new MonsterSumInfo
                    {
                        Code = apc.Code,
                        Type = apcType,
                        Level = apcLevel,
                        IsBlocking = false,
                    });
                }
                return new MazeSumInfo { Monsters = listO, X = x, Y = y, Index = overrideMapId };
            }

            var maplst = LoadLstFile(Path.Combine("map", "map.lst"));
            var dgnlstForDir = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
            var dgnFilePath2 = ResolveFilePath(dgnlstForDir, dungeonId, "地下城");
            var dgnDir = System.IO.Path.GetFileNameWithoutExtension(dgnFilePath2);

            var mapDirCandidates = new List<string>();
            void AddDirCandidate(string dir)
            {
                if (string.IsNullOrEmpty(dir)) return;
                dir = dir.Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrEmpty(dir)) return;
                foreach (var d in mapDirCandidates)
                    if (string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)) return;
                mapDirCandidates.Add(dir);
            }
            if (defaultMaze.MapSpecifications != null && maplst != null)
            {
                foreach (var spec in defaultMaze.MapSpecifications)
                {
                    var entry = maplst.GetById(spec.Index);
                    if (entry != null && !string.IsNullOrEmpty(entry.FilePath))
                    {
                        var dirPart = System.IO.Path.GetDirectoryName(entry.FilePath);
                        AddDirCandidate(dirPart);
                    }
                }
            }
            AddDirCandidate(dgnDir);
            if (dgnDir != null && dgnDir.StartsWith("tutorial_", StringComparison.OrdinalIgnoreCase))
                AddDirCandidate(dgnDir.Substring("tutorial_".Length));
            if (maplst != null && !string.IsNullOrEmpty(dgnDir))
            {
                foreach (var entry in maplst.Entries)
                {
                    if (entry.FilePath == null) continue;
                    var fn = System.IO.Path.GetFileName(entry.FilePath);
                    if (fn != null && fn.StartsWith(dgnDir, StringComparison.OrdinalIgnoreCase))
                    {
                        AddDirCandidate(System.IO.Path.GetDirectoryName(entry.FilePath));
                    }
                }
            }
            if (mapDirCandidates.Count == 0) AddDirCandidate(dgnDir);

            var mapId = -1;

            bool isStartRoom = defaultMaze.StartMap != null && defaultMaze.StartMap.Length >= 2
                               && defaultMaze.StartMap[0] == x && defaultMaze.StartMap[1] == y;
            var effectiveBoss = bossPos ?? (defaultMaze.BossMap != null && defaultMaze.BossMap.Length >= 2
                ? new[] { defaultMaze.BossMap[0], defaultMaze.BossMap[1] } : null);
            bool isBossRoom = effectiveBoss != null && effectiveBoss[0] == x && effectiveBoss[1] == y;
            bool IsQuestVariantFile(string fileName)
            {
                return IsQuestVariantFileName(fileName);
            }
            bool InCandidateDir(string filePath)
            {
                if (filePath == null) return false;
                foreach (var d in mapDirCandidates)
                {
                    if (filePath.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase)
                        || filePath.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            int FindMapIdByFileName(string[] patterns)
            {
                if (maplst == null) return -1;
                foreach (var pat in patterns)
                {
                    foreach (var entry in maplst.Entries)
                    {
                        if (!InCandidateDir(entry.FilePath)) continue;
                        var fileName = System.IO.Path.GetFileName(entry.FilePath);
                        if (IsQuestVariantFile(fileName)) continue;
                        if (fileName.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                            return entry.Id;
                    }
                }
                return -1;
            }
            int FindMapIdByPrefixChar(char ch)
            {
                if (maplst == null) return -1;
                foreach (var entry in maplst.Entries)
                {
                    if (!InCandidateDir(entry.FilePath)) continue;
                    var fileName = System.IO.Path.GetFileName(entry.FilePath);
                    if (IsQuestVariantFile(fileName)) continue;
                    if (fileName.Length > 1
                        && char.ToLowerInvariant(fileName[0]) == char.ToLowerInvariant(ch)
                        && char.IsDigit(fileName[1]))
                        return entry.Id;
                }
                return -1;
            }
            int FindMapIdByDigitSuffix(char suffix)
            {
                if (maplst == null) return -1;
                foreach (var entry in maplst.Entries)
                {
                    if (!InCandidateDir(entry.FilePath)) continue;
                    var fileName = System.IO.Path.GetFileName(entry.FilePath);
                    if (IsQuestVariantFile(fileName)) continue;
                    var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
                    if (stem.Length < 2) continue;
                    if (stem[stem.Length - 1] != suffix) continue;
                    char prev = stem[stem.Length - 2];
                    if (!(char.IsDigit(prev) || prev == ')')) continue;
                    bool hasDigit = false;
                    for (int i = 0; i < stem.Length - 1; i++) if (char.IsDigit(stem[i])) { hasDigit = true; break; }
                    if (!hasDigit) continue;
                    return entry.Id;
                }
                return -1;
            }
            int FindMapIdByKeywordPrefix(string keyword)
            {
                if (maplst == null) return -1;
                foreach (var entry in maplst.Entries)
                {
                    if (!InCandidateDir(entry.FilePath)) continue;
                    var fileName = System.IO.Path.GetFileName(entry.FilePath);
                    if (IsQuestVariantFile(fileName)) continue;
                    var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
                    if (string.IsNullOrEmpty(stem)) continue;
                    if (stem.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                        return entry.Id;
                    if (stem.Length > keyword.Length + 1
                        && stem[stem.Length - keyword.Length - 1] == '_'
                        && string.Compare(stem, stem.Length - keyword.Length, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0)
                        return entry.Id;
                    var us = stem.IndexOf('_');
                    if (us > 0 && us < stem.Length - 1)
                    {
                        bool digitsOnly = true;
                        for (int i = 0; i < us; i++) if (!char.IsDigit(stem[i])) { digitsOnly = false; break; }
                        if (digitsOnly && string.Compare(stem, us + 1, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0)
                            return entry.Id;
                    }
                }
                return -1;
            }
            int FindNumericStemNeighbor(bool wantSmallest)
            {
                if (maplst == null) return -1;
                int chosen = -1;
                foreach (var entry in maplst.Entries)
                {
                    if (!InCandidateDir(entry.FilePath)) continue;
                    var fileName = System.IO.Path.GetFileName(entry.FilePath);
                    if (IsQuestVariantFile(fileName)) continue;
                    var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
                    if (string.IsNullOrEmpty(stem)) continue;
                    bool allDigit = true;
                    for (int i = 0; i < stem.Length; i++) if (!char.IsDigit(stem[i])) { allDigit = false; break; }
                    if (!allDigit) continue;
                    if (chosen == -1) { chosen = entry.Id; continue; }
                    if (wantSmallest) { if (entry.Id < chosen) chosen = entry.Id; }
                    else { if (entry.Id > chosen) chosen = entry.Id; }
                }
                return chosen;
            }
            int FindMapIdByDgnNamePrefix()
            {
                if (maplst == null || string.IsNullOrEmpty(dgnDir)) return -1;
                foreach (var entry in maplst.Entries)
                {
                    if (!InCandidateDir(entry.FilePath)) continue;
                    var fileName = System.IO.Path.GetFileName(entry.FilePath);
                    if (IsQuestVariantFile(fileName)) continue;
                    if (fileName.StartsWith(dgnDir, StringComparison.OrdinalIgnoreCase))
                        return entry.Id;
                }
                return -1;
            }
            var bossActorMapCache = new Dictionary<int, bool>();
            // Use parsed map content instead of map ids so duplicated BOSS coordinates work across PVFs.
            bool HasBossActor(int mapId)
            {
                if (maplst == null || mapId <= 0) return false;
                bool cached;
                if (bossActorMapCache.TryGetValue(mapId, out cached))
                    return cached;

                var found = false;
                try
                {
                    var mapFilePath = ResolveFilePath(maplst, mapId, "map");
                    var mapFile = MapFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("map", mapFilePath)));
                    foreach (var monster in mapFile.Monsters)
                    {
                        if (monster.MonsterId.GetValueOrDefault() > 0 && monster.Type == MonsterType.Boss)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        foreach (var apc in mapFile.AICharacters)
                        {
                            if (apc.Code > 0 && apc.AIType == ApcAIType.Boss)
                            {
                                found = true;
                                break;
                            }
                        }
                    }
                }
                catch { }
                bossActorMapCache[mapId] = found;
                return found;
            }
            int ChooseBossRoomMapId(List<int> bossActorMapIds, int[] originalCandidates)
            {
                if (bossActorMapIds != null && bossActorMapIds.Count > 0)
                {
                    return bossActorMapIds.Count > 1
                        ? bossActorMapIds[_mazeRng.Next(bossActorMapIds.Count)]
                        : bossActorMapIds[0];
                }

                if (originalCandidates == null || originalCandidates.Length == 0)
                    return -1;
                return originalCandidates.Length > 1
                    ? originalCandidates[_mazeRng.Next(originalCandidates.Length)]
                    : originalCandidates[0];
            }
            int FindMapIdByMapSpecification(bool allowMapTypeForBossRoom)
            {
                if (defaultMaze.MapSpecifications == null)
                    return -1;

                if (isBossRoom)
                {
                    var bossActorMapIds = new List<int>();
                    int[] originalCandidates = null;
                    for (var specIndex = 0; specIndex < defaultMaze.MapSpecifications.Count; specIndex++)
                    {
                        var item = defaultMaze.MapSpecifications[specIndex];
                        if (item.X != x || item.Y != y)
                            continue;
                        var specType = item.Type ?? string.Empty;
                        if (!string.Equals(specType, "boss", StringComparison.OrdinalIgnoreCase)
                            && !(allowMapTypeForBossRoom && string.Equals(specType, "map", StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var candidates = item.MapCandidates != null && item.MapCandidates.Length > 0
                            ? item.MapCandidates
                            : new[] { item.Index };
                        if (originalCandidates == null)
                            originalCandidates = candidates;
                        foreach (var candidate in candidates)
                        {
                            if (candidate > 0 && HasBossActor(candidate))
                                bossActorMapIds.Add(candidate);
                        }
                    }

                    // Some PVFs list an ordinary coordinate map before the actual BOSS variant
                    // for the same room. Prefer maps whose content declares a BOSS actor; otherwise
                    // keep the original first-match/random-candidate behavior.
                    return ChooseBossRoomMapId(bossActorMapIds, originalCandidates);
                }

                foreach (var item in defaultMaze.MapSpecifications)
                {
                    if (item.X != x || item.Y != y)
                        continue;
                    if (item.MapCandidates != null && item.MapCandidates.Length > 1)
                        return item.MapCandidates[_mazeRng.Next(item.MapCandidates.Length)];
                    return item.Index;
                }

                return -1;
            }

            var isQuestConnectedMaze = defaultMaze.QuestConnection != null
                && defaultMaze.QuestConnection.Length >= 2;

            if (isQuestConnectedMaze)
                mapId = FindMapIdByMapSpecification(allowMapTypeForBossRoom: false);

            if (mapId == -1 && isStartRoom)
            {
                mapId = FindMapIdByFileName(new[]
                {
                    $"({x},{y})_start", $"({x},{y})start",
                    $"({x}.{y})_start", $"({x}.{y})start",
                });
            }

            if (mapId == -1)
            {
                mapId = FindMapIdByMapSpecification(allowMapTypeForBossRoom: false);
            }

            if (isBossRoom && mapId == -1)
            {
                int bossVariant = FindMapIdByFileName(new[]
                {
                    $"({x},{y})_boss", $"({x},{y})boss",
                    $"({x}.{y})_boss", $"({x}.{y})boss",
                });
                if (bossVariant == -1)
                    bossVariant = FindMapIdByPrefixChar('b');
                if (bossVariant == -1)
                    bossVariant = FindMapIdByDigitSuffix('B');
                if (bossVariant == -1)
                    bossVariant = FindMapIdByKeywordPrefix("boss");
                if (bossVariant != -1)
                    mapId = bossVariant;
            }

            if (mapId == -1 && isBossRoom)
            {
                foreach (var item in defaultMaze.MapSpecifications)
                {
                    if (item.X == x && item.Y == y && item.Type == "map")
                    {
                        mapId = (item.MapCandidates != null && item.MapCandidates.Length > 1)
                            ? item.MapCandidates[_mazeRng.Next(item.MapCandidates.Length)]
                            : item.Index;
                        break;
                    }
                }
            }

            if (mapId == -1 && isBossRoom)
            {
                mapId = FindNumericStemNeighbor(wantSmallest: false);
            }

            if (mapId == -1 && isStartRoom)
            {
                mapId = FindMapIdByPrefixChar('s');
                if (mapId == -1)
                    mapId = FindMapIdByDigitSuffix('S');
                if (mapId == -1)
                    mapId = FindMapIdByKeywordPrefix("start");
                if (mapId == -1)
                    mapId = FindNumericStemNeighbor(wantSmallest: true);
            }

            if (mapId == -1)
            {
                mapId = FindMapIdByFileName(new[] { $"({x},{y})", $"({x}.{y})" });
            }

            if (mapId == -1 && (isStartRoom || isBossRoom))
            {
                mapId = FindMapIdByDgnNamePrefix();
            }

            if (mapId == -1)
            {
                var preferQuestVariantFallback = isStartRoom
                    || (defaultMaze.QuestConnection != null && defaultMaze.QuestConnection.Length >= 2);
                string fallbackReason;
                mapId = SelectFallbackMapIdForUnresolvedRoom(
                    dungeonId,
                    mazeIndex,
                    x,
                    y,
                    defaultMaze.MapSpecifications,
                    maplst != null ? maplst.Entries : null,
                    mapDirCandidates,
                    preferQuestVariantFallback,
                    out fallbackReason);

                if (mapId > 0)
                    FileLogger.Log($"[Dungeon] GetDungeonMapMonsterSummaryInformation fallback to {fallbackReason}: dungeon={dungeonId} maze={mazeIndex} room=({x},{y}) -> map={mapId}");
            }

            if (mapId == -1)
            {
                FileLogger.Log($"[Dungeon] GetDungeonMapMonsterSummaryInformation WARNING: no map resolved for dungeon={dungeonId} maze={mazeIndex} room=({x},{y}) startRoom={isStartRoom} bossRoom={isBossRoom}");
                return new MazeSumInfo { X = x, Y = y, Index = 0, Monsters = new List<MonsterSumInfo>() };
            }
            if (maplst == null)
                throw new Exception("未能成功解析门LST文件 map/map.lst");

            var mapFilePath = ResolveFilePath(maplst, mapId, "门");
            var mapFile = MapFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("map", mapFilePath)));

            var list = new List<MonsterSumInfo>();
            foreach (var item in mapFile.Monsters)
            {
                if (!item.MonsterId.HasValue || item.MonsterId.Value <= 0)
                {
                    FileLogger.Log($"[Dungeon] GetDungeonMapMonsterSummaryInformation: skip monster with invalid id in map={mapId} dungeon={dungeonId} room=({x},{y})");
                    continue;
                }
                var monsterType = (byte)item.Type;
                if (monsterType > 3)
                {
                    FileLogger.Log($"[Dungeon] GetDungeonMapMonsterSummaryInformation: clamp monster type {monsterType} to 0 in map={mapId} dungeon={dungeonId} room=({x},{y})");
                    monsterType = 0;
                }
                int rawMonsterLevel = item.Lv.GetValueOrDefault() != 0
                    ? dungeonBasicLv + item.AutoLv.GetValueOrDefault()
                    : item.AutoLv.GetValueOrDefault();
                byte monsterLevel = rawMonsterLevel > 0 ? (byte)Math.Min(rawMonsterLevel, 255) : dungeonBasicLv;
                var monster = new MonsterSumInfo
                {
                    Code = item.MonsterId.Value,
                    Type = monsterType,
                    Level = monsterLevel,
                    IsBlocking = true,
                };
                list.Add(monster);
            }

            // APC
            foreach (var apc in mapFile.AICharacters)
            {
                if (apc.Code <= 0 || !TryGetAICharacterLevel(apc.Code, out var apcLevel))
                {
                    FileLogger.Log($"[Dungeon] GetDungeonMapMonsterSummaryInformation: skip APC code={apc.Code} not found in map={mapId} dungeon={dungeonId} room=({x},{y})");
                    continue;
                }
                var apcType = (byte)apc.AIType;
                if (apcType < 5 || apcType > 8)
                {
                    FileLogger.Log($"[Dungeon] GetDungeonMapMonsterSummaryInformation: clamp APC type {apcType} to 5 in map={mapId} dungeon={dungeonId} room=({x},{y})");
                    apcType = 5;
                }
                list.Add(new MonsterSumInfo
                {
                    Code = apc.Code,
                    Type = apcType,
                    Level = apcLevel,
                    IsBlocking = false,
                });
            }

            return new MazeSumInfo
            {
                Monsters = list,
                X = x,
                Y = y,
                Index = mapId,
            };
        }

        internal static int SelectFallbackMapIdForUnresolvedRoom(
            int dungeonId,
            int mazeIndex,
            int x,
            int y,
            IReadOnlyList<MapSpecificationItem> mapSpecifications,
            IReadOnlyList<LstEntry> mapEntries,
            IReadOnlyList<string> mapDirCandidates,
            bool preferQuestVariant,
            out string reason)
        {
            reason = string.Empty;

            if (preferQuestVariant)
            {
                var questMapId = FindQuestVariantMapId(mapEntries, mapDirCandidates, x, y, out var questReason);
                if (questMapId > 0)
                {
                    reason = questReason;
                    return questMapId;
                }
            }

            // Some PVFs omit ordinary rooms from [map specification] and keep a
            // generic maze(x,y) map in the same directory. Use that PVF template
            // before unrelated specs so a boss-only spec cannot fill normal rooms.
            var mazeTemplateMapId = FindNearestCoordinateMapId(
                mapEntries,
                mapDirCandidates,
                x,
                y,
                requireMazeTemplateName: true,
                allowBossVariant: false,
                allowQuestVariant: false,
                out var mazeTemplateReason);
            if (mazeTemplateMapId > 0)
            {
                reason = mazeTemplateReason;
                return mazeTemplateMapId;
            }

            // Without a shared maze template, use nearby ordinary coordinate maps
            // by filename. This stays below exact map-spec/name matches, but above
            // the old "first map spec" fallback that can point at an unrelated room.
            var coordinateMapId = FindNearestCoordinateMapId(
                mapEntries,
                mapDirCandidates,
                x,
                y,
                requireMazeTemplateName: false,
                allowBossVariant: false,
                allowQuestVariant: false,
                out var coordinateReason);
            if (coordinateMapId > 0)
            {
                reason = coordinateReason;
                return coordinateMapId;
            }

            if (mapSpecifications != null)
            {
                for (var i = 0; i < mapSpecifications.Count; i++)
                {
                    var item = mapSpecifications[i];
                    if (item == null || item.Index <= 0)
                        continue;

                    reason = "first map spec";
                    if (item.MapCandidates != null && item.MapCandidates.Length > 0)
                    {
                        var pick = _mazeRng.Next(item.MapCandidates.Length);
                        return item.MapCandidates[pick];
                    }
                    return item.Index;
                }
            }

            var ordinaryMapId = FindCandidateMapId(mapEntries, mapDirCandidates, allowQuestVariant: false, out var ordinaryReason);
            if (ordinaryMapId > 0)
            {
                reason = ordinaryReason;
                return ordinaryMapId;
            }

            var fallbackQuestMapId = FindQuestVariantMapId(mapEntries, mapDirCandidates, x, y, out var fallbackQuestReason);
            if (fallbackQuestMapId > 0)
            {
                reason = fallbackQuestReason;
                return fallbackQuestMapId;
            }

            return -1;
        }

        private static int FindNearestCoordinateMapId(
            IReadOnlyList<LstEntry> mapEntries,
            IReadOnlyList<string> mapDirCandidates,
            int x,
            int y,
            bool requireMazeTemplateName,
            bool allowBossVariant,
            bool allowQuestVariant,
            out string reason)
        {
            reason = string.Empty;
            if (mapEntries == null)
                return -1;

            var bestId = -1;
            var bestX = 0;
            var bestY = 0;
            var bestDistance = int.MaxValue;
            var bestAxisScore = int.MinValue;

            for (var i = 0; i < mapEntries.Count; i++)
            {
                var entry = mapEntries[i];
                if (entry == null || !InMapDirCandidate(entry.FilePath, mapDirCandidates))
                    continue;

                var fileName = Path.GetFileName(entry.FilePath);
                if (!allowQuestVariant && IsQuestVariantFileName(fileName))
                    continue;
                if (!allowBossVariant && IsBossVariantFileName(fileName))
                    continue;
                if (requireMazeTemplateName && !IsMazeTemplateFileName(fileName))
                    continue;
                if (!TryParseMapFileCoordinate(fileName, out var mapX, out var mapY))
                    continue;

                // Prefer closer PVF coordinates, then same-row/same-column matches,
                // then the smaller map id so repeated fallback is deterministic.
                var distance = Math.Abs(mapX - x) + Math.Abs(mapY - y);
                var axisScore = (mapX == x ? 1 : 0) + (mapY == y ? 1 : 0);
                if (bestId > 0
                    && (distance > bestDistance
                        || (distance == bestDistance && axisScore < bestAxisScore)
                        || (distance == bestDistance && axisScore == bestAxisScore && entry.Id >= bestId)))
                    continue;

                bestId = entry.Id;
                bestX = mapX;
                bestY = mapY;
                bestDistance = distance;
                bestAxisScore = axisScore;
            }

            if (bestId <= 0)
                return -1;

            reason = requireMazeTemplateName
                ? $"nearest maze coordinate map ({bestX},{bestY})"
                : $"nearest coordinate map ({bestX},{bestY})";
            return bestId;
        }

        private static int FindCandidateMapId(
            IReadOnlyList<LstEntry> mapEntries,
            IReadOnlyList<string> mapDirCandidates,
            bool allowQuestVariant,
            out string reason)
        {
            reason = string.Empty;
            if (mapEntries == null)
                return -1;

            for (var i = 0; i < mapEntries.Count; i++)
            {
                var entry = mapEntries[i];
                if (entry == null || !InMapDirCandidate(entry.FilePath, mapDirCandidates))
                    continue;

                var fileName = Path.GetFileName(entry.FilePath);
                if (!allowQuestVariant && IsQuestVariantFileName(fileName))
                    continue;

                reason = allowQuestVariant ? "first candidate map" : "first non-quest candidate map";
                return entry.Id;
            }

            return -1;
        }

        private static int FindQuestVariantMapId(
            IReadOnlyList<LstEntry> mapEntries,
            IReadOnlyList<string> mapDirCandidates,
            int x,
            int y,
            out string reason)
        {
            reason = string.Empty;
            if (mapEntries == null)
                return -1;

            var bestId = -1;
            var bestScore = -1;
            for (var i = 0; i < mapEntries.Count; i++)
            {
                var entry = mapEntries[i];
                if (entry == null || !InMapDirCandidate(entry.FilePath, mapDirCandidates))
                    continue;

                var fileName = Path.GetFileName(entry.FilePath);
                if (!IsQuestVariantFileName(fileName))
                    continue;

                var score = ScoreQuestVariantFileName(fileName, x, y);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestId = entry.Id;
            }

            if (bestId > 0)
            {
                reason = bestScore >= 100 ? "quest-variant coordinate map" : "quest-variant map";
                return bestId;
            }

            return -1;
        }

        private static int ScoreQuestVariantFileName(string fileName, int x, int y)
        {
            if (string.IsNullOrEmpty(fileName))
                return -1;

            var stem = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            if (stem.IndexOf($"({x},{y})", StringComparison.OrdinalIgnoreCase) >= 0
                || stem.IndexOf($"({x}.{y})", StringComparison.OrdinalIgnoreCase) >= 0)
                return 120;

            if (stem.IndexOf($"{x}_{y}", StringComparison.OrdinalIgnoreCase) >= 0
                || stem.IndexOf($"{x}-{y}", StringComparison.OrdinalIgnoreCase) >= 0
                || stem.IndexOf($"{x}.{y}", StringComparison.OrdinalIgnoreCase) >= 0)
                return 100;

            return 10;
        }

        private static bool IsQuestVariantFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            var stem = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            return stem.StartsWith("q_", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("quest_", StringComparison.OrdinalIgnoreCase)
                || (stem.Length > 1
                    && char.ToLowerInvariant(stem[0]) == 'q'
                    && char.IsDigit(stem[1]));
        }

        // Only unresolved ordinary-room fallback uses this filter. Real BOSS rooms
        // are resolved earlier through map specs, boss coordinates, and actor checks.
        private static bool IsBossVariantFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;

            var stem = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            if (stem.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (stem.EndsWith("B", StringComparison.OrdinalIgnoreCase))
            {
                var prev = stem.Length >= 2 ? stem[stem.Length - 2] : '\0';
                return char.IsDigit(prev) || prev == ')';
            }

            return false;
        }

        private static bool IsMazeTemplateFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;

            var stem = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            return stem.IndexOf("maze(", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // PVF map filenames commonly encode room coordinates as "(x,y)" or "(x.y)".
        // The parser is filename-only to avoid opening map contents on this fallback path.
        private static bool TryParseMapFileCoordinate(string fileName, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (string.IsNullOrEmpty(fileName))
                return false;

            var stem = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            var match = MapCoordinateFileNameRegex.Match(stem);
            return match.Success
                && int.TryParse(match.Groups["x"].Value, out x)
                && int.TryParse(match.Groups["y"].Value, out y);
        }

        private static bool InMapDirCandidate(string filePath, IReadOnlyList<string> mapDirCandidates)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;

            if (mapDirCandidates == null || mapDirCandidates.Count == 0)
                return true;

            var normalizedPath = filePath.Replace('\\', '/');
            for (var i = 0; i < mapDirCandidates.Count; i++)
            {
                var dir = mapDirCandidates[i];
                if (string.IsNullOrEmpty(dir))
                    continue;

                dir = dir.Replace('\\', '/').TrimEnd('/');
                if (normalizedPath.Equals(dir, StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static byte GetAICharacterLevel(int apcCode)
        {
            if (TryGetAICharacterLevel(apcCode, out var level))
                return level;

            throw new Exception($"AICharacter code={apcCode} 在 AICharacter.lst 中不存在或无法解析等级");
        }

        private static bool TryGetAICharacterLevel(int apcCode, out byte level)
        {
            level = 0;
            var lst = LstFile.Parse(PvfArchiveAccessor.ReadText("AICharacter/AICharacter.lst"));
            var entry = lst.GetById(apcCode);
            if (entry == null)
                return false;

            var content = PvfArchiveAccessor.ReadText(Path.Combine("AICharacter", entry.FilePath));
            var match = System.Text.RegularExpressions.Regex.Match(content,
                @"\[minimum info\]\s*`[^`]*`\s+\d+\s+\d+\s+\d+\s+\d+\s+(\d+)");
            if (!match.Success)
                return false;

            int parsedLevel = int.Parse(match.Groups[1].Value);
            if (parsedLevel <= 0 || parsedLevel > 255)
                return false;

            level = (byte)parsedLevel;
            return true;
        }
    }
}
