using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Dungeon;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonMapHandler
    {
        private const byte HellPartyHiddenTemplateFlag = 1;
        private const byte HellPartyAttachAllWavesSelector = 0xFF;

        private readonly DungeonSharedServices _svc;

        internal DungeonMapHandler(DungeonSharedServices svc) => _svc = svc;

        internal async Task HandleMoveMap(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var req = MoveMapRequest.Parse(body);

            if (session.Player.CurDungeonCleared)
            {
                FileLogger.Log($"[DungeonHandler] MOVE_MAP ignored after dungeon clear: current=({session.Player.CurRoomKey.X},{session.Player.CurRoomKey.Y}) next=({req.NextX},{req.NextY})");
                return;
            }

            session.Player.CurMoveMapU15 = req.Unknown15;
            session.Player.CurMoveMapU19 = req.Unknown19;

            if (IsCurrentHellPartyLocked(session))
            {
                FileLogger.Log($"[DungeonHandler] MOVE_MAP blocked by active hell party: current=({session.Player.CurRoomKey.X},{session.Player.CurRoomKey.Y}) next=({req.NextX},{req.NextY})");
                return;
            }

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
            var effectiveOverrideMapId = overrideMapId;
            var maze = DungeonData.GetDungeonMapMonsterSummaryInformation(session.Player.CurDungeon, nextX, nextY, session.Player.CurMazeIndex, overrideMapId, session.Player.CurBossMapPos);
            if (overrideMapId <= 0
                && session.Player.CurDungeonHellMode
                && session.Player.CurDungeonHellMapId > 0
                && maze.X == session.Player.CurDungeonHellMapX
                && maze.Y == session.Player.CurDungeonHellMapY)
            {
                var hellMapId = session.Player.CurDungeonHellMapId;
                effectiveOverrideMapId = hellMapId;
                if (hellMapId != maze.Index)
                    maze = DungeonData.GetDungeonMapMonsterSummaryInformation(session.Player.CurDungeon, maze.X, maze.Y, session.Player.CurMazeIndex, hellMapId, session.Player.CurBossMapPos);
                FileLogger.Log($"[DungeonHandler] START_MAP hell override: room=({maze.X},{maze.Y}) map={maze.Index}");
            }

            var roomKey = new RoomKey(maze.X, maze.Y, effectiveOverrideMapId);
            session.Player.CurRoomKey = roomKey;
            CacheQuestConnectedStartMapId(session, maze);

            byte[] startMapBody;
            List<KeyValuePair<int, int>> hellPartyMonsterInfoAfterStartMap = null;

            if (session.Player.DungeonRoomStates.TryGetValue(roomKey, out var cached))
            {
                session.Player.CurRoomMonsters = cached.Maze.Monsters;
                session.Player.CurRoomStartSequence = cached.FirstSeqId;
                session.Player.CurRoomKilledSeqIds = cached.KilledSeqIds;
                session.Player.CurRoomLcg = cached.Lcg;
                session.Player.CurDungeonSeed = cached.Seed;
                session.Player.CurRoomKey = roomKey;

                startMapBody = DungeonNotificationBuilder.BuildStartMapRevisit(cached.Maze, cached.Seed);
                FileLogger.Log($"[DungeonHandler] START_MAP revisit: room=({maze.X},{maze.Y}) killed={cached.KilledSeqIds.Count}/{cached.MonsterCount} cleared={cached.IsCleared}");
            }
            else
            {
                var startSequence = session.Player.CurMonsterCnt;
                session.Player.CurRoomStartSequence = (ushort)(startSequence + 1);
                // TODO：真实服务端房间切换时序号会出现跳号，当前仍按 firstMonsterSequence+index+1 近似。
                var seed = (uint)(DungeonSharedServices.SeedGen.Next() & ~0x40000);
                session.Player.CurDungeonSeed = seed;
                var lcg = new DnfLcg(seed);
                session.Player.CurRoomLcg = lcg;
                var killedSet = new HashSet<ushort>();
                session.Player.CurRoomKilledSeqIds = killedSet;

                var hellRoomInfo = session.Player.CurDungeonHellRoomInfo;
                var isHellPartyRoom = session.Player.CurDungeonHellMode
                    && hellRoomInfo != null
                    && effectiveOverrideMapId == hellRoomInfo.MapId
                    && maze.X == hellRoomInfo.X
                    && maze.Y == hellRoomInfo.Y;

                var startMapMaze = isHellPartyRoom
                    ? BuildHellPartyStartMapMaze(session, maze, hellRoomInfo)
                    : maze;

                if (!isHellPartyRoom)
                    ApplyChampionPromotion(session, startMapMaze.Monsters);

                session.Player.CurRoomMonsters = startMapMaze.Monsters;

                session.Player.DungeonRoomStates[roomKey] = new RoomState
                {
                    Maze = startMapMaze,
                    FirstSeqId = session.Player.CurRoomStartSequence,
                    MonsterCount = (ushort)CountServerTrackedMonsters(startMapMaze, isHellPartyRoom ? hellRoomInfo : null),
                    KilledSeqIds = killedSet,
                    Seed = seed,
                    Lcg = lcg,
                };

                byte layeredFlag = (byte)(overrideMapId > 0 ? 1 : 0);

                if (isHellPartyRoom)
                {
                    var state = session.Player.DungeonRoomStates[roomKey];
                    state.IsHellPartyRoom = true;
                    state.HellPartyVeryDifficult = session.Player.CurDungeonVeryDifficultHell;
                    state.HellPartyPillarObjectCode = hellRoomInfo.PillarObjectCode;
                    state.HellPartySpawnX = hellRoomInfo.SpawnX;
                    state.HellPartySpawnY = hellRoomInfo.SpawnY;
                    state.HellPartyWaves = hellRoomInfo.Waves;
                    state.HellPartyPhase = HellPartyPhase.WaitingStart;
                    state.HellPartyGroupRemaining = BuildHellPartyGroupRemaining(startMapMaze.Monsters);
                    var difficultyRule = hellRoomInfo.DifficultyRule;
                    FileLogger.Log($"[DungeonHandler] HELLPARTY room initialized: pillar={state.HellPartyPillarObjectCode} spawn=({state.HellPartySpawnX},{state.HellPartySpawnY}) waves={state.HellPartyWaves?.Count ?? 0} tracked={state.MonsterCount}/{startMapMaze.Monsters.Count} rewardRolls={difficultyRule?.RewardRollCount ?? 0} probability={difficultyRule?.Probability ?? 0} ratioProbability={difficultyRule?.RatioProbability ?? 0} groups={FormatHellPartyGroups(state.HellPartyGroupRemaining)}");
                    hellPartyMonsterInfoAfterStartMap = BuildHellPartyMonsterInfoEntries(hellRoomInfo);
                }

                // df_game_r：掉落物序号使用独立随机计数，和怪物序号分离。
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
                var hellPartyMapMode = session.Player.CurDungeonHellMode ? session.Player.CurDungeonHellPartyMode : (byte)0;
                var startMapFogFlag = session.Player.CurDungeonHellMode ? (byte)1 : (byte)0;

                startMapBody = DungeonNotificationBuilder.BuildStartMap(startMapMaze, startSequence, (int)seed,
                    layeredRoomFlag: layeredFlag,
                    hellPartyMode: hellPartyMapMode,
                    hellPartyFogFlag: startMapFogFlag,
                    extraEntries: extraEntries,
                    ridableEntries: ridableForRoom);
                session.Player.CurMonsterCnt += (ushort)startMapMaze.Monsters.Count;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001D, startMapBody));

            if (hellPartyMonsterInfoAfterStartMap != null && hellPartyMonsterInfoAfterStartMap.Count > 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x02A6,
                    DungeonNotificationBuilder.BuildHellPartyMonsterInfo(hellPartyMonsterInfoAfterStartMap)));
                FileLogger.Log($"[DungeonHandler] HELLPARTY monster info sent after hell START_MAP: entries={hellPartyMonsterInfoAfterStartMap.Count} actorLevels={string.Join(",", hellPartyMonsterInfoAfterStartMap.Select(x => $"{x.Key}:{x.Value}"))}");
            }
        }

        private static void CacheQuestConnectedStartMapId(EnhancedClientSession session, DungeonData.MazeSumInfo maze)
        {
            var player = session?.Player;
            if (player == null || !player.CurMazeQuestConnected)
                return;
            if (maze.X != player.CurMazeStartX || maze.Y != player.CurMazeStartY || maze.Index <= 0)
                return;

            player.CurMazeStartMapId = maze.Index;
        }

        private static List<KeyValuePair<int, int>> BuildHellPartyMonsterInfoEntries(DungeonData.HellPartyRoomInfo hellRoomInfo)
        {
            var seen = new HashSet<int>();
            var result = new List<KeyValuePair<int, int>>();
            if (hellRoomInfo?.Waves != null && hellRoomInfo.Waves.Count > 0)
            {
                foreach (var wave in hellRoomInfo.Waves)
                {
                    if (wave?.Monsters == null)
                        continue;

                    foreach (var monster in wave.Monsters)
                    {
                        if (monster.Code <= 0 || seen.Contains(monster.Code))
                            continue;

                        seen.Add(monster.Code);
                        result.Add(new KeyValuePair<int, int>(monster.Code, Math.Max(1, (int)monster.Level)));
                    }
                }
            }

            return result;
        }

        private static DungeonData.MazeSumInfo BuildHellPartyStartMapMaze(
            EnhancedClientSession session,
            DungeonData.MazeSumInfo maze,
            DungeonData.HellPartyRoomInfo hellRoomInfo)
        {
            if (hellRoomInfo == null || hellRoomInfo.NormalMapId <= 0)
                return maze;

            try
            {
                var normalMaze = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    session.Player.CurDungeon,
                    hellRoomInfo.X,
                    hellRoomInfo.Y,
                    session.Player.CurMazeIndex,
                    hellRoomInfo.NormalMapId,
                    session.Player.CurBossMapPos);

                var monsters = new List<DungeonData.MonsterSumInfo>(
                    normalMaze.Monsters ?? new List<DungeonData.MonsterSumInfo>());
                ApplyChampionPromotion(session, monsters);
                var normalCount = monsters.Count;
                var hiddenCount = AppendHellPartyTemplateRows(monsters, hellRoomInfo);

                FileLogger.Log($"[DungeonHandler] HELLPARTY using normal room monsters: hellMap={maze.Index} normalMap={hellRoomInfo.NormalMapId} normal={normalCount} hidden={hiddenCount}");
                return new DungeonData.MazeSumInfo
                {
                    X = maze.X,
                    Y = maze.Y,
                    Index = maze.Index,
                    Monsters = monsters,
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] HELLPARTY normal room monster fallback: normalMap={hellRoomInfo.NormalMapId} error={ex.Message}");
            }

            return maze;
        }

        private static void ApplyChampionPromotion(EnhancedClientSession session, List<DungeonData.MonsterSumInfo> monsters)
        {
            if (monsters == null || monsters.Count == 0)
                return;

            var champCount = DungeonData.GetChampionCount(
                session.Player.CurDungeon,
                session.Player.CurDungeonDifficulty,
                session.Player.CurMazeIndex,
                DungeonSharedServices.SeedGen,
                out var namedMonsters);
            DungeonData.PromoteChampions(monsters, champCount, DungeonSharedServices.SeedGen, namedMonsters);
        }

        private static int AppendHellPartyTemplateRows(List<DungeonData.MonsterSumInfo> monsters, DungeonData.HellPartyRoomInfo hellRoomInfo)
        {
            if (monsters == null || hellRoomInfo?.Waves == null)
                return 0;

            var hiddenCount = 0;
            foreach (var wave in hellRoomInfo.Waves)
            {
                if (wave?.Monsters == null || wave.Monsters.Count == 0)
                    continue;

                var order = wave.Order > 0 && wave.Order <= ushort.MaxValue
                    ? (ushort)wave.Order
                    : (ushort)0;
                var waveIndex = HellPartyAttachAllWavesSelector;

                foreach (var monster in wave.Monsters)
                {
                    var template = monster;
                    template.TemplateOrder = order;
                    template.PacketIndex = null;
                    template.Flag0 = HellPartyHiddenTemplateFlag;
                    template.Flag1 = waveIndex;
                    template.ExtraState = 0;
                    monsters.Add(template);
                    hiddenCount++;
                }

                FileLogger.Log($"[DungeonHandler] HELLPARTY template wave: order={order} index={waveIndex} group={wave.GroupId} count={wave.Monsters.Count} rows={string.Join(",", wave.Monsters.Select(x => $"{x.Code}:{x.Type}:{x.Level}:{waveIndex}"))}");
            }

            return hiddenCount;
        }

        private static Dictionary<int, int> BuildHellPartyGroupRemaining(IReadOnlyList<DungeonData.MonsterSumInfo> monsters)
        {
            var result = new Dictionary<int, int>();
            if (monsters == null)
                return result;

            for (var i = 0; i < monsters.Count; i++)
            {
                var monster = monsters[i];
                if (!monster.IsHellPartyActor || monster.HellPartyGroupId <= 0)
                    continue;

                result.TryGetValue(monster.HellPartyGroupId, out var count);
                result[monster.HellPartyGroupId] = count + 1;
            }
            return result;
        }

        private static string FormatHellPartyGroups(Dictionary<int, int> groups)
        {
            if (groups == null || groups.Count == 0)
                return "-";

            return string.Join(",", groups.Select(x => $"{x.Key}={x.Value}"));
        }

        private static int CountServerTrackedMonsters(DungeonData.MazeSumInfo maze, DungeonData.HellPartyRoomInfo hellRoomInfo)
        {
            if (maze.Monsters == null)
                return 0;

            if (hellRoomInfo == null)
                return maze.Monsters.Count;

            return maze.Monsters.Count(monster => monster.Type != 9);
        }

        private static bool TryGetCurrentRoomState(EnhancedClientSession session, out RoomState roomState)
        {
            return session.Player.DungeonRoomStates.TryGetValue(session.Player.CurRoomKey, out roomState);
        }

        internal static bool IsCurrentHellPartyLocked(EnhancedClientSession session)
        {
            if (!TryGetCurrentRoomState(session, out var roomState) || !roomState.IsHellPartyRoom)
                return false;

            return roomState.HellPartyPhase == HellPartyPhase.Started && !roomState.IsCleared;
        }

        internal Task HandleHellPartyStart(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!TryGetCurrentRoomState(session, out var roomState) || !roomState.IsHellPartyRoom)
            {
                FileLogger.Log($"[DungeonHandler] HELLPARTY_START ignored: not in hell room cmd=0x{header.type:X4} bodyLen={body?.Length ?? 0}");
                return Task.CompletedTask;
            }

            if (roomState.HellPartyPhase == HellPartyPhase.WaitingStart)
            {
                roomState.HellPartyPhase = HellPartyPhase.Started;
                FileLogger.Log($"[DungeonHandler] HELLPARTY_START: room=({roomState.Maze.X},{roomState.Maze.Y}) tracked={roomState.MonsterCount}/{roomState.Maze.Monsters.Count}");
            }
            else
            {
                FileLogger.Log($"[DungeonHandler] HELLPARTY_START ignored: phase={roomState.HellPartyPhase}");
            }

            return Task.CompletedTask;
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
