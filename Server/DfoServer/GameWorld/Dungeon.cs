using System;
using System.Collections.Generic;
using System.IO;
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

            public byte Type { get; set; }

            public bool IsBlocking { get; set; }
        }

        public struct MazeSumInfo
        {
            public int Index { get; set; }

            public int X { get; set; }

            public int Y { get; set; }

            public List<MonsterSumInfo> Monsters { get; set; }
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

        public static int[] RandomizeBossPosition(int[] bossMap)
        {
            if (bossMap == null || bossMap.Length < 2) return null;
            int pairCount = bossMap.Length / 2;
            if (pairCount <= 1) return new[] { bossMap[0], bossMap[1] };
            int pick = _mazeRng.Next(pairCount);
            return new[] { bossMap[pick * 2], bossMap[pick * 2 + 1] };
        }

        public static (MazeInfo Maze, int Index) SelectDungeonMaze(int dungeonId, ICollection<int> activeQuestIds = null)
        {
            var dgnlst = LoadLstFile(Path.Combine("dungeon", "dungeon.lst"));
            if (dgnlst == null)
                throw new Exception("未能成功解析地下城LST文件 dungeon/dungeon.lst");

            var dgnFilePath = ResolveFilePath(dgnlst, dungeonId, "地下城");
            var dngFile = DungeonFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("dungeon", dgnFilePath)));
            if (dngFile.Mazes == null || dngFile.Mazes.Count == 0)
                throw new Exception("未解析到迷宫信息");

            if (activeQuestIds != null && activeQuestIds.Count > 0)
            {
                for (int i = 0; i < dngFile.Mazes.Count; i++)
                {
                    var qc = dngFile.Mazes[i].QuestConnection;
                    if (qc != null && qc.Length >= 2 && activeQuestIds.Contains(qc[1]))
                    {
                        FileLogger.Log($"[Dungeon] SelectMaze: dungeon={dungeonId} matched quest maze #{i} (questId={qc[1]})");
                        return (dngFile.Mazes[i], i);
                    }
                }
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
                    listO.Add(new MonsterSumInfo
                    {
                        Code = item.MonsterId.Value,
                        Type = (byte)item.Type,
                        Level = item.Lv.GetValueOrDefault() != 0
                            ? (byte)(GetDungeonBasicLv(dungeonId) + item.AutoLv.GetValueOrDefault())
                            : (byte)item.AutoLv.GetValueOrDefault(),
                        IsBlocking = true,
                    });
                }
                foreach (var apc in mapFileO.AICharacters)
                {
                    byte apcLevel = GetAICharacterLevel(apc.Code);
                    listO.Add(new MonsterSumInfo
                    {
                        Code = apc.Code,
                        Type = (byte)apc.AIType,
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
                if (string.IsNullOrEmpty(fileName)) return false;
                return fileName.StartsWith("q_", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("quest_", StringComparison.OrdinalIgnoreCase);
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

            if (isStartRoom)
            {
                mapId = FindMapIdByFileName(new[]
                {
                    $"({x},{y})_start", $"({x},{y})start",
                    $"({x}.{y})_start", $"({x}.{y})start",
                });
            }

            if (mapId == -1)
            {
                foreach (var item in defaultMaze.MapSpecifications)
                {
                    if (item.X == x && item.Y == y)
                    {
                        if (isBossRoom && item.Type != "boss")
                            continue;
                        if (item.MapCandidates != null && item.MapCandidates.Length > 1)
                            mapId = item.MapCandidates[_mazeRng.Next(item.MapCandidates.Length)];
                        else
                            mapId = item.Index;
                        break;
                    }
                }
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
                return new MazeSumInfo { X = x, Y = y, Index = 0, Monsters = new List<MonsterSumInfo>() };
            }
            if (maplst == null)
                throw new Exception("未能成功解析门LST文件 map/map.lst");

            var mapFilePath = ResolveFilePath(maplst, mapId, "门");
            var mapFile = MapFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("map", mapFilePath)));

            var list = new List<MonsterSumInfo>();
            foreach (var item in mapFile.Monsters)
            {
                var monster = new MonsterSumInfo
                {
                    Code = item.MonsterId.Value,
                    Type = (byte)item.Type,
                    Level = item.Lv.GetValueOrDefault() != 0
                        ? (byte)(GetDungeonBasicLv(dungeonId) + item.AutoLv.GetValueOrDefault())
                        : (byte)item.AutoLv.GetValueOrDefault(),
                    IsBlocking = true,
                };
                list.Add(monster);
            }

            // APC
            foreach (var apc in mapFile.AICharacters)
            {
                byte apcLevel = GetAICharacterLevel(apc.Code);
                list.Add(new MonsterSumInfo
                {
                    Code = apc.Code,
                    Type = (byte)apc.AIType,
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

        private static byte GetAICharacterLevel(int apcCode)
        {
            var lst = LstFile.Parse(PvfArchiveAccessor.ReadText("AICharacter/AICharacter.lst"));
            var entry = lst.GetById(apcCode);
            if (entry == null)
                throw new Exception($"AICharacter code={apcCode} 在 AICharacter.lst 中不存在");

            var content = PvfArchiveAccessor.ReadText(Path.Combine("AICharacter", entry.FilePath));
            var match = System.Text.RegularExpressions.Regex.Match(content,
                @"\[minimum info\]\s*`[^`]*`\s+\d+\s+\d+\s+\d+\s+\d+\s+(\d+)");
            if (!match.Success)
                throw new Exception($"AICharacter code={apcCode} file={entry.FilePath} 无法解析 [minimum info] 等级");

            int level = int.Parse(match.Groups[1].Value);
            if (level <= 0 || level > 255)
                throw new Exception($"AICharacter code={apcCode} 等级={level} 超出有效范围 [1,255]");

            return (byte)level;
        }
    }
}