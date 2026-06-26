using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Dungeon;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonMapHandler
    {
        private readonly DungeonSharedServices _svc;

        internal DungeonMapHandler(DungeonSharedServices svc) => _svc = svc;

        internal async Task HandleMoveMap(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var req = MoveMapRequest.Parse(body);
            session.Player.CurMoveMapU15 = req.Unknown15;
            session.Player.CurMoveMapU19 = req.Unknown19;

            int overrideMapId = -1;

            if (req.Unknown23 == 1)
            {
                var layeredIds = DungeonData.GetLayeredMapIds(session.Player.CurDungeon, req.NextX, req.NextY, session.Player.CurMazeIndex);
                if (layeredIds != null && layeredIds.Length > 0)
                {
                    var nextLayer = session.Player.CurLayeredMapIndex + 1;
                    if (nextLayer < layeredIds.Length)
                    {
                        session.Player.CurLayeredMapIndex = nextLayer;
                        overrideMapId = layeredIds[nextLayer];
                    }
                }
            }
            else
            {
                session.Player.CurLayeredMapIndex = -1;
            }

            await SendStartMapAsync(session, req.NextX, req.NextY, overrideMapId);
        }

        internal async Task SendStartMapAsync(EnhancedClientSession session, int nextX, int nextY, int overrideMapId)
        {
            var maze = DungeonData.GetDungeonMapMonsterSummaryInformation(session.Player.CurDungeon, nextX, nextY, session.Player.CurMazeIndex, overrideMapId, session.Player.CurBossMapPos);
            var roomKey = new RoomKey(maze.X, maze.Y, overrideMapId);

            byte[] startMapBody;

            if (session.Player.DungeonRoomStates.TryGetValue(roomKey, out var cached))
            {
                session.Player.CurRoomMonsters = cached.Maze.Monsters;
                session.Player.CurRoomStartSequence = cached.FirstSeqId;
                session.Player.CurRoomKilledSeqIds = cached.KilledSeqIds;
                session.Player.CurRoomLcg = cached.Lcg;
                session.Player.CurDungeonSeed = cached.Seed;

                startMapBody = DungeonNotificationBuilder.BuildStartMapRevisit(cached.Maze, cached.Seed);
                FileLogger.Log($"[DungeonHandler] START_MAP revisit: room=({maze.X},{maze.Y}) killed={cached.KilledSeqIds.Count}/{cached.MonsterCount} cleared={cached.IsCleared}");
            }
            else
            {
                session.Player.CurRoomMonsters = maze.Monsters;

                var startSequence = session.Player.CurMonsterCnt;
                session.Player.CurRoomStartSequence = (ushort)(startSequence + 1);
                // TODO: real server seqId has unknown gaps (Room1=1-6, Room2=8-14, 7 skipped),
                // likely extra +1 per door transition. Current approximation: firstMonsterSequence+index+1
                var seed = (uint)(DungeonSharedServices.SeedGen.Next() & ~0x40000);
                session.Player.CurDungeonSeed = seed;
                var lcg = new DnfLcg(seed);
                session.Player.CurRoomLcg = lcg;
                var killedSet = new HashSet<ushort>();
                session.Player.CurRoomKilledSeqIds = killedSet;

                session.Player.DungeonRoomStates[roomKey] = new RoomState
                {
                    Maze = maze,
                    FirstSeqId = session.Player.CurRoomStartSequence,
                    MonsterCount = (ushort)maze.Monsters.Count,
                    KilledSeqIds = killedSet,
                    Seed = seed,
                    Lcg = lcg,
                };

                byte layeredFlag = (byte)(overrideMapId > 0 ? 1 : 0);

                // df_game_r: item seq uses independent random counter v7=get_rand_int(60000), isolated from monster seq
                var itemSeqCounter = (ushort)DungeonSharedServices.SeedGen.Next(60000);
                var extraEntries = GeneratePassiveObjectDrops(
                    session.Player.CurDungeon, session.Player.CurMazeIndex,
                    ref itemSeqCounter);

                if (extraEntries != null)
                {
                    foreach (var e in extraEntries)
                        session.Player.CurDungeonDrops[e.GlobalSeq] = new DropInfo
                        {
                            SceneSlot = e.GlobalSeq,
                            TemplateId = e.ItemId,
                            StackCount = e.StackCount,
                            Endurance = e.Endurance,
                        };
                }

                var ridableForRoom = GetRidableEntriesForRoom(session, maze.X, maze.Y);
                startMapBody = DungeonNotificationBuilder.BuildStartMap(maze, startSequence, (int)seed,
                    fogOrModeFlag: layeredFlag, extraEntries: extraEntries, ridableEntries: ridableForRoom);
                session.Player.CurMonsterCnt += (ushort)maze.Monsters.Count;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001D, startMapBody));
        }

        internal static List<RidableObjectSpawnEntry> InitRidableObjects(MazeInfo maze)
        {
            var result = new List<RidableObjectSpawnEntry>();
            if (maze.RidableScript == null || maze.RidableScript.Objects.Count == 0)
                return result;

            var script = maze.RidableScript;
            var candidates = new List<RidableObject>(script.Objects);

            if (script.SelectCount > 0 && script.SelectCount < candidates.Count)
            {
                lock (DungeonSharedServices.SeedGen)
                {
                    for (int i = candidates.Count - 1; i > 0; i--)
                    {
                        int j = DungeonSharedServices.SeedGen.Next(i + 1);
                        var tmp = candidates[i];
                        candidates[i] = candidates[j];
                        candidates[j] = tmp;
                    }
                }
                candidates = candidates.GetRange(0, script.SelectCount);
            }

            foreach (var obj in candidates)
            {
                result.Add(new RidableObjectSpawnEntry
                {
                    ObjectIndex = obj.ObjectIndex,
                    MonsterIndex = 0,
                    PosX = obj.PosX,
                    PosY = obj.PosY,
                    Faction = obj.Faction,
                    MapX = (byte)obj.MapX,
                    MapY = (byte)obj.MapY,
                });
            }

            if (result.Count > 0)
                FileLogger.Log($"[DungeonHandler] RIDABLE: selected {result.Count}/{script.Objects.Count} objects (select={script.SelectCount})");

            return result;
        }

        private static List<RidableObjectSpawnEntry> GetRidableEntriesForRoom(
            EnhancedClientSession session, int roomX, int roomY)
        {
            var all = session.Player.CurDungeonRidableObjects;
            if (all == null || all.Count == 0) return null;
            var result = new List<RidableObjectSpawnEntry>();
            foreach (var r in all)
            {
                if (r.MapX == roomX && r.MapY == roomY)
                    result.Add(r);
            }
            return result.Count > 0 ? result : null;
        }

        private static List<PassiveObjectDropEntry> GeneratePassiveObjectDrops(
            int dungeonId, int mazeIndex, ref ushort itemSeqCounter)
        {
            try
            {
                var dgnlst = DungeonData.LoadDungeonLstFile();
                var dgnPath = dgnlst.GetById(dungeonId);
                if (dgnPath == null || string.IsNullOrEmpty(dgnPath.FilePath)) return null;

                var dgnText = GameWorld.PvfArchiveAccessor.ReadText(
                    System.IO.Path.Combine("dungeon", dgnPath.FilePath));
                var dgn = DungeonFile.Parse(dgnText);
                if (dgn.SpecialPassiveObjectItems.Count == 0) return null;

                var result = new List<PassiveObjectDropEntry>();
                var rng = new Random();

                foreach (var item in dgn.SpecialPassiveObjectItems)
                {
                    int roll = rng.Next(10000);
                    if (roll >= item.DropRate) continue;

                    itemSeqCounter++;
                    result.Add(new PassiveObjectDropEntry
                    {
                        ObjectIndex = (byte)item.Index,
                        GlobalSeq = itemSeqCounter,
                        ItemId = (uint)item.ItemId,
                        StackCount = 1,
                    });
                }

                if (result.Count > 0)
                    FileLogger.Log($"[DungeonHandler] PASSIVE_OBJ_DROP: {result.Count} items generated for dungeon={dungeonId}");
                return result.Count > 0 ? result : null;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] GeneratePassiveObjectDrops ERROR: {ex.Message}");
                return null;
            }
        }
    }
}
