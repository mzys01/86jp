using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
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
        private const int GorgeousChallengeGoldCost = 190000;

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
                    var record = _svc.CharacterRepository.GetById(cid);
                    var addition = _svc.Subtype1Repository.HasData(cid) ? _svc.Subtype1Repository.Load(cid) : null;
                    if (record != null && addition != null)
                    {
                        var accountId = session.Account?.AccountId ?? record.AccountId;
                        var accountCharacters = _svc.CharacterRepository.ListByAccount(accountId);
                        AdventureGroupUserInfoSynchronizer.ApplyToUserInfoAddition(addition, accountCharacters);
                        _svc.HonorLevel.ApplyToUserInfoAddition(addition, accountId, accountCharacters);
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

            // 塔类副本分流: dungeonKind==1 走专属流程(NOTI 142+143, 非普通副本的 START_MAP)
            if (_svc.DeathTower.TryCreateSession(req.DungeonId, out var tower))
            {
                DungeonRunLifecycle.BeginTowerRun(session, req.DungeonId, tower, req.Difficulty);
                await _svc.DeathTower.SendEntryPacketsAsync(session, tower, req.Difficulty);
                return;
            }

            DungeonRunLifecycle.BeginRun(session, req.DungeonId, req.Difficulty);
            var run = session.Player.CurrentRun;
            run.HellMode = req.HellPartyRequestFlag != 0 && DungeonData.IsHellDungeon(req.DungeonId);

            WarmUpDropConfigs(run.HellMode);

            if (req.HellPartyRequestFlag != 0)
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: manual hell requested dungeon={req.DungeonId} enabled={run.HellMode}");

            HashSet<int> activeQuestIds = null;
            HashSet<int> clearedQuestIds = null;
            try
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var quests = QuestService.LoadActiveQuests(connStr, session.Player.CharacterId);
                if (quests.Count > 0)
                    activeQuestIds = new HashSet<int>(quests.ConvertAll(q => (int)q.QuestId));
                var clearedFlags = new Game.Quests.QuestRepository(connStr)
                    .LoadClearedFlags(session.Player.CharacterId);
                if (clearedFlags.Count > 0)
                    clearedQuestIds = new HashSet<int>(clearedFlags.Keys);
            }
            catch (Exception ex) { FileLogger.Log($"[DungeonHandler] SELECT_DUNGEON ERROR: quest load failed: {ex.Message}"); }
            var selection = DungeonData.SelectDungeonMaze(req.DungeonId, req.Difficulty, activeQuestIds, clearedQuestIds);
            run.MazeIndex = selection.Index;
            run.MazeQuestConnected = selection.Maze.QuestConnection != null
                && selection.Maze.QuestConnection.Length >= 2;
            run.MazeStartX = selection.Maze.StartMap != null && selection.Maze.StartMap.Length >= 2
                ? selection.Maze.StartMap[0]
                : -1;
            run.MazeStartY = selection.Maze.StartMap != null && selection.Maze.StartMap.Length >= 2
                ? selection.Maze.StartMap[1]
                : -1;
            run.MazeStartMapId = ResolveMazeStartMapId(selection.Maze);
            var bossPos = DungeonData.RandomizeBossPosition(selection.Maze.BossMap);
            run.BossMapPos = bossPos;
            run.RidableObjects = DungeonMapHandler.InitRidableObjects(selection.Maze);
            run.ClearCondition = new ClearConditionState(selection.Maze.ClearConditions);
            if (run.HellMode)
                await PrepareManualHellPartyAsync(session, req, selection.Maze, selection.Index);

            if (run.ClearCondition.HasConditions)
                FileLogger.Log($"[DungeonHandler] ClearCondition init: {selection.Maze.ClearConditions.Count} conditions, totalRequired={run.ClearCondition.TotalRequired}");
            else
                FileLogger.Log($"[DungeonHandler] WARNING: dungeon={req.DungeonId} maze={selection.Index} has no [clear condition]");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001C, DungeonNotificationBuilder.BuildDungeonInfo(
                dungeonId: req.DungeonId,
                difficulty: req.Difficulty,
                modeFlag: (byte)selection.Index,
                bossX: bossPos != null ? (byte)bossPos[0] : (byte)0,
                bossY: bossPos != null ? (byte)bossPos[1] : (byte)0,
                hellPartyRoomX: run.HellMode ? run.HellMapX : (byte)0xFF,
                hellPartyRoomY: run.HellMode ? run.HellMapY : (byte)0xFF,
                dungeonMode: 0,
                hellPartyEnabled: run.HellMode ? (ushort)1 : (ushort)0,
                value2: run.HellMode ? (byte)0x0B : (byte)0)));

            await _mapHandler.SendStartMapAsync(session, 0xFF, 0xFF, overrideMapId: -1);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0117, BitConverter.GetBytes(session.Player.CharacterId)));
            if (StrikerSupportTagCharacterPacketBuilder.TryBuildDungeonOwnerMappedSupportBody(session.Player.CharacterId, out var strikerBody))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x019F, strikerBody));
            }
            else
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x019F, new byte[] { 0x00, 0x00 }));
            }
        }

        internal Task HandleGorgeousChallengeToggle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (session?.Player == null)
                return Task.CompletedTask;

            var enabled = ParseGorgeousChallengeEnabled(body);
            session.Player.HellPartyGorgeousChallengeEnabled = enabled;
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GORGEOUS_CHALLENGE_TOGGLE: enabled={enabled} cmd=0x{header.cmd:X2} type=0x{header.type:X4} bodyLen={body?.Length ?? 0} body={(body != null ? BitConverter.ToString(body) : string.Empty)}");
            return Task.CompletedTask;
        }

        private static byte ResolveHellPartyMode(byte requestFlag)
        {
            if (requestFlag == 1 || requestFlag == 2)
                return requestFlag;

            return HellPartyData.PickManualHellPartyMode();
        }

        private static bool ParseGorgeousChallengeEnabled(byte[] body)
        {
            if (body == null || body.Length <= 13)
                return false;

            // 86 client CMD 0x03B6: body[12] is always 7; body[13] is 0 for checked, 1 for unchecked.
            return body[13] == 0;
        }

        private static int ResolveMazeStartMapId(PvfLib.MazeInfo maze)
        {
            if (maze == null
                || maze.StartMap == null
                || maze.StartMap.Length < 2
                || maze.MapSpecifications == null)
                return 0;

            var startX = maze.StartMap[0];
            var startY = maze.StartMap[1];
            foreach (var spec in maze.MapSpecifications)
            {
                if (spec.X != startX || spec.Y != startY || spec.Index <= 0)
                    continue;
                return spec.Index;
            }

            return 0;
        }

        private async Task PrepareManualHellPartyAsync(
            EnhancedClientSession session,
            Network.Parsers.Dungeon.SelectDungeonRequest req,
            PvfLib.MazeInfo maze,
            int mazeIndex)
        {
            var run = session.Player.CurrentRun;
            var area = WorldMap.GetAreaByDungeonId(req.DungeonId);
            var dungeonMinLevel = DungeonData.GetDungeonMinimumRequiredLevel(req.DungeonId);
            var hellPartyMode = ResolveHellPartyMode(req.HellPartyDifficultyFlag);
            DungeonData.HellPartyRoomInfo hellRoom = null;
            var gorgeousGoldBefore = 0;
            var gorgeousGoldAfter = -1;
            var gorgeousCanApply = false;

            if (session.Player.HellPartyGorgeousChallengeEnabled)
            {
                var veryHardRoom = DungeonData.FindHellMapRoom(req.DungeonId, maze, mazeIndex, 1);
                gorgeousGoldBefore = ReadGold(
                    session.Player.CharacterId,
                    session.Account?.AccountId ?? 1);
                if (veryHardRoom.Found && gorgeousGoldBefore >= GorgeousChallengeGoldCost)
                {
                    hellPartyMode = 1;
                    hellRoom = veryHardRoom;
                    gorgeousCanApply = true;
                }
                else
                {
                    hellPartyMode = HellPartyData.PickManualHellPartyMode();
                    if (!veryHardRoom.Found)
                        FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: gorgeous challenge ignored, very hard hell room missing dungeon={req.DungeonId}");
                    else
                        FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: gorgeous challenge insufficient gold need={GorgeousChallengeGoldCost} have={gorgeousGoldBefore}, use weighted hell difficulty mode={hellPartyMode}");
                }
            }

            if (hellRoom == null || !hellRoom.Found)
                hellRoom = DungeonData.FindHellMapRoom(req.DungeonId, maze, mazeIndex, hellPartyMode);

            if (hellRoom == null || !hellRoom.Found)
            {
                DisableCurrentHellParty(session);
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: hell requested but no hell map found dungeon={req.DungeonId}");
                return;
            }

            var ticketResult = _svc.EntryCost.TryConsumeAbyssPartyTicket(
                session.Player.CharacterId, session.Account?.AccountId ?? 1, area, dungeonMinLevel);
            if (!ticketResult.Success)
            {
                DisableCurrentHellParty(session);
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: hell ticket check failed dungeon={req.DungeonId} area={area?.AreaId ?? -1} minLevel={dungeonMinLevel} reason={ticketResult.FailReason}");
                return;
            }

            var gorgeousApplied = false;
            if (gorgeousCanApply)
            {
                if (TrySpendGold(session.Player.CharacterId, session.Account?.AccountId ?? 1, GorgeousChallengeGoldCost, out gorgeousGoldBefore, out gorgeousGoldAfter))
                {
                    gorgeousApplied = true;
                    run.HellGorgeousChallenge = true;
                }
                else
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: gorgeous challenge spend failed after ticket, keep selected hell mode need={GorgeousChallengeGoldCost} have={gorgeousGoldBefore}");
                }
            }

            run.HellPartyMode = hellPartyMode;
            run.VeryDifficultHell = run.HellPartyMode == 1;
            run.HellMapId = hellRoom.MapId;
            run.HellMapX = (byte)hellRoom.X;
            run.HellMapY = (byte)hellRoom.Y;
            run.HellRoomInfo = hellRoom;

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: hell room=({hellRoom.X},{hellRoom.Y}) map={hellRoom.MapId} normalMap={hellRoom.NormalMapId} waves={hellRoom.Waves.Count} requestFlag={req.HellPartyRequestFlag} difficultyFlag={req.HellPartyDifficultyFlag} mode={run.HellPartyMode} veryDifficult={run.VeryDifficultHell} area={area?.AreaId ?? -1} minLevel={dungeonMinLevel} ticket={(ticketResult.IsFreePass ? "freepass" : "normal")} updates={ticketResult.ConsumedItems.Count}");

            await SendHellPartyTicketUpdates(session, ticketResult);
            if (gorgeousApplied && gorgeousGoldAfter >= 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                    ItemListUpdateBuilder.BuildGoldUpdate(gorgeousGoldAfter)));
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: gorgeous challenge applied cost={GorgeousChallengeGoldCost} gold={gorgeousGoldBefore}->{gorgeousGoldAfter}");
            }
        }

        private static async Task SendHellPartyTicketUpdates(
            EnhancedClientSession session,
            EntryCostResult ticketResult)
        {
            foreach (var update in ticketResult.ConsumedItems)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                    ItemListUpdateBuilder.BuildCommonSlotUpdate(update.SlotIndex, update.ItemId, update.RemainingCount)));
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: hell ticket consumed item={update.ItemId} count={update.Count} slot={update.SlotIndex} remain={update.RemainingCount}");
            }
        }

        private static void DisableCurrentHellParty(EnhancedClientSession session)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;
            run.HellMode = false;
            run.HellPartyMode = 0;
            run.VeryDifficultHell = false;
            run.HellGorgeousChallenge = false;
            run.HellMapId = -1;
            run.HellMapX = 0xFF;
            run.HellMapY = 0xFF;
            run.HellRoomInfo = null;
        }

        private static void WarmUpDropConfigs(bool includeHellParty)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (includeHellParty)
                        DropService.WarmUpAbyssParty();
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DROP_CONFIG_WARMUP ERROR: {ex.Message}");
                }
            });
        }

        private int ReadGold(int characterId, int accountId)
        {
            try
            {
                using (var scope = _svc.AssetService.OpenScope(characterId, accountId))
                    return _svc.AssetService.LoadWallet(scope).Gold;
            }
            catch (Exception ex) { FileLogger.Log($"[DungeonEntry] ReadGold ERROR: cid={characterId}: {ex.Message}"); return 0; }
        }

        private bool TrySpendGold(int characterId, int accountId, int goldCost, out int currentGold, out int updatedGold)
        {
            currentGold = 0;
            updatedGold = 0;
            try
            {
                using (var scope = _svc.AssetService.OpenScope(characterId, accountId))
                {
                    var wallet = _svc.AssetService.LoadWallet(scope);
                    currentGold = wallet.Gold;
                    updatedGold = wallet.Gold;
                    if (!_svc.AssetService.TrySpendGold(scope, goldCost))
                        return false;
                    updatedGold = wallet.Gold - goldCost;
                    scope.Commit();
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonEntry] TrySpendGold ERROR: {ex.Message}");
                return false;
            }
        }
    }
}
