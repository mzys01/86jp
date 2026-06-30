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

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok) failures++;
        }
    }
}
