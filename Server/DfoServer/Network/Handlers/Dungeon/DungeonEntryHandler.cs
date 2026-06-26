using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonEntryHandler
    {
        private readonly DungeonSharedServices _svc;
        private readonly DungeonMapHandler _mapHandler;

        internal DungeonEntryHandler(DungeonSharedServices svc, DungeonMapHandler mapHandler)
        {
            _svc = svc;
            _mapHandler = mapHandler;
        }

        internal async Task HandleEnterSelectDungeon(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON: cid={session.Player.CharacterId} uid={session.Player.UserId} town={session.Player.CurTownId} area={session.Player.CurAreaId}");
            try
            {
                session.Player.UserState = 0x01;

                var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);
                snapshot.AreaId = 0xFF;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017, TownAreaNotificationBuilder.BuildUserArea(snapshot)));

                // NOTI 0x0002 subtype1 (ADDITION): dynamically built from structured table (same path as init flow)
                int cid = session.Player.CharacterId;
                if (cid <= 0)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON ERROR: CharacterId<=0, USERINFO not sent");
                }
                else
                {
                    var charRepo = new SqliteCharacterRepository(
                        ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                    var record = charRepo.GetById(cid);
                    var subtype1Repo = new SqliteSubtype1Repository(
                        ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                    var addition = subtype1Repo.HasData(cid) ? subtype1Repo.Load(cid) : null;
                    if (record != null && addition != null)
                    {
                        var skillSnap = _svc.LoadSyncedSkillState(cid, record.Level).Skills;
                        var w = new GamePacketWriter();
                        w.WriteByte(1); // subtype 1 ADDITION
                        w.WriteUInt16(1);
                        w.WriteUInt16((ushort)record.CharacterId);
                        w.WriteBytes(UserInfoSubtype1Builder.BuildFromSnapshot(addition, skillSnap));
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, w.ToArray()));
                        FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON: NOTI 2 type1 dynamic body");
                    }
                    else
                    {
                        FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON ERROR: record={record != null} addition={addition != null}, USERINFO not sent (no fallback)");
                    }
                }
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0003, EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001A, UdpHostBuilder.BuildUnavailable()));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001B, EnterSelectDungeonStateBuilder.BuildEnterSelectDungeon(session.Player)));
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON: 5 packets sent OK");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON EXCEPTION: {ex}");
            }
        }

        internal async Task HandleSelectDungeon(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var req = Network.Parsers.Dungeon.SelectDungeonRequest.Parse(body);

            session.Player.CurDungeon = (short)req.DungeonId;
            session.Player.CurDungeonDifficulty = req.Difficulty;
            session.Player.CurDungeonFlag1 = req.Flag1;
            session.Player.CurDungeonFlag2 = req.Flag2;
            session.Player.CurMonsterCnt = 0;
            session.Player.CurLayeredMapIndex = -1;
            session.Player.CurMoveMapU15 = 0;
            session.Player.CurMoveMapU19 = 0;
            session.Player.CurDungeonTotalExp = 0;
            session.Player.CurDungeonTotalGold = 0;
            session.Player.CurSceneSlotCounter = 0;
            session.Player.CurDungeonDrops.Clear();
            session.Player.CurRoomKilledSeqIds.Clear();
            session.Player.DungeonRoomStates.Clear();
            session.Player.CurDungeonRidableObjects.Clear();
            session.Player.CurBossKilled = false;
            session.Player.CurBossCode = 0;

            HashSet<int> activeQuestIds = null;
            try
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var quests = QuestService.LoadActiveQuests(connStr, session.Player.CharacterId);
                if (quests.Count > 0)
                    activeQuestIds = new HashSet<int>(quests.ConvertAll(q => (int)q.QuestId));
            }
            catch { }
            var selection = DungeonData.SelectDungeonMaze(req.DungeonId, activeQuestIds);
            session.Player.CurMazeIndex = selection.Index;
            var bossPos = DungeonData.RandomizeBossPosition(selection.Maze.BossMap);
            session.Player.CurBossMapPos = bossPos;
            session.Player.CurDungeonRidableObjects = DungeonMapHandler.InitRidableObjects(selection.Maze);
            session.Player.CurClearCondition = new ClearConditionState(selection.Maze.ClearConditions);
            if (session.Player.CurClearCondition.HasConditions)
                FileLogger.Log($"[DungeonHandler] ClearCondition init: {selection.Maze.ClearConditions.Count} conditions, totalRequired={session.Player.CurClearCondition.TotalRequired}");
            else
                FileLogger.Log($"[DungeonHandler] WARNING: dungeon={req.DungeonId} maze={selection.Index} has no [clear condition]");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001C, DungeonNotificationBuilder.BuildDungeonInfo(
                dungeonId: req.DungeonId,
                difficulty: req.Difficulty,
                modeFlag: (byte)selection.Index,
                bossX: bossPos != null ? (byte)bossPos[0] : (byte)0,
                bossY: bossPos != null ? (byte)bossPos[1] : (byte)0)));

            await _mapHandler.SendStartMapAsync(session, 0xFF, 0xFF, overrideMapId: -1);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0117, BitConverter.GetBytes(session.Player.CharacterId)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x019F, new byte[] { 0x00, 0x00 }));
        }
    }
}
