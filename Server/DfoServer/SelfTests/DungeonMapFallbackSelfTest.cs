using System;
using System.Collections.Generic;
using PvfLib;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.SelfTests
{
    public static class DungeonMapFallbackSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_MAP_FALLBACK selftest ===");
            var failures = 0;

            var mapSpecs = new List<MapSpecificationItem>
            {
                new MapSpecificationItem { Type = "map", X = 0, Y = 0, Index = 13417 },
            };
            var mapEntries = new List<LstEntry>
            {
                new LstEntry { Id = 13417, FilePath = "eternal_dream/01.map" },
                new LstEntry { Id = 14999, FilePath = "eternal_dream/q_7_0.map" },
            };
            var mapDirs = new List<string> { "eternal_dream" };

            var mapId = DungeonData.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 1004,
                mazeIndex: 0,
                x: 7,
                y: 0,
                mapSpecifications: mapSpecs,
                mapEntries: mapEntries,
                mapDirCandidates: mapDirs,
                preferQuestVariant: true,
                reason: out var reason);

            Check("quest start room prefers coordinate quest variant over first ordinary map spec",
                mapId == 14999 && reason.StartsWith("quest-variant", StringComparison.Ordinal),
                ref failures);

            var compactQuestMapEntries = new List<LstEntry>
            {
                new LstEntry { Id = 13417, FilePath = "eternal_dream/01.map" },
                new LstEntry { Id = 15000, FilePath = "eternal_dream/q7_0.map" },
            };
            var compactQuestMapId = DungeonData.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 1004,
                mazeIndex: 0,
                x: 7,
                y: 0,
                mapSpecifications: mapSpecs,
                mapEntries: compactQuestMapEntries,
                mapDirCandidates: mapDirs,
                preferQuestVariant: true,
                reason: out reason);

            Check("quest variant detection accepts q-prefixed coordinate map names",
                compactQuestMapId == 15000 && reason.StartsWith("quest-variant", StringComparison.Ordinal),
                ref failures);

            var ordinaryMapId = DungeonData.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 1004,
                mazeIndex: 0,
                x: 7,
                y: 0,
                mapSpecifications: mapSpecs,
                mapEntries: mapEntries,
                mapDirCandidates: mapDirs,
                preferQuestVariant: false,
                reason: out reason);

            Check("ordinary fallback keeps first map spec when quest variant is not preferred",
                ordinaryMapId == 13417 && reason == "first map spec",
                ref failures);

            var rottenMapSpecs = new List<MapSpecificationItem>
            {
                new MapSpecificationItem { Type = "boss", X = 4, Y = 0, Index = 18914 },
            };
            var rottenMapEntries = new List<LstEntry>
            {
                new LstEntry { Id = 18914, FilePath = "158_DecayArea/18914(4,0)B.map" },
                new LstEntry { Id = 36041, FilePath = "158_DecayArea/maze(2,2).map" },
                new LstEntry { Id = 18911, FilePath = "158_DecayArea/18911(0,5)N.map" },
            };
            var rottenFallbackMapId = DungeonData.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 158,
                mazeIndex: 0,
                x: 1,
                y: 5,
                mapSpecifications: rottenMapSpecs,
                mapEntries: rottenMapEntries,
                mapDirCandidates: new List<string> { "158_DecayArea" },
                preferQuestVariant: false,
                reason: out reason);

            Check("unresolved rotten land room uses PVF maze coordinate before boss spec",
                rottenFallbackMapId == 36041 && reason.StartsWith("nearest maze coordinate map", StringComparison.Ordinal),
                ref failures);

            var lowercaseBossEntries = new List<LstEntry>
            {
                new LstEntry { Id = 77001, FilePath = "generic/77001(2,4)b.map" },
                new LstEntry { Id = 77002, FilePath = "generic/77002(2,6)N.map" },
            };
            var lowercaseBossFallbackMapId = DungeonData.SelectFallbackMapIdForUnresolvedRoom(
                dungeonId: 9000,
                mazeIndex: 0,
                x: 2,
                y: 5,
                mapSpecifications: null,
                mapEntries: lowercaseBossEntries,
                mapDirCandidates: new List<string> { "generic" },
                preferQuestVariant: false,
                reason: out reason);

            Check("coordinate fallback ignores lowercase boss suffix",
                lowercaseBossFallbackMapId == 77002 && reason.StartsWith("nearest coordinate map", StringComparison.Ordinal),
                ref failures);

            var mazes = new List<MazeInfo>
            {
                new MazeInfo { QuestConnection = null },
                new MazeInfo { QuestConnection = new[] { 0, 1848, 1 } },
            };
            var mazeIndex = DungeonData.FindQuestConnectedMazeIndex(
                mazes,
                primaryQuestIds: new HashSet<int> { 1849 },
                fallbackQuestIds: new HashSet<int> { 1848 },
                matchedQuestId: out var matchedQuestId,
                matchSource: out var matchSource);

            Check("quest maze selection falls back to active quest prerequisites",
                mazeIndex == 1 && matchedQuestId == 1848 && matchSource == "related",
                ref failures);

            try
            {
                var issue189StartMap = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 165,
                    x: 0xFF,
                    y: 0xFF,
                    mazeIndex: 4);
                Check("issue 189 quest maze start room uses map specification",
                    issue189StartMap.Index == 33060,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 189 quest maze start room uses map specification: {ex.Message}");
                failures++;
            }

            try
            {
                var upperBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 147,
                    x: 4,
                    y: 1,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 1 });
                var middleBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 147,
                    x: 4,
                    y: 2,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 2 });
                var lowerBoss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 147,
                    x: 4,
                    y: 3,
                    mazeIndex: 0,
                    bossPos: new[] { 4, 3 });

                Check("issue 180 upper boss room uses boss actor map",
                    upperBoss.Index == 8179 && ContainsMonster(upperBoss, 65312),
                    ref failures);
                Check("issue 180 middle boss room skips duplicate non-boss map",
                    middleBoss.Index == 8180 && ContainsMonster(middleBoss, 65312),
                    ref failures);
                Check("issue 180 lower boss room uses boss actor map",
                    lowerBoss.Index == 8181 && ContainsMonster(lowerBoss, 65312),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 180 boss rooms use boss actor maps: {ex.Message}");
                failures++;
            }

            try
            {
                var issue227Boss = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 0,
                    y: 5,
                    mazeIndex: 0,
                    bossPos: new[] { 0, 5 });
                var issue227Adjacent = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 158,
                    x: 1,
                    y: 5,
                    mazeIndex: 0,
                    bossPos: new[] { 0, 5 });

                Check("issue 227 selected boss room keeps boss actor map",
                    issue227Boss.Index == 18915 && ContainsMonster(issue227Boss, 65029),
                    ref failures);
                Check("issue 227 adjacent unresolved room uses rotten land maze template",
                    issue227Adjacent.Index == 36041 && !ContainsMonster(issue227Adjacent, 65029),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] issue 227 rotten land unresolved room map selection: {ex.Message}");
                failures++;
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool ContainsMonster(DungeonData.MazeSumInfo maze, int monsterCode)
        {
            if (maze.Monsters == null)
                return false;
            foreach (var monster in maze.Monsters)
                if (monster.Code == monsterCode)
                    return true;
            return false;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok) failures++;
        }
    }
}
