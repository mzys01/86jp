using System;
using System.Net.Sockets;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    // 锁定 DungeonRun 状态模型的关键语义:
    // 1. 新局字段默认值 = 旧版"返城重置后"的取值(常量表);
    // 2. Begin/End 生命周期与幂等性;
    // 3. 跨局字段(华丽挑战开关)不随 run 重建;
    // 4. 翻牌定时器句柄随局取消, 换局时旧句柄必被取消。
    public static class DungeonRunLifecycleSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_RUN_LIFECYCLE selftest ===");
            var failures = 0;

            using var client = new TcpClient();
            var session = new EnhancedClientSession(client, new GamePacketHeader());
            var player = session.Player;

            // 1. 初始无局; 塔便捷入口跟随
            Check("fresh session has no run",
                player.CurrentRun == null
                && player.DeathTowerState == null
                && !player.IsInDeathTower,
                ref failures);
            // 2. 新局字段默认值 = 旧版返城重置后的取值(常量表)
            var fresh = new DungeonRun();
            Check("fresh run fields carry legacy reset defaults",
                fresh.DungeonId == 0
                && fresh.Phase == DungeonRunPhase.None
                && fresh.MazeIndex == -1
                && fresh.LayeredMapIndex == -1
                && fresh.MazeStartX == -1
                && fresh.MazeStartY == -1
                && !fresh.HellMode
                && fresh.HellMapId == -1
                && fresh.HellMapX == 0xFF
                && fresh.HellMapY == 0xFF
                && fresh.RoomMonsters.Count == 0
                && fresh.RoomKilledSeqIds.Count == 0
                && fresh.RoomStates.Count == 0
                && fresh.Drops.Count == 0
                && fresh.CardRewards == null
                && fresh.FreeCardSlots.Length == 4 && fresh.FreeCardSlots[0] == 0xFF
                && fresh.PaidCardSlots[3] == 0xFF
                && fresh.Tower == null,
                ref failures);

            // 3. BeginRun 建立新局
            DungeonRunLifecycle.BeginRun(session, 1002, 1);
            var run = player.CurrentRun;
            Check("BeginRun creates run with entry params",
                run != null
                && run.DungeonId == 1002
                && run.Difficulty == 1
                && run.Phase == DungeonRunPhase.InProgress,
                ref failures);

            var markerRun = new DungeonRun(1002, 0);
            Check("clear-map quest sync marker deduplicates by dungeon and map",
                markerRun.TryMarkClearMapQuestSynced(0, 33060)
                && !markerRun.TryMarkClearMapQuestSynced(0, 33060)
                && markerRun.TryMarkClearMapQuestSynced(0, 33061)
                && markerRun.TryMarkClearMapQuestSynced(1002, 33060),
                ref failures);

            // 4. 跨局字段不随 run 重建
            player.HellPartyGorgeousChallengeEnabled = true;
            DungeonRunLifecycle.EndRunOnTeardown(session, "selftest");
            Check("teardown clears run",
                player.CurrentRun == null, ref failures);
            Check("cross-run fields survive teardown",
                player.HellPartyGorgeousChallengeEnabled,
                ref failures);

            // 5. 无局时 End 幂等不抛
            var idempotentOk = true;
            try
            {
                DungeonRunLifecycle.EndRunOnTeardown(session, "selftest-again");
                DungeonRunLifecycle.EndRunToTownAsync(session).GetAwaiter().GetResult();
            }
            catch { idempotentOk = false; }
            Check("End without run is idempotent", idempotentOk, ref failures);

            // 6. 翻牌定时器句柄: 取消置空 + 换局时旧句柄必被取消
            DungeonRunLifecycle.BeginRun(session, 1002, 0);
            var firstRun = player.CurrentRun;
            var handle = ClockService.Instance.ScheduleOneShot(
                "selftest:auto-flip:" + Guid.NewGuid().ToString("N"),
                DateTime.UtcNow.AddHours(1),
                _ => { });
            var versionBeforeCancel = firstRun.AutoFlipTimerVersion;
            firstRun.AutoFlipTimerHandle = handle;
            DungeonRunLifecycle.CancelAutoFlip(session);
            Check("CancelAutoFlip cancels and clears the handle",
                firstRun.AutoFlipTimerHandle == null
                && firstRun.AutoFlipTimerVersion == versionBeforeCancel + 1
                && !handle.Cancel(),
                ref failures);

            var staleHandle = ClockService.Instance.ScheduleOneShot(
                "selftest:auto-flip:" + Guid.NewGuid().ToString("N"),
                DateTime.UtcNow.AddHours(1),
                _ => { });
            firstRun.AutoFlipTimerHandle = staleHandle;
            DungeonRunLifecycle.BeginRun(session, 1003, 0);
            Check("BeginRun cancels the previous run timer and swaps the run",
                !staleHandle.Cancel()
                && !ReferenceEquals(player.CurrentRun, firstRun)
                && player.CurrentRun.DungeonId == 1003,
                ref failures);

            // 7. 塔局: 挂 Tower 载荷, 返城随局消失
            var tower = new Game.DeathTower.DeathTowerSession(new Game.DeathTower.DeathTowerData.TowerConfig
            {
                DungeonId = 11000,
                TotalStages = 3,
                StageMapIds = new[] { 1, 2, 3 },
                BasisLevel = 50,
            });
            DungeonRunLifecycle.BeginTowerRun(session, 11000, tower);
            Check("BeginTowerRun mounts tower payload",
                player.IsInDeathTower
                && ReferenceEquals(player.DeathTowerState, tower)
                && player.CurrentRun.DungeonId == 11000,
                ref failures);

            DungeonRunLifecycle.EndRunToTownAsync(session).GetAwaiter().GetResult();
            Check("EndRunToTown clears run and tower",
                player.CurrentRun == null && !player.IsInDeathTower, ref failures);

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
