using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonSettlementHandler
    {
        private readonly DungeonSharedServices _svc;

        // PVF [visible on dungeon clear]=1: Delilah(1000) Gabriel(1002/1003/1004) Yunmi(1203, invalid in 86JP)
        private static readonly int[] SecretShopNpcIds = { 1000, 1002, 1003, 1004 };

        internal DungeonSettlementHandler(DungeonSharedServices svc) => _svc = svc;

        // CMD 71 (0x47) SELECT_CARD -- card flip + EPLP settlement option
        // 86JP merges SELECT_CARD and EPLP_COMMAND into same wire type
        // body[0]: 0=free card, 1=paid card/EPLP confirm, 2=EPLP status update
        // body[1]: cardIndex(0-3) when flipping, option(0=retry 1=select other 2=return town) when EPLP
        internal async Task HandleSelectCard(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body.Length < 2) return;
            byte cardType = body[0];
            byte cardIndex = body[1];

            // EPLP: df_game_r _BroadCastPacket replies ACK [01, state, option] for each EPLP
            // after confirm, starts 1s timer -> ReturnToVillage (NOTI 2)
            if (cardType >= 2 || (cardType == 1 && session.Player.CurCardRewards == null))
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] EPLP: state={cardType} option={cardIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0048,
                    new byte[] { 0x01, cardType, cardIndex }));

                if (cardType == 1)
                {
                    // 86JP all three options return to town: retry(0)/select other(1)/return town(2)
                    // client handles re-entry automatically after returning
                    int delayMs = cardIndex == 2 ? 1000 : 3000;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(delayMs);
                        try
                        {
                            DungeonSharedServices.ResetDungeonState(session);
                            session.Player.UserState = 0x01;

                            // NOTI 3 USER_STATE -- exit dungeon state (out_from_dungeon verified)
                            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0003,
                                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));

                            // USER_AREA + AREA_USERS -- load town scene
                            var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);
                            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017,
                                TownAreaNotificationBuilder.BuildUserArea(snapshot)));
                            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018,
                                TownAreaNotificationBuilder.BuildAreaUsers(snapshot)));

                            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] EPLP ReturnToVillage: STATE+AREA+USERS sent");
                        }
                        catch (Exception ex) { FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] EPLP ReturnToVillage ERROR: {ex.Message}"); }
                    });
                }
                return;
            }

            // Card flip
            if (cardIndex > 3) return;
            session.Player.CurCardFlipCount++;
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_CARD: flip#{session.Player.CurCardFlipCount} type={cardType} index={cardIndex}");

            if (cardType == 0)
                session.Player.CurFreeCardSlots[cardIndex] = 0x00;
            else
                session.Player.CurPaidCardSlots[cardIndex] = 0x00;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047,
                BuildCardInfoAck(session)));

            bool freeSelected = session.Player.CurFreeCardSlots[0] != 0xFF;
            bool paidSelected = session.Player.CurPaidCardSlots[0] != 0xFF;

            if (freeSelected && paidSelected)
            {
                var cards = session.Player.CurCardRewards;
                if (cards != null)
                {
                    var entries = new List<byte[]>();
                    var accountId = session.Account?.AccountId ?? 1;

                    // Free card gold
                    if (cards.Count > 0 && cards[0].IsGold && cards[0].GoldAmount > 0)
                    {
                        _svc.PersistGold(session.Player.CharacterId, accountId, cards[0].GoldAmount);
                        int totalGold = _svc.ReadGold(session.Player.CharacterId, accountId);
                        entries.Add(DungeonSharedServices.BuildItemEntry(0, 0, (uint)totalGold));
                    }

                    // Free card item
                    if (cards.Count > 1 && !cards[1].IsGold && cards[1].ItemId > 0)
                    {
                        short slot;
                        if (_svc.TryPickupItemToInventory(session.Player.CharacterId, accountId, cards[1].ItemId, cards[1].StackCount, out slot))
                            entries.Add(cards[1].IsEquipment
                                ? DungeonSharedServices.BuildEquipEntry(slot, (uint)cards[1].ItemId)
                                : DungeonSharedServices.BuildItemEntry(slot, (uint)cards[1].ItemId, (uint)cards[1].StackCount));
                    }

                    // Paid card gold
                    if (cards.Count > 4 && cards[4].IsGold && cards[4].GoldAmount > 0)
                    {
                        _svc.PersistGold(session.Player.CharacterId, accountId, cards[4].GoldAmount);
                        int totalGold = _svc.ReadGold(session.Player.CharacterId, accountId);
                        entries.Add(DungeonSharedServices.BuildItemEntry(0, 0, (uint)totalGold));
                    }

                    // Paid card item
                    if (cards.Count > 5 && !cards[5].IsGold && cards[5].ItemId > 0)
                    {
                        short slot;
                        if (_svc.TryPickupItemToInventory(session.Player.CharacterId, accountId, cards[5].ItemId, cards[5].StackCount, out slot))
                            entries.Add(cards[5].IsEquipment
                                ? DungeonSharedServices.BuildEquipEntry(slot, (uint)cards[5].ItemId)
                                : DungeonSharedServices.BuildItemEntry(slot, (uint)cards[5].ItemId, (uint)cards[5].StackCount));
                    }

                    // NOTI 14 UPDATE_ITEM_LIST
                    if (entries.Count > 0)
                    {
                        var w = new GamePacketWriter();
                        w.WriteByte(0);                     // updateType = 0
                        w.WriteUInt16((ushort)entries.Count);
                        foreach (var e in entries)
                            w.WriteBytes(e);
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, w.ToArray()));
                    }

                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Card flip complete -- {entries.Count} entries sent via NOTI 14");
                }
                session.Player.CurCardRewards = null;
            }
        }

        internal async Task HandleSetPlayResult(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!session.Player.CurBossKilled) return;
            session.Player.CurBossKilled = false;
            _svc.PersistLevelAndExp(session.Player.CharacterId, session.Player.Level, session.Player.Exp);

            // Pre-generate card rewards (df_game_r: clear_reward generated before NOTI 35)
            int dungeonLevel = 85;
            try { dungeonLevel = DungeonData.GetDungeonBasicLv(session.Player.CurDungeon); } catch { }
            var lcg = session.Player.CurRoomLcg ?? new DnfLcg(session.Player.CurDungeonSeed);
            var freeGold = ClearRewardGenerator.GenerateGoldCard(
                dungeonLevel, session.Player.CurDungeonDifficulty, lcg);
            var freeItem = ClearRewardGenerator.GenerateItemCard(
                dungeonLevel, session.Player.CurDungeonDifficulty, lcg);
            var paidGold = ClearRewardGenerator.GenerateGoldCard(
                dungeonLevel, session.Player.CurDungeonDifficulty, lcg);
            var paidItem = ClearRewardGenerator.GenerateItemCard(
                dungeonLevel, session.Player.CurDungeonDifficulty, lcg);
            session.Player.CurCardRewards = new List<ClearRewardGenerator.CardReward>
            {
                freeGold, freeItem, default, default,  // free: [0]gold [1]item [2-3]empty(solo)
                paidGold, paidItem, default, default    // paid: [4]gold [5]item [6-7]empty(solo)
            };

            // Settlement 3 packets + card layout
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0022,
                DungeonNotificationBuilder.BuildPlayResult(
                    session.Player.UserId, session.Player.CurBossCode,
                    session.Player.CurDungeonTotalExp, allKill: true)));
            ushort remainSp = 0, remainTp = 0;
            try
            {
                var points = _svc.LoadSyncedSkillState(session.Player.CharacterId, session.Player.Level, persist: false).Points;
                if (points != null)
                {
                    remainSp = (ushort)points.RemainingSp;
                    remainTp = (ushort)points.RemainingTp;
                }
            }
            catch { }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0025,
                DungeonNotificationBuilder.BuildExp(session.Player.Level, session.Player.Exp, remainSp, remainTp)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0023,
                DungeonNotificationBuilder.BuildClearDungeonReward(
                    session.Player.CurDungeonTotalExp, session.Player.CurDungeonTotalGold,
                    goldCardCost: 10180, freeCardGold: freeGold.GoldAmount,
                    freeCardItemId: freeItem.ItemId, freeCardItemCount: freeItem.StackCount)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0045, new byte[] { 0x01 }));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0046, BuildCardLayoutAck()));

            session.Player.CurDungeonClearState = 4;
            session.Player.CurCardFlipCount = 0;
            session.Player.CurFreeCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
            session.Player.CurPaidCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        }

        // df_game_r CParty::ClearDungeon (0x85A9330)
        // Preamble: if (!cleared_flag) return; Epilogue: cleared_flag = 1;
        // Normal dungeon sends NOTI 31 (ENABLE_CLEAR_DUNGEON), sets CurBossKilled
        // + NOTI 279 (0x0117) SECRET_SHOP_NPC -- settlement mystery merchant NPC ID
        internal async Task TryClearDungeon(EnhancedClientSession session, string reason, int bossCode = 0)
        {
            if (session.Player.CurDungeonCleared) return;
            session.Player.CurDungeonCleared = true;
            session.Player.CurBossKilled = true;
            if (bossCode != 0) session.Player.CurBossCode = bossCode;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001F, DungeonNotificationBuilder.BuildEnableClearDungeon()));
            var npcId = SecretShopNpcIds[DungeonSharedServices.SeedGen.Next(SecretShopNpcIds.Length)];
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0117, BitConverter.GetBytes(npcId)));
            FileLogger.Log($"[DungeonHandler] ClearDungeon: {reason} secretShopNpc={npcId}");
        }

        // CMD ACK 71 body -- 86JP 8-seat format
        // seat[0-3]: active seats (solo uses seat0 only)
        // seat[4-7]: 0xFF*4 (hidden/disabled)
        private byte[] BuildCardInfoAck(EnhancedClientSession session)
        {
            var w = new GamePacketWriter();
            w.WriteByte(0x01);  // resultCode

            for (int i = 0; i < 8; i++)
            {
                if (i >= 4)
                {
                    // Hidden seat: type=FF flag=FF count=FF(-1,skip) flipped=FF
                    w.WriteByte(0xFF);
                    w.WriteByte(0xFF);
                    w.WriteByte(0xFF);
                    w.WriteByte(0xFF);
                    continue;
                }

                bool freeSelected = session.Player.CurFreeCardSlots[i] != 0xFF;
                bool paidSelected = session.Player.CurPaidCardSlots[i] != 0xFF;

                if (i != 0)
                {
                    // Solo mode seat1-3 empty: type=FF flag=FF count=0 flipped=0
                    w.WriteByte(0xFF);
                    w.WriteByte(0xFF);
                    w.WriteByte(0x00);
                    w.WriteByte(0x00);
                    continue;
                }

                // seat0: active card seat
                w.WriteByte(freeSelected ? (byte)0x00 : (byte)0xFF);  // cardType
                w.WriteByte(paidSelected ? (byte)0x00 : (byte)0xFF);  // seatFlag

                if (paidSelected)
                {
                    // Paid card: count=2, item[0]=gold{0,gold}, item[1]=item{id,count}
                    // S4Log verified: {0,0} + {100180190,1}
                    var cards = session.Player.CurCardRewards;
                    int paidGoldAmt = (cards != null && cards.Count > 4 && cards[4].IsGold) ? cards[4].GoldAmount : 0;
                    int paidItemId = (cards != null && cards.Count > 5 && !cards[5].IsGold) ? cards[5].ItemId : 0;
                    int paidItemCnt = (cards != null && cards.Count > 5 && !cards[5].IsGold) ? cards[5].StackCount : 0;

                    w.WriteByte(2);                         // itemCount = 2
                    w.WriteUInt32(0);                       // item[0] itemId=0 (gold)
                    w.WriteInt32(paidGoldAmt);              // item[0] amount
                    w.WriteUInt32((uint)paidItemId);        // item[1] itemId
                    w.WriteInt32(paidItemCnt);              // item[1] count
                }
                else
                {
                    w.WriteByte(0x00);  // itemCount = 0 (free card sends no content)
                }

                w.WriteByte(0x00);  // flippedFlag
            }

            return w.ToArray();
        }

        // CMD ACK 70 -- card layout: u8 resultCode + u16[8] slotStatus
        // Solo: slot[0]=0x0001(flippable) slot[1-7]=0xFFFF(disabled)
        private static byte[] BuildCardLayoutAck()
        {
            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(0x0001);
            for (int i = 1; i < 8; i++)
                w.WriteUInt16(0xFFFF);
            return w.ToArray();
        }

        internal static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
