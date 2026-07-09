using System;
using System.Collections.Generic;
using System.Net.Sockets;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.SelfTests
{
    public static class DungeonRoomProgressSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_ROOM_PROGRESS selftest ===");
            var failures = 0;

            using var client = new TcpClient();
            var session = new EnhancedClientSession(client, new GamePacketHeader());
            var run = new Game.Dungeon.DungeonRun(1000, 0);
            session.Player.CurrentRun = run;

            run.RoomStartSequence = 10;
            run.RoomMonsters = new List<DungeonData.MonsterSumInfo>
            {
                new DungeonData.MonsterSumInfo { Code = 200, Type = 0, Level = 1, IsBlocking = true },
                new DungeonData.MonsterSumInfo { Code = 56408, Type = 8, Level = 25, IsBlocking = true },
            };
            run.RoomKilledSeqIds = new HashSet<ushort> { 10 };

            var progress = DungeonRoomTopology.GetCurrentRoomProgress(session);
            Check("enemy apc remains blocking after normal monster kill",
                !DungeonRoomTopology.ShouldClearAfterApcDialog(progress)
                && progress.KilledNormalCount == 1
                && progress.BlockingRemainingCount == 1
                && !progress.RoomPassable,
                ref failures);

            run.RoomKilledSeqIds.Add(11);
            progress = DungeonRoomTopology.GetCurrentRoomProgress(session);
            Check("apc dialog may clear after blocking apc is defeated",
                DungeonRoomTopology.ShouldClearAfterApcDialog(progress)
                && progress.BlockingRemainingCount == 0
                && progress.RoomPassable,
                ref failures);

            run.RoomStartSequence = 20;
            run.RoomMonsters = new List<DungeonData.MonsterSumInfo>
            {
                new DungeonData.MonsterSumInfo { Code = 300, Type = 5, Level = 1, IsBlocking = false },
            };
            run.RoomKilledSeqIds = new HashSet<ushort>();
            progress = DungeonRoomTopology.GetCurrentRoomProgress(session);
            Check("non-blocking apc dialog room can clear",
                DungeonRoomTopology.ShouldClearAfterApcDialog(progress)
                && progress.BlockingRemainingCount == 0,
                ref failures);

            run.RoomStartSequence = 30;
            run.RoomMonsters = new List<DungeonData.MonsterSumInfo>
            {
                new DungeonData.MonsterSumInfo { Code = 301, Type = 0, Level = 1, IsBlocking = true },
                new DungeonData.MonsterSumInfo { Code = 302, Type = 5, Level = 1, IsBlocking = false },
            };
            run.RoomKilledSeqIds = new HashSet<ushort>();
            progress = DungeonRoomTopology.GetCurrentRoomProgress(session);
            Check("apc dialog does not clear while normal monster remains",
                !DungeonRoomTopology.ShouldClearAfterApcDialog(progress)
                && progress.KilledNormalCount == 0
                && progress.NormalCount == 1,
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
