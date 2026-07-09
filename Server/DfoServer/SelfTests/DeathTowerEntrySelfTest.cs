using System;
using DfoServer.Game.DeathTower;

namespace DfoServer.SelfTests
{
    public static class DeathTowerEntrySelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DEATH_TOWER_ENTRY selftest ===");
            var failures = 0;

            var config = new DeathTowerData.TowerConfig
            {
                DungeonId = 11000,
                TotalStages = 3,
                StageMapIds = new[] { 33060, 33061, 33062 },
                BasisLevel = 50,
            };

            var formal = new DeathTowerSession(config);
            Check("select-dungeon tower defaults to formal run",
                formal.EntryMode == DeathTowerRunMode.Formal && formal.IsFormalRun,
                ref failures);

            Check("formal tower clear-map uses current stage map",
                DeathTowerHandler.TryResolveFormalStageClearMapId(formal, out var formalMapId)
                && formalMapId == 33060,
                ref failures);

            formal.SetFighting();
            formal.SetCleared();
            formal.TryAdvanceStage();
            Check("formal tower clear-map follows advanced stage map",
                DeathTowerHandler.TryResolveFormalStageClearMapId(formal, out var secondMapId)
                && secondMapId == 33061,
                ref failures);

            var practice = new DeathTowerSession(config, DeathTowerRunMode.Practice);
            Check("practice tower clear-map sync is blocked",
                !DeathTowerHandler.TryResolveFormalStageClearMapId(practice, out _),
                ref failures);

            var missingMap = new DeathTowerSession(new DeathTowerData.TowerConfig
            {
                DungeonId = 11000,
                TotalStages = 1,
                StageMapIds = Array.Empty<int>(),
                BasisLevel = 50,
            });
            Check("formal tower clear-map rejects missing map id",
                !DeathTowerHandler.TryResolveFormalStageClearMapId(missingMap, out _),
                ref failures);

            var towerInfo = DeathTowerPacketBuilder.BuildTowerInfo(11000, 3);
            Check("0x008E body remains 8 bytes",
                towerInfo.Length == 8,
                ref failures);
            Check("0x008E encodes dungeon and stage count",
                BitConverter.ToUInt32(towerInfo, 0) == 11000
                && BitConverter.ToUInt16(towerInfo, 4) == 3,
                ref failures);
            Check("0x008E observed tail stays 01 0B until client evidence changes it",
                towerInfo[6] == DeathTowerPacketBuilder.ObservedTowerInfoModeByte
                && towerInfo[6] == 1
                && towerInfo[7] == DeathTowerPacketBuilder.ObservedRandomBuffType
                && towerInfo[7] == 11,
                ref failures);

            var dungeonInfo = DeathTowerPacketBuilder.BuildFormalDungeonInfo(11000, difficulty: 2);
            Check("formal tower 0x001C encodes dungeon id and difficulty",
                dungeonInfo.Length >= 12
                && BitConverter.ToInt16(dungeonInfo, 0) == 11000
                && dungeonInfo[2] == 2,
                ref failures);
            Check("formal tower 0x001C uses non-hell neutral map fields",
                dungeonInfo[3] == 0
                && dungeonInfo[4] == 0
                && dungeonInfo[5] == 0
                && dungeonInfo[6] == 0xFF
                && dungeonInfo[7] == 0xFF
                && dungeonInfo[8] == 0
                && dungeonInfo[9] == 0,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
