using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonTutorialHandler
    {
        private readonly DungeonSharedServices _svc;
        private readonly DungeonSettlementHandler _settlement;

        // df_game_r=59; FBS new0610 tested TUTORIAL_LEVEL_UP only levels to Lv2
        private const byte TutorialTargetLevel = 2;

        internal DungeonTutorialHandler(DungeonSharedServices svc, DungeonSettlementHandler settlement)
        {
            _svc = svc;
            _settlement = settlement;
        }

        internal async Task HandleStoryPause(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body.Length < 2) return;
            byte pauseFlag = body[0];
            byte requestType = body[1];

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] STORY_PAUSE CMD: pauseFlag={pauseFlag} requestType={requestType} cid={session.Player.CharacterId}");

            var w = new GamePacketWriter();
            w.WriteUInt16(session.Player.UserId);
            w.WriteByte(pauseFlag);
            w.WriteByte(requestType);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00AA, w.ToArray()));

            // After APC dialog ends: only non-blocking APC rooms may clear from dialog flow.
            // Enemy APC/BOSS actors still block the room until their DIE_MONSTER is received.
            if (pauseFlag == 1 && (session.Player.CurrentRun?.DungeonId ?? 0) > 0)
            {
                var progress = DungeonSharedServices.GetCurrentRoomProgress(session);
                if (DungeonSharedServices.ShouldClearAfterApcDialog(progress))
                {
                    await _settlement.TryClearDungeon(session, "APC dialog + all normals dead");
                }
                else if (progress.ApcCount > 0 && progress.KilledNormalCount >= progress.NormalCount)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] STORY_PAUSE clear skipped: blockingRemaining={progress.BlockingRemainingCount} remaining={progress.RemainingCount}");
                }
            }
        }

        // CMD 0x008F (wire 143) CHANGE_TUTORIAL_FLAG
        // body: u32 flagIndex + u8 rewardFlag (5B, df_game_r get_int+get_byte verified, 86JP capture 1F-00-00-00-01 matches)
        // df_game_r: setCurCharacTutorialFlag(flagIndex), if rewardFlag -> RewardTutorial(flagIndex)
        //            flagIndex==31 + in dungeon -> giveup_game (tutorial complete, return to town)
        //            flagIndex==77 -> set ALL flags 0-77
        internal async Task HandleChangeTutorialFlag(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body.Length < 5) return;
            uint flagIndex = BitConverter.ToUInt32(body, 0);
            byte rewardFlag = body[4];

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CHANGE_TUTORIAL_FLAG: flagIndex={flagIndex} rewardFlag={rewardFlag} dungeon={(session.Player.CurrentRun?.DungeonId ?? 0)} cid={session.Player.CharacterId}");

            // RewardTutorial: PVF serverparameter.etc [escalade tutorial reward]
            var inserted = new List<(short slot, int itemId, int count)>();
            var accountId = session.Account?.AccountId ?? 1;
            if (rewardFlag != 0)
            {
                var rewards = TutorialRewardProvider.GetRewards(flagIndex);
                if (rewards != null)
                {
                    foreach (var r in rewards)
                    {
                        short slot;
                        if (_svc.TryPickupItemToInventory(session.Player.CharacterId, accountId, r.ItemId, r.Count, out slot))
                        {
                            inserted.Add((slot, r.ItemId, r.Count));
                            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] RewardTutorial: flag={flagIndex} gave item {r.ItemId} x{r.Count} -> slot {slot}");
                        }
                        else
                        {
                            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] RewardTutorial: flag={flagIndex} FAILED to insert item {r.ItemId}");
                        }
                    }
                }
            }

            // ACK: resultCode=1 + u8 count + count x { u16 slot, u32 itemId, u32 count }
            var ack = new GamePacketWriter();
            ack.WriteByte(0x01);
            ack.WriteByte((byte)inserted.Count);
            foreach (var item in inserted)
            {
                ack.WriteUInt16((ushort)item.slot);
                ack.WriteUInt32((uint)item.itemId);
                ack.WriteUInt32((uint)item.count);
            }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x008F, ack.ToArray()));

            // flagIndex==31: tutorial complete -> return to town (only when in dungeon, df_game_r: state>1 + giveup_game)
            if (flagIndex == 31)
            {
                var cid = session.Player.CharacterId;
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CHANGE_TUTORIAL_FLAG: tutorial complete (flag=31), marking skip. cid={cid}");

                var snap = new SelectCharacterInitializationSnapshot();
                _svc.CharacterStateRepository.LoadFlags(cid, snap);
                snap.AckTutorialSkipable = 1;
                _svc.CharacterStateRepository.SaveFlags(cid, snap);

                if ((session.Player.CurrentRun?.DungeonId ?? 0) > 0)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CHANGE_TUTORIAL_FLAG: returning to town from dungeon={(session.Player.CurrentRun?.DungeonId ?? 0)}");
                    await ReturnToVillage(session);
                }
            }
        }

        // CMD 0x01E4 (wire 484) TUTORIAL_LEVEL_UP
        // 86JP body: empty (0B). df_game_r: check level==1 + map in {61001,61009,61016},
        // CalLevelUpItemState(1, targetLevel) bulk exp to target level, SendCmdOkPacket(484)
        internal async Task HandleTutorialLevelUp(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] TUTORIAL_LEVEL_UP: cid={session.Player.CharacterId} level={session.Player.Level} dungeon={(session.Player.CurrentRun?.DungeonId ?? 0)}");

            if (session.Player.Level != 1 || (session.Player.CurrentRun?.DungeonId ?? 0) <= 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01E4, new byte[] { 0x13 }));
                return;
            }

            byte target = TutorialTargetLevel;
            uint targetExp = 0;
            for (byte lv = 1; lv < target; lv++)
            {
                var threshold = (uint)ExpTableProvider.GetLevelThreshold(lv);
                if (threshold > targetExp) targetExp = threshold;
            }
            session.Player.Exp = targetExp;
            session.Player.Level = target;

            _svc.PersistLevelAndExp(session.Player.CharacterId, session.Player.Level, session.Player.Exp);
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] TUTORIAL_LEVEL_UP: {1}->{target} exp={targetExp}");

            var (remainSp, remainTp) = _svc.GetRemainingSpTp(session, persist: true, logTag: "TUTORIAL_LEVEL_UP");

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0025,
                ExpNotificationBuilder.Build(session.Player.Level, session.Player.Exp, remainSp, remainTp)));

            await _svc.SendInDungeonLevelUpFollowups(session);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01E4, new byte[] { 0x01 }));
        }

        internal async Task HandleBack2Village(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] BACK_2_VILLAGE: returning to town");
            await ReturnToVillage(session);
        }

        private async Task ReturnToVillage(EnhancedClientSession session)
        {
            await DungeonRunLifecycle.EndRunToTownAsync(session);
            session.Player.UserState = 0x00;

            var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0003,
                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017,
                TownAreaNotificationBuilder.BuildUserArea(snapshot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(snapshot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00CA,
                new byte[] { 0x00 }));

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ReturnToVillage: 4 town packets sent");
        }
    }
}
