using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Premium;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Pets;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonCombatHandler
    {
        private const int GrowthContractPremiumType = 84; // PVF premiumlist_new.etc: growth contract
        private const float GrowthContractMonsterBonusRate = 0.20f;

        private readonly DungeonSharedServices _svc;
        private readonly DungeonSettlementHandler _settlement;

        internal DungeonCombatHandler(DungeonSharedServices svc, DungeonSettlementHandler settlement)
        {
            _svc = svc;
            _settlement = settlement;
        }

        internal async Task HandleDieMonster(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;

            var req = DieMonsterRequest.Parse(body);

            if (run.Phase >= DungeonRunPhase.Cleared)
            {
                FileLogger.Log($"[DungeonHandler] DIE_MONSTER: post-clear seqId={req.LocalIndex}, ignored for exp");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0026,
                    DungeonNotificationBuilder.BuildMonsterDie(req.LocalIndex, null, session.Player.UserId)));
                return;
            }

            if (req.IsPassiveObject)
            {
                FileLogger.Log($"[DungeonHandler] DIE_MONSTER: passive object code={req.LocalIndex}");
                if (run.ClearCondition != null && run.ClearCondition.Check(0, req.LocalIndex))
                    await _settlement.TryClearDungeon(session, $"destroy object {req.LocalIndex}");
                await _svc.QuestDrops.CheckPassiveObjectDrop(session, req.LocalIndex);
                return;
            }

            if (!run.RoomKilledSeqIds.Add(req.LocalIndex))
            {
                FileLogger.Log($"[DungeonHandler] DIE_MONSTER: duplicate seqId={req.LocalIndex}, ignored");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0026,
                    DungeonNotificationBuilder.BuildMonsterDie(req.LocalIndex, null, session.Player.UserId)));
                return;
            }

            var roomLocalIndex = req.LocalIndex - run.RoomStartSequence;
            var monsters = run.RoomMonsters;

            if (roomLocalIndex < 0 || roomLocalIndex >= monsters.Count)
            {
                if (TryGetCurrentRoomState(session, out var outOfRangeRoomState) && outOfRangeRoomState.IsHellPartyRoom)
                {
                    FileLogger.Log($"[DungeonHandler] HELLPARTY DIE_MONSTER: seqId={req.LocalIndex} local={roomLocalIndex} source=out-of-start-map ignoredForClear tracked={outOfRangeRoomState.MonsterCount} killed={run.RoomKilledSeqIds.Count}");
                }
            }

            List<DropInfo> drops = null;
            byte killedMonsterType = 0;
            int killedMonsterCode = 0;
            if (roomLocalIndex >= 0 && roomLocalIndex < monsters.Count)
            {
                var monster = monsters[roomLocalIndex];
                killedMonsterType = monster.Type;
                killedMonsterCode = monster.Code;
                var monsterLevel = monster.Level;

                TryGetCurrentRoomState(session, out var dieRoomState);
                if (dieRoomState != null && dieRoomState.IsHellPartyRoom)
                {
                    var source = monster.Flag0 == 0 ? "normal" : "hell-hidden";
                    FileLogger.Log($"[DungeonHandler] HELLPARTY DIE_MONSTER: seqId={req.LocalIndex} local={roomLocalIndex} source={source} code={monster.Code} type={monster.Type} level={monster.Level} order={monster.TemplateOrder} flag0={monster.Flag0} flag1={monster.Flag1} group={monster.HellPartyGroupId} hellMonster={monster.IsHellMonsterScript} tracked={dieRoomState.MonsterCount} killed={run.RoomKilledSeqIds.Count}");
                }

                var weight = DungeonData.GetExperienceWeight(run.DungeonId);
                var rewardMonsterType = GetRewardMonsterType(monster.Type);
                var isBossMonster = IsBossActorType(monster.Type);
                var isChampionMonster = monster.Type == 1;
                var isNamedMonster = !isBossMonster && DungeonData.IsNamedMonster(run.DungeonId, monster.Code);
                var isSuperChampionMonster = monster.Type == 2 && !isNamedMonster;
                var baseExp = (uint)MonsterRewardTable.CalcBaseExp(monsterLevel, weight);
                var gainedExp = (uint)MonsterRewardTable.CalcExp(
                    monsterLevel,
                    weight,
                    run.Difficulty,
                    rewardMonsterType,
                    isNamedMonster);
                var growthContractBonusExp = CalculateGrowthContractMonsterBonus(session, gainedExp);
                var totalGainedExp = AddSaturating(gainedExp, growthContractBonusExp);

                int dungeonBasisLevel = monsterLevel;
                try { dungeonBasisLevel = DungeonData.GetDungeonBasicLv(run.DungeonId); } catch (Exception ex) { FileLogger.Log($"[DungeonHandler] DIE_MONSTER ERROR: basic level fallback dungeon={run.DungeonId} default={dungeonBasisLevel}: {ex.Message}"); }
                int dungeonMinimumLevel = dungeonBasisLevel;
                try { dungeonMinimumLevel = DungeonData.GetDungeonMinimumRequiredLevel(run.DungeonId); } catch (Exception ex) { FileLogger.Log($"[DungeonHandler] DIE_MONSTER ERROR: minimum level fallback dungeon={run.DungeonId} default={dungeonMinimumLevel}: {ex.Message}"); }
                int goldGained;
                List<DropInfo> generatedDrops;
                if (monster.IsHellPartyActor && dieRoomState != null && dieRoomState.IsHellPartyRoom)
                {
                    var abyssRequest = BuildAbyssPartyDropRequest(
                        dieRoomState, monster, dungeonMinimumLevel, dungeonBasisLevel);
                    generatedDrops = _svc.Drops.GenerateAbyssPartyAndRegister(run, abyssRequest);
                    goldGained = 0;
                    FileLogger.Log($"[DungeonHandler] ABYSS_PARTY_DROP: code={monster.Code} type={monster.Type} group={monster.HellPartyGroupId} abyssDifficulty={monster.HellPartyDifficulty} isLastGroup={abyssRequest.IsLastGroupMonster} abyssMonster={monster.IsHellMonsterScript} rewardRolls={monster.HellRewardRollCount} drops={generatedDrops.Count}");
                }
                else
                {
                    var dropRateLevel = run.HellMode ? dungeonBasisLevel : (int)monsterLevel;
                    var dropResult = _svc.Drops.GenerateAndRegister(run, new MonsterDropRequest
                    {
                        DropRateLevel = dropRateLevel,
                        MonsterType = rewardMonsterType,
                        MonsterCode = monster.Code,
                        DungeonBasisLevel = dungeonBasisLevel
                    });
                    goldGained = dropResult.GoldAmount;
                    generatedDrops = dropResult.Drops;
                }

                var prevLevel = session.Player.Level;
                var prevExp = session.Player.Exp;
                var honorExpGain = HonorLevelDataProvider.CalculateHonorExpGain(prevLevel, prevExp, totalGainedExp);
                var normalExpGain = totalGainedExp > honorExpGain ? totalGainedExp - honorExpGain : 0u;
                HonorLevelSummary honorSummary = null;
                var normalizedMaxExp = false;
                if (prevLevel >= ExpTableProvider.MaxLevel)
                {
                    var maxLevelEntryExp = (uint)Math.Max(0, ExpTableProvider.GetLevelThreshold(ExpTableProvider.MaxLevel - 1));
                    if (session.Player.Exp != maxLevelEntryExp)
                    {
                        session.Player.Exp = maxLevelEntryExp;
                        normalizedMaxExp = true;
                    }
                }
                else if (normalExpGain > 0)
                {
                    session.Player.Exp = AddSaturating(session.Player.Exp, normalExpGain);
                }
                if (honorExpGain > 0 && (session.Account?.AccountId ?? 0) > 0)
                {
                    honorSummary = _svc.HonorLevel.AddExp(session.Account.AccountId, honorExpGain);
                    FileLogger.Log($"[DungeonHandler] HONOR_EXP_GAIN monster: account={session.Account.AccountId} cid={session.Player.CharacterId} gain={honorExpGain}");
                }
                run.TotalExp += gainedExp;
                run.MonsterGrowthContractBonusExp =
                    AddSaturating(run.MonsterGrowthContractBonusExp, growthContractBonusExp);
                if (isBossMonster)
                    run.BossTotalExp += gainedExp;
                if (isChampionMonster)
                    run.ChampionTotalExp += gainedExp;
                if (isSuperChampionMonster)
                    run.SuperChampionTotalExp += gainedExp;
                if (isNamedMonster)
                    run.NamedMonsterTotalExp += gainedExp;
                run.TotalGold += goldGained;

                FileLogger.Log($"[DungeonHandler] DIE_MONSTER_EXP: seqId={req.LocalIndex} local={roomLocalIndex} code={monster.Code} type={monster.Type} level={monsterLevel} weight={weight:0.###} baseExp={baseExp} totalExp={gainedExp} growthContract={growthContractBonusExp} awardedExp={totalGainedExp} boss={isBossMonster} champion={isChampionMonster} superChampion={isSuperChampionMonster} named={isNamedMonster} dungeonTotalExp={run.TotalExp} bossTotalExp={run.BossTotalExp} championTotalExp={run.ChampionTotalExp} superChampionTotalExp={run.SuperChampionTotalExp} namedTotalExp={run.NamedMonsterTotalExp} monsterGrowthContractTotal={run.MonsterGrowthContractBonusExp}");

                if (generatedDrops != null && generatedDrops.Count > 0)
                {
                    drops = generatedDrops;
                    FileLogger.Log($"[DungeonHandler] DROP: {generatedDrops.Count} items, seqId={req.LocalIndex} seed={run.RoomLcg.Seed:X8}");
                }

                session.Player.Level = ExpTableProvider.ApplyLevelUps(session.Player.Level, session.Player.Exp);

                var leveledUp = session.Player.Level > prevLevel;
                if (leveledUp || normalizedMaxExp)
                {
                    CharacterProgressService.PersistLevelAndExp(session.Player.CharacterId, session.Player.Level, session.Player.Exp);
                }

                var (remainSp, remainTp) = _svc.GetRemainingSpTp(session, persist: leveledUp, logTag: "DIE_MONSTER");

                if (normalExpGain > 0 || leveledUp || honorExpGain > 0)
                {
                    honorSummary = _svc.ResolveHonorLevelForExp(session, honorSummary);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0025,
                        ExpNotificationBuilder.Build(session.Player.Level, session.Player.Exp, remainSp, remainTp, honorSummary,
                            premiumBonusExp: growthContractBonusExp)));
                }

                if (leveledUp)
                {
                    FileLogger.Log($"[DungeonHandler] LEVEL UP: cid={session.Player.CharacterId} {prevLevel}->{session.Player.Level} exp={session.Player.Exp}");
                    await _svc.SendInDungeonLevelUpFollowups(session);
                }

            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0026,
                DungeonNotificationBuilder.BuildMonsterDie(req.LocalIndex, drops, session.Player.UserId)));

            // Quest item drop (IDA: CUser::CheckQuestMonster, after DIE_MONSTER NOTI)
            await _svc.QuestDrops.CheckMonsterDrop(session, killedMonsterCode);

            // check_grid_clear (IDA 0x830A0E8): spawnType==100 && spawnFlag==0 blocks passage
            int blockingCount = 0;
            int killedBlockingCount = 0;
            for (var i = 0; i < monsters.Count; i++)
            {
                if (!monsters[i].IsBlocking)
                    continue;

                blockingCount++;
                var seqId = (ushort)(run.RoomStartSequence + i);
                if (run.RoomKilledSeqIds.Contains(seqId))
                    killedBlockingCount++;
            }
            bool roomCleared = killedBlockingCount >= blockingCount;

            // Old server kill_monster execution order (IDA 0x85A3AED):
            //   1. prepare_dungeon_clear (path B)
            //   2. ClearCondition(type, monsterCode) (path A)
            // Both paths call ClearDungeon, cleared_flag prevents duplicates

            // Path B: prepare_dungeon_clear (df_game_r 0x85AA598)
            // check_grid_clear -> ClearCondition(1, mapIndex) OR check_end_point -> ClearDungeon
            if (roomCleared)
            {
                TryGetCurrentRoomState(session, out var clearedRoomState);

                bool endPoint = false;
                if (clearedRoomState != null
                    && run.BossMapPos != null && run.BossMapPos.Length >= 2)
                {
                    endPoint = clearedRoomState.Maze.X == run.BossMapPos[0]
                            && clearedRoomState.Maze.Y == run.BossMapPos[1];
                }

                int currentMapId = clearedRoomState != null ? clearedRoomState.Maze.Index : 0;
                bool ccType1 = run.ClearCondition != null
                    && run.ClearCondition.Check(1, currentMapId);

                await PetCreatureRuntimeService.GrantRoomClearExperienceOnceAsync(session, clearedRoomState, 1);

                if (ccType1 || endPoint)
                    await _settlement.TryClearDungeon(session, $"prepare_dungeon_clear ccType1={ccType1} endPoint={endPoint}", killedMonsterCode);

                FileLogger.Log($"[DungeonHandler] ROOM CLEARED: dungeon={run.DungeonId} room=({run.RoomKey.X},{run.RoomKey.Y}) map={currentMapId} killedBlocking={killedBlockingCount}/{blockingCount} killedTotal={run.RoomKilledSeqIds.Count}");
                if (currentMapId > 0 && session.GameSession?.QuestManager != null)
                {
                    if (ShouldDeferQuestConnectedStartMapSync(session, currentMapId)
                        && session.GameSession.QuestManager.HasDeferredQuestConnectedStartMapClearQuest(
                            session.Player.CharacterId,
                            currentMapId))
                    {
                        FileLogger.Log($"[DungeonHandler] CLEAR_MAP deferred for quest-connected start room: dungeon={run.DungeonId} maze={run.MazeIndex} map={currentMapId}");
                    }
                    else
                    {
                        await session.GameSession.QuestManager.SyncClearMapQuestProgressAsync(0, currentMapId);
                    }
                }
            }

            if (roomCleared
                && TryGetCurrentRoomState(session, out var currentRoomState)
                && currentRoomState.IsHellPartyRoom
                && currentRoomState.HellPartyPhase == HellPartyPhase.Started)
            {
                currentRoomState.HellPartyPhase = HellPartyPhase.Complete;
                FileLogger.Log("[DungeonHandler] HELLPARTY complete: tracked monsters cleared");
            }

            // Path A: ClearCondition(type, monsterCode) (df_game_r kill_monster tail)
            // monsterType -> conditionType: boss(3) / AI boss(8)->4, APC(5-7)->3, normal->2
            if (run.ClearCondition != null)
            {
                int ccType = IsBossActorType(killedMonsterType) ? 4 : (killedMonsterType >= 5 ? 3 : 2);
                if (run.ClearCondition.Check(ccType, killedMonsterCode))
                    await _settlement.TryClearDungeon(session, $"ClearCondition type={ccType} target={killedMonsterCode}", killedMonsterCode);
            }
        }

        private static bool IsBossActorType(byte monsterType)
        {
            return monsterType == 3 || monsterType == 8;
        }

        private static int GetRewardMonsterType(byte monsterType)
        {
            return monsterType == 8 ? 3 : monsterType;
        }

        private static bool TryGetCurrentRoomState(EnhancedClientSession session, out RoomState roomState)
        {
            var run = session.Player.CurrentRun;
            if (run == null)
            {
                roomState = null;
                return false;
            }

            return run.RoomStates.TryGetValue(run.RoomKey, out roomState);
        }

        private static bool ShouldDeferQuestConnectedStartMapSync(EnhancedClientSession session, int currentMapId)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || !run.MazeQuestConnected)
                return false;
            if (run.MazeStartMapId <= 0 || run.MazeStartMapId != currentMapId)
                return false;
            return run.RoomKey.X == run.MazeStartX
                && run.RoomKey.Y == run.MazeStartY;
        }

        private static AbyssPartyDropRequest BuildAbyssPartyDropRequest(
            RoomState roomState,
            DungeonData.MonsterSumInfo monster,
            int dungeonMinimumLevel,
            int dungeonBasisLevel)
        {
            var isLastGroupMonster = false;
            if (roomState.HellPartyGroupRemaining != null
                && monster.HellPartyGroupId > 0
                && roomState.HellPartyGroupRemaining.TryGetValue(monster.HellPartyGroupId, out var remaining))
            {
                var after = Math.Max(0, remaining - 1);
                if (after == 0)
                {
                    roomState.HellPartyGroupRemaining.Remove(monster.HellPartyGroupId);
                    isLastGroupMonster = true;
                }
                else
                {
                    roomState.HellPartyGroupRemaining[monster.HellPartyGroupId] = after;
                }
            }

            return new AbyssPartyDropRequest
            {
                MonsterCode = monster.Code,
                DungeonMinimumLevel = dungeonMinimumLevel,
                DungeonBasisLevel = dungeonBasisLevel,
                AbyssPartyDifficulty = monster.HellPartyDifficulty,
                RewardRollCount = monster.HellRewardRollCount,
                IsLastGroupMonster = isLastGroupMonster,
                IsAbyssMonsterScript = monster.IsHellMonsterScript
            };
        }

        internal async Task HandleDieCharacter(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DIE_CHARACTER: uid={session.Player.UserId} body={BitConverter.ToString(body)}");
            // NOTI 32 (wire 0x0020) DIE_STATE: u16 actorId + u8 dieType(0=death) + u8 flag
            var w = new GamePacketWriter();
            w.WriteUInt16(session.Player.UserId);
            w.WriteByte(0x00);  // dieType=0 death confirmed
            w.WriteByte(0x00);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0020, w.ToArray()));
        }

        private static uint CalculateGrowthContractMonsterBonus(EnhancedClientSession session, uint baseMonsterExp)
        {
            if (baseMonsterExp == 0)
                return 0;

            var accountId = session.Account?.AccountId ?? 0;
            var connStr = SqliteDatabaseBootstrap.Initialize(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            return PremiumService.HasActivePremium(connStr, accountId, GrowthContractPremiumType)
                ? ToUInt32Floor(baseMonsterExp * GrowthContractMonsterBonusRate)
                : 0;
        }

        private static uint ToUInt32Floor(float value)
        {
            if (value <= 0)
                return 0;
            return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private static uint AddSaturating(uint current, uint add)
        {
            var value = (ulong)current + add;
            return value > uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        internal async Task HandleUseCoin(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            // df_game_r: read = u16 targetActorId
            ushort targetId = body.Length >= 2 ? BitConverter.ToUInt16(body, 0) : session.Player.UserId;
            var characterId = session.Player?.CharacterId ?? 0;
            var accountId = session.Account?.AccountId ?? 1;
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] USE_COIN: uid={session.Player.UserId} target={targetId} cid={characterId}");

            // 先扣复活币, 成功才发复活通知(旧实现不扣币白送复活)
            short coinSlot;
            int coinRemaining;
            if (characterId <= 0 || !_svc.ReviveCoin.TryConsume(characterId, accountId, out coinSlot, out coinRemaining))
            {
                var err = new GamePacketWriter();
                err.WriteByte(0x00);
                err.WriteUInt16(targetId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0029, err.ToArray()));
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] USE_COIN: no coin cid={characterId}");
                return;
            }

            // 1. NOTI 0x0020 DIE_STATE: set_charac_live(user, 1=revive)
            //    df_game_r body = u16 actorId + u8 state; 86JP has extra u8 flag
            var noti = new GamePacketWriter();
            noti.WriteUInt16(targetId);
            noti.WriteByte(0x01);  // state=1 revive
            noti.WriteByte(0x00);  // 86JP flag
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0020, noti.ToArray()));

            // 2. CMD ACK 0x0029: resultCode=1 + u16 targetActorId
            //    不补发 0x000E: 客户端使用复活币时本地已预扣显示(PR#338 实测说明), 全量列表随下次进城刷新
            var ack = new GamePacketWriter();
            ack.WriteByte(0x01);           // resultCode = success
            ack.WriteUInt16(targetId);     // targetActorId
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0029, ack.ToArray()));
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] USE_COIN: OK cid={characterId} slot={coinSlot} remaining={coinRemaining}");
        }

        internal async Task HandleGetItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;

            var req = GetItemRequest.Parse(body);
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: cid={session.Player.CharacterId} srcSlot={req.SrcSlot}");

            var accountId = session.Account?.AccountId ?? 1;
            var pickup = _svc.Drops.TryPickup(run, req.SrcSlot, session.Player.CharacterId, accountId);

            if (!pickup.Success)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: {pickup.FailReason} srcSlot={req.SrcSlot}");
                return;
            }

            if (pickup.IsGold)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0027,
                    DropItemBuilder.BuildPickupGold(req.SrcSlot, session.Player.UserId, pickup.GoldAmount, pickup.ExtraGold)));
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: gold pickup srcSlot={req.SrcSlot} gold={pickup.GoldAmount} extra={pickup.ExtraGold}");
            }
            else
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0027,
                    DropItemBuilder.BuildPickupItem(req.SrcSlot, session.Player.UserId, (ushort)pickup.InventorySlot, 7)));
                if (session.GameSession?.QuestManager != null && pickup.PickedUpItemId > 0)
                    await session.GameSession.QuestManager.SyncItemSeekingQuestProgressAsync(
                        new[] { pickup.PickedUpItemId });
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: item pickup srcSlot={req.SrcSlot} templateId={pickup.PickedUpItemId} invSlot={pickup.InventorySlot}");
            }
        }
    }
}
