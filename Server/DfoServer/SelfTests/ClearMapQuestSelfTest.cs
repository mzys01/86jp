using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;

namespace DfoServer.SelfTests
{
    public static class ClearMapQuestSelfTest
    {
        private const int CharacterId = 189001;
        private const ushort DungeonQuestId = 1890;
        private const ushort MapQuestId = 1891;
        private const ushort OtherQuestId = 1892;

        public static int Run()
        {
            Console.WriteLine("=== CLEAR_MAP_QUEST selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "clear-map-quest.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = DungeonQuestId, TriggerValue = 1 },
                new ActiveQuest { Slot = 1, QuestId = MapQuestId, TriggerValue = 2 },
                new ActiveQuest { Slot = 2, QuestId = OtherQuestId, TriggerValue = 3 },
            });

            var failures = 0;

            var changed = QuestService.SyncClearMapQuestProgressCore(
                connStr,
                CharacterId,
                dungeonId: 0,
                mapId: 33061,
                matchesClearMapQuest: MatchesClearMapQuest);
            Check("room clear only updates map-target quest", changed == 1, ref failures);
            Check("map quest trigger cleared", LoadTrigger(connStr, MapQuestId) == 0, ref failures);
            Check("dungeon quest unchanged by room-only sync", LoadTrigger(connStr, DungeonQuestId) == 1, ref failures);

            changed = QuestService.SyncClearMapQuestProgressCore(
                connStr,
                CharacterId,
                dungeonId: 33060,
                mapId: 0,
                matchesClearMapQuest: MatchesClearMapQuest);
            Check("dungeon clear updates dungeon-target quest", changed == 1, ref failures);
            Check("dungeon quest trigger cleared", LoadTrigger(connStr, DungeonQuestId) == 0, ref failures);
            Check("unmatched quest unchanged", LoadTrigger(connStr, OtherQuestId) == 3, ref failures);

            Check("issue 189 quest matches start map id",
                GameWorld.QuestData.MatchesClearMapTarget(1981, dungeonId: 0, mapId: 33060),
                ref failures);
            Check("issue 189 quest does not match boss map id",
                !GameWorld.QuestData.MatchesClearMapTarget(1981, dungeonId: 0, mapId: 33061),
                ref failures);
            Check("issue 189 start-map SET_TRIGGER is deferred before dungeon clear",
                QuestManager.ShouldDeferQuestConnectedStartMapSetTrigger(
                    1981,
                    triggerType: 0,
                    isIncrement: false,
                    dungeonCleared: false,
                    mazeQuestConnected: true,
                    mazeStartMapId: 33060),
                ref failures);
            Check("issue 189 start-map SET_TRIGGER is allowed after dungeon clear",
                !QuestManager.ShouldDeferQuestConnectedStartMapSetTrigger(
                    1981,
                    triggerType: 0,
                    isIncrement: false,
                    dungeonCleared: true,
                    mazeQuestConnected: true,
                    mazeStartMapId: 33060),
                ref failures);
            Check("issue 189 start-map increment trigger is not deferred",
                !QuestManager.ShouldDeferQuestConnectedStartMapSetTrigger(
                    1981,
                    triggerType: 0,
                    isIncrement: true,
                    dungeonCleared: false,
                    mazeQuestConnected: true,
                    mazeStartMapId: 33060),
                ref failures);
            Check("auto-complete start-map quest is not deferred",
                !QuestManager.ShouldDeferQuestConnectedStartMapSetTrigger(
                    2541,
                    triggerType: 0,
                    isIncrement: false,
                    dungeonCleared: false,
                    mazeQuestConnected: true,
                    mazeStartMapId: 18017),
                ref failures);

            changed = QuestService.SyncClearMapQuestProgressCore(
                connStr,
                CharacterId,
                dungeonId: 33060,
                mapId: 33061,
                matchesClearMapQuest: MatchesClearMapQuest);
            Check("clear-map sync is idempotent", changed == 0, ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool MatchesClearMapQuest(ushort questId, int dungeonId, int mapId)
        {
            if (questId == DungeonQuestId)
                return dungeonId == 33060;
            if (questId == MapQuestId)
                return mapId == 33061;
            return false;
        }

        private static uint LoadTrigger(string connStr, ushort questId)
        {
            var active = QuestService.LoadActiveQuests(connStr, CharacterId);
            var quest = QuestService.FindByQuestId(active, questId);
            return quest != null ? quest.TriggerValue : uint.MaxValue;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
