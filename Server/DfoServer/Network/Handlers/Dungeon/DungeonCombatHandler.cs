using DfoServer.Game.Dungeon;
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
                var gainedExp = (uint)MonsterRewardTable.CalcExp(monsterLevel, weight, session.Player.CurDungeonDifficulty);

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

                    // 区域材料是服务端补偿到普通怪物 [item] 池里的掉落，深渊 hidden actor 不走这条普通分支。
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
                    var result = generator.GenerateMonsterDrops(dropRateLevel, monster.Type, monster.Code, session.Player.CurDungeonDifficulty, dungeonBasisLevel, ref slotCounter, dropPool);
                    goldGained = result.goldAmount;
                    generatedDrops = result.drops;
                }

                session.Player.CurSceneSlotCounter = slotCounter;

                session.Player.Exp += gainedExp;
                session.Player.CurDungeonTotalExp += gainedExp;
                session.Player.CurDungeonTotalGold += goldGained;

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
                    var points = _svc.LoadSyncedSkillState(session.Player.CharacterId, session.Player.Level, persist: leveledUp).Points;
                    if (points != null)
                    {
                        remainSp = (ushort)points.RemainingSp;
                        remainTp = (ushort)points.RemainingTp;
                    }
                }
                catch { }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0025,
                    ExpNotificationBuilder.Build(session.Player.Level, session.Player.Exp, remainSp, remainTp)));

                if (leveledUp)
                {
                    FileLogger.Log($"[DungeonHandler] LEVEL UP: cid={session.Player.CharacterId} {prevLevel}->{session.Player.Level} exp={session.Player.Exp}");
                    await _svc.SendQuestListRefresh(session);
                    await _svc.SendUserInfoBroadcast(session);
                }

            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0026,
                DungeonNotificationBuilder.BuildMonsterDie(req.LocalIndex, drops, session.Player.UserId)));

            // Quest item drop (IDA: CUser::CheckQuestMonster, after DIE_MONSTER NOTI)
            await _svc.CheckQuestMonsterDrop(session, killedMonsterCode);

            // check_grid_clear (IDA 0x830A0E8): spawnType==100 && spawnFlag==0 blocks passage
            int blockingCount = 0;
            foreach (var m in monsters)
                if (m.IsBlocking) blockingCount++;
            bool roomCleared = session.Player.CurRoomKilledSeqIds.Count >= blockingCount && blockingCount > 0;

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

                FileLogger.Log($"[DungeonHandler] ROOM CLEARED: dungeon={session.Player.CurDungeon} killed={session.Player.CurRoomKilledSeqIds.Count}/{blockingCount}");
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
            // monsterType -> conditionType: boss(3)->4, apc(5-8)->3, normal->2
            if (session.Player.CurClearCondition != null)
            {
                int ccType = killedMonsterType == 3 ? 4 : (killedMonsterType >= 5 ? 3 : 2);
                if (session.Player.CurClearCondition.Check(ccType, killedMonsterCode))
                    await _settlement.TryClearDungeon(session, $"ClearCondition type={ccType} target={killedMonsterCode}", killedMonsterCode);
            }
        }

        private static bool TryGetCurrentRoomState(EnhancedClientSession session, out RoomState roomState)
        {
            return session.Player.DungeonRoomStates.TryGetValue(session.Player.CurRoomKey, out roomState);
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

        internal async Task HandleUseCoin(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            // df_game_r: read = u16 targetActorId
            ushort targetId = body.Length >= 2 ? BitConverter.ToUInt16(body, 0) : session.Player.UserId;
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] USE_COIN: uid={session.Player.UserId} target={targetId}");

            // 1. NOTI 0x0020 DIE_STATE: set_charac_live(user, 1=revive)
            //    df_game_r body = u16 actorId + u8 state; 86JP has extra u8 flag
            var noti = new GamePacketWriter();
            noti.WriteUInt16(targetId);
            noti.WriteByte(0x01);  // state=1 revive
            noti.WriteByte(0x00);  // 86JP flag
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0020, noti.ToArray()));

            // 2. CMD ACK 0x0029: resultCode=1 + u16 targetActorId
            var ack = new GamePacketWriter();
            ack.WriteByte(0x01);           // resultCode = success
            ack.WriteUInt16(targetId);     // targetActorId
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0029, ack.ToArray()));
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
                    await session.GameSession.QuestManager.SyncMonsterRewardItemProgressAsync(
                        new[] { (int)matchedDrop.TemplateId });
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: item pickup srcSlot={req.SrcSlot} templateId={matchedDrop.TemplateId} invSlot={invSlot}");
            }
        }
    }
}
