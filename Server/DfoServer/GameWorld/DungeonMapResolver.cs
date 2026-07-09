using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal static class DungeonMapResolver
    {
        private static readonly Regex MapCoordinateFileNameRegex =
            new Regex(@"\((?<x>-?\d+)[,.](?<y>-?\d+)\)", RegexOptions.Compiled);

        private static readonly ConcurrentDictionary<int, bool> BossActorMapCache =
            new ConcurrentDictionary<int, bool>();

        private struct MapResolveContext
        {
            public LstFile MapLst;
            public List<string> MapDirCandidates;
            public string DgnDir;
            public MazeInfo Maze;
            public int X;
            public int Y;
            public bool IsStartRoom;
            public bool IsBossRoom;
        }

        internal static int ResolveMapId(int dungeonId, int x, int y, MazeInfo maze, int mazeIndex, int[] bossPos)
        {
            var maplst = Dungeon.LoadLstFile(Path.Combine("map", "map.lst"));
            var loaded = Dungeon.LoadDungeonFileWithPath(dungeonId);
            var dgnDir = Path.GetFileNameWithoutExtension(loaded.FilePath);
            var mapDirCandidates = Dungeon.BuildMapDirCandidates(maplst, maze, loaded.FilePath);

            var effectiveBoss = bossPos ?? (maze.BossMap != null && maze.BossMap.Length >= 2
                ? new[] { maze.BossMap[0], maze.BossMap[1] } : null);

            var ctx = new MapResolveContext
            {
                MapLst = maplst,
                MapDirCandidates = mapDirCandidates,
                DgnDir = dgnDir,
                Maze = maze,
                X = x,
                Y = y,
                IsStartRoom = maze.StartMap != null && maze.StartMap.Length >= 2
                               && maze.StartMap[0] == x && maze.StartMap[1] == y,
                IsBossRoom = effectiveBoss != null && effectiveBoss[0] == x && effectiveBoss[1] == y,
            };

            var isQuestConnectedMaze = maze.QuestConnection != null && maze.QuestConnection.Length >= 2;

            int mapId = -1;

            // 1. Quest-connected maze → MapSpecification
            if (isQuestConnectedMaze)
                mapId = FindMapIdByMapSpecification(ref ctx, allowMapTypeForBossRoom: false);

            // 2. Start room → filename patterns "(x,y)_start"
            if (mapId == -1 && ctx.IsStartRoom)
            {
                mapId = FindMapIdByFileName(ref ctx, new[]
                {
                    $"({x},{y})_start", $"({x},{y})start",
                    $"({x}.{y})_start", $"({x}.{y})start",
                });
            }

            // 3. Start room (non-quest) → prefix 's', digit suffix 'S', keyword "start"
            if (mapId == -1 && ctx.IsStartRoom && !isQuestConnectedMaze)
            {
                mapId = FindMapIdByPrefixChar(ref ctx, 's');
                if (mapId == -1)
                    mapId = FindMapIdByDigitSuffix(ref ctx, 'S');
                if (mapId == -1)
                    mapId = FindMapIdByKeywordPrefix(ref ctx, "start");
            }

            // 4. General → MapSpecification
            if (mapId == -1)
                mapId = FindMapIdByMapSpecification(ref ctx, allowMapTypeForBossRoom: false);

            // 5. Boss room → filename patterns "(x,y)_boss", prefix 'b', digit suffix 'B', keyword "boss"
            if (ctx.IsBossRoom && mapId == -1)
            {
                int bossVariant = FindMapIdByFileName(ref ctx, new[]
                {
                    $"({x},{y})_boss", $"({x},{y})boss",
                    $"({x}.{y})_boss", $"({x}.{y})boss",
                }, allowBossVariant: true);
                if (bossVariant == -1)
                    bossVariant = FindMapIdByPrefixChar(ref ctx, 'b');
                if (bossVariant == -1)
                    bossVariant = FindMapIdByDigitSuffix(ref ctx, 'B');
                if (bossVariant == -1)
                    bossVariant = FindMapIdByKeywordPrefix(ref ctx, "boss");
                if (bossVariant != -1)
                    mapId = bossVariant;
            }

            // 6. Boss room → map spec with type "map"
            if (mapId == -1 && ctx.IsBossRoom)
            {
                foreach (var item in maze.MapSpecifications)
                {
                    if (item.X == x && item.Y == y && item.Type == "map")
                    {
                        mapId = (item.MapCandidates != null && item.MapCandidates.Length > 1)
                            ? item.MapCandidates[Infrastructure.ServerRandom.Next(item.MapCandidates.Length)]
                            : item.Index;
                        break;
                    }
                }
            }

            // 7. Boss room → FindNumericStemNeighbor (largest)
            if (mapId == -1 && ctx.IsBossRoom)
                mapId = FindNumericStemNeighbor(ref ctx, wantSmallest: false);

            // 8. Start room retry
            if (mapId == -1 && ctx.IsStartRoom)
            {
                mapId = FindMapIdByPrefixChar(ref ctx, 's');
                if (mapId == -1)
                    mapId = FindMapIdByDigitSuffix(ref ctx, 'S');
                if (mapId == -1)
                    mapId = FindMapIdByKeywordPrefix(ref ctx, "start");
                if (mapId == -1)
                    mapId = FindNumericStemNeighbor(ref ctx, wantSmallest: true);
            }

            // 9. General → filename "(x,y)"
            if (mapId == -1)
                mapId = FindMapIdByFileName(ref ctx, new[] { $"({x},{y})", $"({x}.{y})" });

            // 10. Start/Boss → dungeon name prefix
            if (mapId == -1 && (ctx.IsStartRoom || ctx.IsBossRoom))
                mapId = FindMapIdByDgnNamePrefix(ref ctx);

            // 11. Final fallback
            if (mapId == -1)
            {
                var preferQuestVariantFallback = ctx.IsStartRoom
                    || (maze.QuestConnection != null && maze.QuestConnection.Length >= 2);
                mapId = SelectFallbackMapIdForUnresolvedRoom(
                    dungeonId, mazeIndex, x, y,
                    maze.MapSpecifications,
                    maplst.Entries,
                    mapDirCandidates,
                    preferQuestVariantFallback,
                    out var fallbackReason);

                if (mapId > 0)
                    FileLogger.Log($"[Dungeon] GetDungeonMapMonsterSummaryInformation fallback to {fallbackReason}: dungeon={dungeonId} maze={mazeIndex} room=({x},{y}) -> map={mapId}");
            }

            return mapId;
        }

        private static int FindMapIdByFileName(ref MapResolveContext ctx, string[] patterns, bool allowBossVariant = false)
        {
            if (ctx.MapLst == null) return -1;
            foreach (var pat in patterns)
            {
                foreach (var entry in ctx.MapLst.Entries)
                {
                    if (!InMapDirCandidate(entry.FilePath, ctx.MapDirCandidates)) continue;
                    var fileName = Path.GetFileName(entry.FilePath);
                    if (IsQuestVariantFileName(fileName)) continue;
                    if (!allowBossVariant && IsBossVariantFileName(fileName)) continue;
                    if (fileName.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                        return entry.Id;
                }
            }
            return -1;
        }

        private static int FindMapIdByPrefixChar(ref MapResolveContext ctx, char ch)
        {
            if (ctx.MapLst == null) return -1;
            foreach (var entry in ctx.MapLst.Entries)
            {
                if (!InMapDirCandidate(entry.FilePath, ctx.MapDirCandidates)) continue;
                var fileName = Path.GetFileName(entry.FilePath);
                if (IsQuestVariantFileName(fileName)) continue;
                if (fileName.Length > 1
                    && char.ToLowerInvariant(fileName[0]) == char.ToLowerInvariant(ch)
                    && char.IsDigit(fileName[1]))
                    return entry.Id;
            }
            return -1;
        }

        private static int FindMapIdByDigitSuffix(ref MapResolveContext ctx, char suffix)
        {
            if (ctx.MapLst == null) return -1;
            foreach (var entry in ctx.MapLst.Entries)
            {
                if (!InMapDirCandidate(entry.FilePath, ctx.MapDirCandidates)) continue;
                var fileName = Path.GetFileName(entry.FilePath);
                if (IsQuestVariantFileName(fileName)) continue;
                var stem = Path.GetFileNameWithoutExtension(fileName);
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

        private static int FindMapIdByKeywordPrefix(ref MapResolveContext ctx, string keyword)
        {
            if (ctx.MapLst == null) return -1;
            foreach (var entry in ctx.MapLst.Entries)
            {
                if (!InMapDirCandidate(entry.FilePath, ctx.MapDirCandidates)) continue;
                var fileName = Path.GetFileName(entry.FilePath);
                if (IsQuestVariantFileName(fileName)) continue;
                var stem = Path.GetFileNameWithoutExtension(fileName);
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

        private static int FindNumericStemNeighbor(ref MapResolveContext ctx, bool wantSmallest)
        {
            if (ctx.MapLst == null) return -1;
            int chosen = -1;
            foreach (var entry in ctx.MapLst.Entries)
            {
                if (!InMapDirCandidate(entry.FilePath, ctx.MapDirCandidates)) continue;
                var fileName = Path.GetFileName(entry.FilePath);
                if (IsQuestVariantFileName(fileName)) continue;
                var stem = Path.GetFileNameWithoutExtension(fileName);
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

        private static int FindMapIdByDgnNamePrefix(ref MapResolveContext ctx)
        {
            if (ctx.MapLst == null || string.IsNullOrEmpty(ctx.DgnDir)) return -1;
            foreach (var entry in ctx.MapLst.Entries)
            {
                if (!InMapDirCandidate(entry.FilePath, ctx.MapDirCandidates)) continue;
                var fileName = Path.GetFileName(entry.FilePath);
                if (IsQuestVariantFileName(fileName)) continue;
                if (fileName.StartsWith(ctx.DgnDir, StringComparison.OrdinalIgnoreCase))
                    return entry.Id;
            }
            return -1;
        }

        private static bool HasBossActor(LstFile maplst, int mapId)
        {
            if (maplst == null || mapId <= 0) return false;
            if (BossActorMapCache.TryGetValue(mapId, out var cached))
                return cached;

            var found = false;
            try
            {
                var mapFilePath = Dungeon.ResolveFilePath(maplst, mapId, "map");
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
            BossActorMapCache[mapId] = found;
            return found;
        }

        private static int ChooseBossRoomMapId(List<int> bossActorMapIds, int[] originalCandidates)
        {
            if (bossActorMapIds != null && bossActorMapIds.Count > 0)
            {
                return bossActorMapIds.Count > 1
                    ? bossActorMapIds[Infrastructure.ServerRandom.Next(bossActorMapIds.Count)]
                    : bossActorMapIds[0];
            }

            if (originalCandidates == null || originalCandidates.Length == 0)
                return -1;
            return originalCandidates.Length > 1
                ? originalCandidates[Infrastructure.ServerRandom.Next(originalCandidates.Length)]
                : originalCandidates[0];
        }

        private static int FindMapIdByMapSpecification(ref MapResolveContext ctx, bool allowMapTypeForBossRoom)
        {
            if (ctx.Maze.MapSpecifications == null)
                return -1;

            if (ctx.IsBossRoom)
            {
                var bossActorMapIds = new List<int>();
                int[] originalCandidates = null;
                for (var specIndex = 0; specIndex < ctx.Maze.MapSpecifications.Count; specIndex++)
                {
                    var item = ctx.Maze.MapSpecifications[specIndex];
                    if (item.X != ctx.X || item.Y != ctx.Y)
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
                        if (candidate > 0 && HasBossActor(ctx.MapLst, candidate))
                            bossActorMapIds.Add(candidate);
                    }
                }

                return ChooseBossRoomMapId(bossActorMapIds, originalCandidates);
            }

            foreach (var item in ctx.Maze.MapSpecifications)
            {
                if (item.X != ctx.X || item.Y != ctx.Y)
                    continue;
                if (string.Equals(item.Type, "boss", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (item.MapCandidates != null && item.MapCandidates.Length > 1)
                    return item.MapCandidates[Infrastructure.ServerRandom.Next(item.MapCandidates.Length)];
                return item.Index;
            }

            return -1;
        }

        internal static int SelectFallbackMapIdForUnresolvedRoom(
            int dungeonId, int mazeIndex, int x, int y,
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

            var coordinateMapId = FindNearestCoordinateMapId(
                mapEntries, mapDirCandidates, x, y,
                requireMazeTemplateName: false,
                allowBossVariant: false,
                allowQuestVariant: false,
                out var coordinateReason);
            if (coordinateMapId > 0)
            {
                reason = coordinateReason;
                return coordinateMapId;
            }

            var mazeTemplateMapId = FindNearestCoordinateMapId(
                mapEntries, mapDirCandidates, x, y,
                requireMazeTemplateName: true,
                allowBossVariant: false,
                allowQuestVariant: false,
                out var mazeTemplateReason);
            if (mazeTemplateMapId > 0)
            {
                reason = mazeTemplateReason;
                return mazeTemplateMapId;
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
                        var pick = Infrastructure.ServerRandom.Next(item.MapCandidates.Length);
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
            int x, int y,
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
                if (!requireMazeTemplateName && IsMazeTemplateFileName(fileName))
                    continue;
                if (!TryParseMapFileCoordinate(fileName, out var mapX, out var mapY))
                    continue;

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
            int x, int y,
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

        internal static bool IsQuestVariantFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            var stem = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            return stem.StartsWith("q_", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("quest_", StringComparison.OrdinalIgnoreCase)
                || (stem.Length > 1
                    && char.ToLowerInvariant(stem[0]) == 'q'
                    && char.IsDigit(stem[1]));
        }

        internal static bool IsBossVariantFileName(string fileName)
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

        internal static bool TryParseMapFileCoordinate(string fileName, out int x, out int y)
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
    }
}
