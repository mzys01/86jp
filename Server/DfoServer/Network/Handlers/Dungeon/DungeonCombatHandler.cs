using DfoServer.Game.Dungeon;
using DfoServer.Game.Premium;
using DfoServer.Game.Skills;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
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
            var req = DieMonsterRequest.Parse(body);

            if (session.Player.CurDungeonCleared)
            {
                FileLogger.Log($"[DungeonHandler] DIE_MONSTER: post-clear seqId={req.LocalIndex}, ignored for exp");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0026,
                    DungeonNotificationBuilder.BuildMonsterDie(req.LocalIndex, null, session.Player.UserId)));
                return;
            }

            if (req.IsPassiveObject)
            {
                FileLogger.Log($"[DungeonHandler] DIE_MONSTER: passive object code={req.LocalIndex}");
                if (session.Player.CurClearCondition != null && session.Player.CurClearCondition.Check(0, req.LocalIndex))
                    await _settlement.TryClearDungeon(session, $"destroy object {req.LocalIndex}");
                return;
            }

            if (!session.Player.CurRoomKilledSeqIds.Add(req.LocalIndex))
            {
                FileLogger.Log($"[DungeonHandler] DIE_MONSTER: duplicate seqId={req.LocalIndex}, ignored");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0026,
                    DungeonNotificationBuilder.BuildMonsterDie(req.LocalIndex, null, session.Player.UserId)));
                return;
            }

            var roomLocalIndex = req.LocalIndex - session.Player.CurRoomStartSequence;
            var monsters = session.Player.CurRoomMonsters;

            if (roomLocalIndex < 0 || roomLocalIndex >= monsters.Count)
            {
                if (TryGetCurrentRoomState(session, out var outOfRangeRoomState) && outOfRangeRoomState.IsHellPartyRoom)
                {
                    FileLogger.Log($"[DungeonHandler] HELLPARTY DIE_MONSTER: seqId={req.LocalIndex} local={roomLocalIndex} source=out-of-start-map ignoredForClear tracked={outOfRangeRoomState.MonsterCount} killed={session.Player.CurRoomKilledSeqIds.Count}");
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
                    FileLogger.Log($"[DungeonHandler] HELLPARTY DIE_MONSTER: seqId={req.LocalIndex} local={roomLocalIndex} source={source} code={monster.Code} type={monster.Type} level={monster.Level} order={monster.TemplateOrder} flag0={monster.Flag0} flag1={monster.Flag1} group={monster.HellPartyGroupId} hellMonster={monster.IsHellMonsterScript} tracked={dieRoomState.MonsterCount} killed={session.Player.CurRoomKilledSeqIds.Count}");
                }

                var weight = DungeonData.GetExperienceWeight(session.Player.CurDungeon);
                var rewardMonsterType = GetRewardMonsterType(monster.Type);
                var isBossMonster = IsBossActorType(monster.Type);
                var isChampionMonster = monster.Type == 1;
                var isNamedMonster = !isBossMonster && DungeonData.IsNamedMonster(session.Player.CurDungeon, monster.Code);
                var isSuperChampionMonster = monster.Type == 2 && !isNamedMonster;
                var baseExp = (uint)MonsterRewardTable.CalcBaseExp(monsterLevel, weight);
                var gainedExp = (uint)MonsterRewardTable.CalcExp(
                    monsterLevel,
                    weight,
                    session.Player.CurDungeonDifficulty,
                    rewardMonsterType,
                    isNamedMonster);
                var growthContractBonusExp = CalculateGrowthContractMonsterBonus(session, gainedExp);
                var totalGainedExp = AddSaturating(gainedExp, growthContractBonusExp);

                var slotCounter = session.Player.CurSceneSlotCounter;
                int dungeonBasisLevel = monsterLevel;
                try { dungeonBasisLevel = DungeonData.GetDungeonBasicLv(session.Player.CurDungeon); } catch { }
                int dungeonMinimumLevel = dungeonBasisLevel;
                try { dungeonMinimumLevel = DungeonData.GetDungeonMinimumRequiredLevel(session.Player.CurDungeon); } catch { }
                int goldGained;
                List<DropInfo> generatedDrops;
                if (monster.IsHellPartyActor && dieRoomState != null && dieRoomState.IsHellPartyRoom)
                {
                    generatedDrops = GenerateHellPartyActorDrops(
                        session,
                        dieRoomState,
                        monster,
                        dungeonMinimumLevel,
                        dungeonBasisLevel,
                        ref slotCounter);
                    goldGained = 0;
                }
                else
                {
                    var dropPool = MonsterDropTable.GetDropPool(monster.Code);

                    // Area material is added by the server to the normal monster item pool; hidden hell actors do not use this path.
                    int areaMaterialId = AreaMaterialDropProvider.GetAreaMaterialItem(session.Player.CurDungeon);
                    if (areaMaterialId > 0)
                    {
                        var extended = new List<MonsterDropTable.DropPoolEntry>();
                        if (dropPool != null) extended.AddRange(dropPool);
                        extended.Add(new MonsterDropTable.DropPoolEntry { ItemId = areaMaterialId, Weight = 100 });
                        dropPool = extended;
                    }

                    var generator = new DropGenerator(session.Player.CurRoomLcg);
                    var dropRateLevel = session.Player.CurDungeonHellMode ? dungeonBasisLevel : (int)monsterLevel;
                    var result = generator.GenerateMonsterDrops(dropRateLevel, rewardMonsterType, monster.Code, session.Player.CurDungeonDifficulty, dungeonBasisLevel, ref slotCounter, dropPool);
                    goldGained = result.goldAmount;
                    generatedDrops = result.drops;
                }

                session.Player.CurSceneSlotCounter = slotCounter;

                session.Player.Exp = AddSaturating(session.Player.Exp, totalGainedExp);
                session.Player.CurDungeonTotalExp += gainedExp;
                session.Player.CurDungeonMonsterGrowthContractBonusExp =
                    AddSaturating(session.Player.CurDungeonMonsterGrowthContractBonusExp, growthContractBonusExp);
                if (isBossMonster)
                    session.Player.CurDungeonBossTotalExp += gainedExp;
                if (isChampionMonster)
                    session.Player.CurDungeonChampionTotalExp += gainedExp;
                if (isSuperChampionMonster)
                    session.Player.CurDungeonSuperChampionTotalExp += gainedExp;
                if (isNamedMonster)
                    session.Player.CurDungeonNamedMonsterTotalExp += gainedExp;
                session.Player.CurDungeonTotalGold += goldGained;

                FileLogger.Log($"[DungeonHandler] DIE_MONSTER_EXP: seqId={req.LocalIndex} local={roomLocalIndex} code={monster.Code} type={monster.Type} level={monsterLevel} weight={weight:0.###} baseExp={baseExp} totalExp={gainedExp} growthContract={growthContractBonusExp} awardedExp={totalGainedExp} boss={isBossMonster} champion={isChampionMonster} superChampion={isSuperChampionMonster} named={isNamedMonster} dungeonTotalExp={session.Player.CurDungeonTotalExp} bossTotalExp={session.Player.CurDungeonBossTotalExp} championTotalExp={session.Player.CurDungeonChampionTotalExp} superChampionTotalExp={session.Player.CurDungeonSuperChampionTotalExp} namedTotalExp={session.Player.CurDungeonNamedMonsterTotalExp} monsterGrowthContractTotal={session.Player.CurDungeonMonsterGrowthContractBonusExp}");

                if (generatedDrops.Count > 0)
                {
                    foreach (var drop in generatedDrops)
                        session.Player.CurDungeonDrops[drop.SceneSlot] = drop;
                    drops = generatedDrops;
                    FileLogger.Log($"[DungeonHandler] DROP: {generatedDrops.Count} items, seqId={req.LocalIndex} seed={session.Player.CurRoomLcg.Seed:X8}");
                }

                var prevLevel = session.Player.Level;
                while (session.Player.Level < 86 && session.Player.Exp >= (uint)ExpTableProvider.GetLevelThreshold(session.Player.Level))
                    session.Player.Level++;

                var leveledUp = session.Player.Level > prevLevel;
                if (leveledUp)
                {
                    _svc.PersistLevelAndExp(session.Player.CharacterId, session.Player.Level, session.Player.Exp);
                }

                ushort remainSp = 0, remainTp = 0;
                try
                {
                    var synced = _svc.LoadSyncedSkillState(session.Player.CharacterId, session.Player.Level, persist: leveledUp);
                    if (synced.Points != null)
                    {
                        var pageIndex = session.Player.Subtype0Tail?.SkillTreeIndex == 1 ? 1 : 0;
                        remainSp = SkillStateService.GetPageRemainingSp(synced.Skills, synced.Points, pageIndex);
                        remainTp = (ushort)synced.Points.RemainingTp;
                    }
                }
                catch { }

                if (leveledUp)
                {
                    await _svc.SendUserInfoSubtype0Broadcast(session);
                    await _svc.SendUserInfoBroadcast(session);
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0025,
                    ExpNotificationBuilder.Build(session.Player.Level, session.Player.Exp, remainSp, remainTp,
                        premiumBonusExp: growthContractBonusExp)));

                if (leveledUp)
                {
                    FileLogger.Log($"[DungeonHandler] LEVEL UP: cid={session.Player.CharacterId} {prevLevel}->{session.Player.Level} exp={session.Player.Exp}");
                    await _svc.SendQuestListRefresh(session);
                }

            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0026,
                DungeonNotificationBuilder.BuildMonsterDie(req.LocalIndex, drops, session.Player.UserId)));

            // Quest item drop (IDA: CUser::CheckQuestMonster, after DIE_MONSTER NOTI)
            await _svc.CheckQuestMonsterDrop(session, killedMonsterCode);

            // check_grid_clear (IDA 0x830A0E8): spawnType==100 && spawnFlag==0 blocks passage
            int blockingCount = 0;
            int killedBlockingCount = 0;
            for (var i = 0; i < monsters.Count; i++)
            {
                if (!monsters[i].IsBlocking)
                    continue;

                blockingCount++;
                var seqId = (ushort)(session.Player.CurRoomStartSequence + i);
                if (session.Player.CurRoomKilledSeqIds.Contains(seqId))
                    killedBlockingCount++;
            }
            bool roomCleared = killedBlockingCount >= blockingCount && blockingCount > 0;

            // Old server kill_monster execution order (IDA 0x85A3AED):
            //   1. prepare_dungeon_clear (path B)
            //   2. ClearCondition(type, monsterCode) (path A)
            // Both paths call ClearDungeon, cleared_flag prevents duplicates

            // Path B: prepare_dungeon_clear (df_game_r 0x85AA598)
            // check_grid_clear -> ClearCondition(1, mapIndex) OR check_end_point -> ClearDungeon
            if (roomCleared)
            {
                bool endPoint = false;
                if (session.Player.CurBossMapPos != null && session.Player.CurBossMapPos.Length >= 2)
                {
                    var roomState = session.Player.DungeonRoomStates.Values
                        .FirstOrDefault(rs => rs.KilledSeqIds == session.Player.CurRoomKilledSeqIds);
                    if (roomState != null)
                        endPoint = roomState.Maze.X == session.Player.CurBossMapPos[0]
                                && roomState.Maze.Y == session.Player.CurBossMapPos[1];
                }

                int currentMapId = 0;
                {
                    var rs = session.Player.DungeonRoomStates.Values
                        .FirstOrDefault(r => r.KilledSeqIds == session.Player.CurRoomKilledSeqIds);
                    if (rs != null) currentMapId = rs.Maze.Index;
                }
                bool ccType1 = session.Player.CurClearCondition != null
                    && session.Player.CurClearCondition.Check(1, currentMapId);

                if (ccType1 || endPoint)
                    await _settlement.TryClearDungeon(session, $"prepare_dungeon_clear ccType1={ccType1} endPoint={endPoint}", killedMonsterCode);

                FileLogger.Log($"[DungeonHandler] ROOM CLEARED: dungeon={session.Player.CurDungeon} room=({session.Player.CurRoomKey.X},{session.Player.CurRoomKey.Y}) map={currentMapId} killedBlocking={killedBlockingCount}/{blockingCount} killedTotal={session.Player.CurRoomKilledSeqIds.Count}");
                if (currentMapId > 0 && session.GameSession?.QuestManager != null)
                {
                    if (ShouldDeferQuestConnectedStartMapSync(session, currentMapId)
                        && session.GameSession.QuestManager.HasDeferredQuestConnectedStartMapClearQuest(
                            session.Player.CharacterId,
                            currentMapId))
                    {
                        FileLogger.Log($"[DungeonHandler] CLEAR_MAP deferred for quest-connected start room: dungeon={session.Player.CurDungeon} maze={session.Player.CurMazeIndex} map={currentMapId}");
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
            if (session.Player.CurClearCondition != null)
            {
                int ccType = IsBossActorType(killedMonsterType) ? 4 : (killedMonsterType >= 5 ? 3 : 2);
                if (session.Player.CurClearCondition.Check(ccType, killedMonsterCode))
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
            return session.Player.DungeonRoomStates.TryGetValue(session.Player.CurRoomKey, out roomState);
        }

        private static bool ShouldDeferQuestConnectedStartMapSync(EnhancedClientSession session, int currentMapId)
        {
            var player = session?.Player;
            if (player == null || !player.CurMazeQuestConnected)
                return false;
            if (player.CurMazeStartMapId <= 0 || player.CurMazeStartMapId != currentMapId)
                return false;
            return player.CurRoomKey.X == player.CurMazeStartX
                && player.CurRoomKey.Y == player.CurMazeStartY;
        }

        private static List<DropInfo> GenerateHellPartyActorDrops(
            EnhancedClientSession session,
            RoomState roomState,
            DungeonData.MonsterSumInfo monster,
            int dungeonMinimumLevel,
            int dungeonBasisLevel,
            ref ushort slotCounter)
        {
            var drops = IndependentDropSystem.GenerateDrops(
                monster.Code,
                session.Player.CurDungeonDifficulty,
                dungeonBasisLevel,
                session.Player.CurRoomLcg,
                ref slotCounter);

            var remainingBefore = 0;
            var remainingAfter = 0;
            var isLastGroupMonster = false;
            if (roomState.HellPartyGroupRemaining != null
                && monster.HellPartyGroupId > 0
                && roomState.HellPartyGroupRemaining.TryGetValue(monster.HellPartyGroupId, out remainingBefore))
            {
                remainingAfter = Math.Max(0, remainingBefore - 1);
                if (remainingAfter == 0)
                {
                    roomState.HellPartyGroupRemaining.Remove(monster.HellPartyGroupId);
                    isLastGroupMonster = true;
                }
                else
                {
                    roomState.HellPartyGroupRemaining[monster.HellPartyGroupId] = remainingAfter;
                }
            }

            var hellRewardDrops = 0;
            if (isLastGroupMonster && !monster.IsHellMonsterScript)
            {
                var rewardDrops = HellMonsterDropConfig.GenerateSpecificEquipmentDrops(
                    session.Player.CurRoomLcg,
                    dungeonMinimumLevel,
                    dungeonBasisLevel,
                    session.Player.CurDungeonDifficulty,
                    monster.HellPartyDifficulty,
                    monster.HellRewardRollCount,
                    ref slotCounter);
                hellRewardDrops = rewardDrops.Count;
                drops.AddRange(rewardDrops);
            }

            FileLogger.Log($"[DungeonHandler] HELLPARTY_DROP: code={monster.Code} type={monster.Type} group={monster.HellPartyGroupId} hellDifficulty={monster.HellPartyDifficulty} remainingBefore={remainingBefore} remainingAfter={remainingAfter} isLastGroup={isLastGroupMonster} hellMonster={monster.IsHellMonsterScript} rewardRolls={monster.HellRewardRollCount} independentDrops={drops.Count - hellRewardDrops} hellRewardDrops={hellRewardDrops}");
            return drops;
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
            var req = GetItemRequest.Parse(body);
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: cid={session.Player.CharacterId} srcSlot={req.SrcSlot}");

            DropInfo matchedDrop;
            if (!session.Player.CurDungeonDrops.TryGetValue(req.SrcSlot, out matchedDrop))
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: no pending drop for srcSlot={req.SrcSlot}, ignored");
                return;
            }

            var accountId = session.Account?.AccountId ?? 1;

            if (matchedDrop.IsGold)
            {
                var goldAmount = (int)matchedDrop.StackCount;
                _svc.PersistGold(session.Player.CharacterId, accountId, goldAmount);
                session.Player.CurDungeonDrops.Remove(req.SrcSlot);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0027,
                    DropItemBuilder.BuildPickupGold(req.SrcSlot, session.Player.UserId, goldAmount)));
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: gold pickup srcSlot={req.SrcSlot} amount={goldAmount}");
            }
            else
            {
                short invSlot;
                if (!_svc.TryPickupItemToInventory(session.Player.CharacterId, accountId, (int)matchedDrop.TemplateId, (int)matchedDrop.StackCount, out invSlot))
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: FAILED to insert item templateId={matchedDrop.TemplateId} -- inventory full or special, drop preserved for retry");
                    return;
                }
                session.Player.CurDungeonDrops.Remove(req.SrcSlot);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0027,
                    DropItemBuilder.BuildPickupItem(req.SrcSlot, session.Player.UserId, (ushort)invSlot, 7)));
                if (session.GameSession?.QuestManager != null && matchedDrop.TemplateId > 0)
                    await session.GameSession.QuestManager.SyncItemSeekingQuestProgressAsync(
                        new[] { (int)matchedDrop.TemplateId });
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: item pickup srcSlot={req.SrcSlot} templateId={matchedDrop.TemplateId} invSlot={invSlot}");
            }
        }
    }
}
