using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading;
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

        // ── Settlement result ──────────────────────────────────────────────────
        // df_game_r CParty::CheckPlayResult → CParty::SetPlayResult
        // Sends 3 NOTI packets (34, 37, 35) to show the settlement screen.
        // Card layout is deferred: a 2 s server timer sends it automatically
        // so the player sees the settlement summary first, then the cards appear.
        // After the card layout, a 4 s timer auto-flips the free card
        // (the client shows a 3 s countdown; 4 s on the server gives it room to finish).
        // If the player presses a key before the layout timer fires, the layout
        // is sent immediately and a fresh 3 s auto-flip timer starts.
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

            // Settlement 3 packets: NOTI 34, NOTI 37, NOTI 35
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

            // Card layout is deferred: 2 s timer → layout, then 3 s → auto-flip free card.
            session.Player.CurDungeonClearState = 4;
            session.Player.CurCardFlipCount = 0;
            session.Player.CurFreeCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
            session.Player.CurPaidCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

            ScheduleAutoFlow(session, layoutDelayMs: 2000, autoFlipDelayMs: 4000);
        }

        // ── Auto-flow timer ────────────────────────────────────────────────────
        // Phase 1: after layoutDelayMs, send the card layout (0x0045 + 0x0046).
        // Phase 2: after autoFlipDelayMs more, flip the free card.
        // If the player presses a key before phase 1 fires, HandleSelectCard (state==4)
        // cancels this timer and shows the layout immediately, then starts a fresh
        // phase-2 timer so the free card still auto-flips after 3 s.

        private void ScheduleAutoFlow(EnhancedClientSession session, int layoutDelayMs, int autoFlipDelayMs)
        {
            CancelAutoFlip(session);
            var cts = new CancellationTokenSource();
            session.Player.CardAutoFlipCts = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Phase 1 — wait, then show card layout
                    await Task.Delay(layoutDelayMs, token);
                    if (token.IsCancellationRequested) return;
                    if (session.Player.CurDungeonClearState != 4) return;

                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Auto-layout timer fired, sending card layout");
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0045, new byte[] { 0x01 }));
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0046, BuildCardLayoutAck()));
                    session.Player.CurDungeonClearState = 5;

                    // Phase 2 — wait, then auto-flip free card
                    await Task.Delay(autoFlipDelayMs, token);
                    if (token.IsCancellationRequested) return;

                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Auto-flip timer fired, flipping free card");
                    await AutoFlipFreeCard(session);
                }
                catch (TaskCanceledException) { /* player acted before timer */ }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Auto-flow timer error: {ex}");
                }
            }, token);
        }

        // Starts a server-side 3-second auto-flip timer for the free card.
        // Used when the layout was shown early via a player key-press (so the
        // combined ScheduleAutoFlow timer was cancelled and we need a standalone
        // auto-flip timer).
        private void StartDelayedAutoFlip(EnhancedClientSession session, int delayMs)
        {
            CancelAutoFlip(session);
            var cts = new CancellationTokenSource();
            session.Player.CardAutoFlipCts = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, token);
                    if (token.IsCancellationRequested) return;
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Standalone auto-flip timer fired");
                    await AutoFlipFreeCard(session);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Auto-flip timer error: {ex}");
                }
            }, token);
        }

        private static void CancelAutoFlip(EnhancedClientSession session)
        {
            var cts = Interlocked.Exchange(ref session.Player.CardAutoFlipCts, null);
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        // Auto-flips only the free card (never the paid card).
        // Sends ACK 0x0047 with flipped card info, then delivers free card
        // rewards via NOTI 14. CurCardRewards is NOT cleared — the paid card
        // stays available for the player to flip/EPLP.
        private async Task AutoFlipFreeCard(EnhancedClientSession session)
        {
            if (session.Player.CurFreeCardSlots[0] != 0xFF) return; // already flipped

            session.Player.CurCardFlipCount++;
            session.Player.CurFreeCardSlots[0] = 0x00;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047,
                BuildCardInfoAck(session)));

            bool hasPaid = HasPaidCardReward(session.Player.CurCardRewards);
            if (!hasPaid)
            {
                // No paid card — deliver free rewards and clear cards so EPLP works.
                await DeliverCardRewards(session);
                session.Player.CurCardRewards = null;
            }
            else
            {
                // Paid card pending — only deliver free card rewards; keep cards alive.
                await DeliverFreeCardRewardsOnly(session);
            }
        }

        // ── SELECT_CARD (CMD 0x0047) — card flip only ──────────────────────────
        // body[0]: 0=free card, 1=paid card
        // body[1]: cardIndex (0-3)
        // EPLP buttons come via CMD 0x0048 → HandleEplpCommand, never here.
        internal async Task HandleSelectCard(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body.Length < 2) return;
            byte cardType = body[0];
            byte cardIndex = body[1];

            // Lazy card layout: player pressed a key while settlement is showing.
            if (session.Player.CurDungeonClearState == 4)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Lazy card layout by SELECT_CARD: type={cardType} idx={cardIndex}");
                CancelAutoFlip(session);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0045, new byte[] { 0x01 }));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0046, BuildCardLayoutAck()));
                session.Player.CurDungeonClearState = 5;
                StartDelayedAutoFlip(session, delayMs: 4000);
                return;
            }

            CancelAutoFlip(session);

            // Only card flips here — EPLP goes through CMD 0x0048.
            if (cardType > 1 || cardIndex > 3) return;

            session.Player.CurCardFlipCount++;
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_CARD flip#{session.Player.CurCardFlipCount} type={cardType} idx={cardIndex}");

            if (cardType == 0)
                session.Player.CurFreeCardSlots[cardIndex] = 0x00;
            else
                session.Player.CurPaidCardSlots[cardIndex] = 0x00;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047,
                BuildCardInfoAck(session)));

            bool freeSelected = session.Player.CurFreeCardSlots[0] != 0xFF;
            bool paidSelected = session.Player.CurPaidCardSlots[0] != 0xFF;
            bool allDone = freeSelected && (paidSelected || !HasPaidCardReward(session.Player.CurCardRewards));

            if (allDone)
            {
                await DeliverCardRewards(session);
                session.Player.CurCardRewards = null;
            }
        }

        // ── EPLP (CMD 0x0048) — settlement option buttons ──────────────────────
        // body[0]: 1=confirm, 2=status update
        // body[1]: 0=再次挑战, 1=选择其他地下城, 2=返回城镇
        // If a paid card is still pending, auto-flip it before returning to town
        // (matches DNF behaviour: clicking any EPLP button auto-pays the card).
        internal async Task HandleEplpCommand(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body.Length < 2) return;
            byte state = body[0];
            byte option = body[1];

            // Lazy card layout via EPLP button press
            if (session.Player.CurDungeonClearState == 4)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Lazy card layout by EPLP: state={state} option={option}");
                CancelAutoFlip(session);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0045, new byte[] { 0x01 }));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0046, BuildCardLayoutAck()));
                session.Player.CurDungeonClearState = 5;
                StartDelayedAutoFlip(session, delayMs: 4000);
                return;
            }

            CancelAutoFlip(session);

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] EPLP: state={state} option={option}");

            // Auto-flip pending paid card (DNF: clicking EPLP auto-pays remaining card)
            bool pendingPaidCard = HasPaidCardReward(session.Player.CurCardRewards) &&
                                   session.Player.CurPaidCardSlots[0] == 0xFF;
            if (state == 1 && pendingPaidCard)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] EPLP auto-flipping pending paid card");
                session.Player.CurPaidCardSlots[0] = 0x00;
                session.Player.CurCardFlipCount++;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047,
                    BuildCardInfoAck(session)));
                await DeliverCardRewards(session);
                session.Player.CurCardRewards = null;
            }

            // Send EPLP ACK
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0048,
                new byte[] { 0x01, state, option }));

            if (state == 1)
            {
                await ReturnToVillage(session);
            }
        }

        // Extracted reward delivery shared by auto-flip, manual flip, and EPLP-combo.
        // Delivers both free and paid card rewards via NOTI 0x000E.
        private async Task DeliverCardRewards(EnhancedClientSession session)
        {
            var cards = session.Player.CurCardRewards;
            if (cards == null) return;

            var entries = new List<byte[]>();
            var accountId = session.Account?.AccountId ?? 1;

            // Free card gold
            if (cards.Count > 0 && cards[0].IsGold && cards[0].GoldAmount > 0)
            {
                _svc.PersistGold(session.Player.CharacterId, accountId, cards[0].GoldAmount);
                int totalGold = _svc.ReadGold(session.Player.CharacterId, accountId);
                entries.Add(DungeonSharedServices.BuildItemEntry(0, 0, (uint)totalGold));
            }
            AddCardItemEntry(session, accountId, cards, 1, entries);

            // Paid card gold
            if (cards.Count > 4 && cards[4].IsGold && cards[4].GoldAmount > 0)
            {
                _svc.PersistGold(session.Player.CharacterId, accountId, cards[4].GoldAmount);
                int totalGold = _svc.ReadGold(session.Player.CharacterId, accountId);
                entries.Add(DungeonSharedServices.BuildItemEntry(0, 0, (uint)totalGold));
            }
            AddCardItemEntry(session, accountId, cards, 5, entries);

            if (entries.Count > 0)
            {
                var w = new GamePacketWriter();
                w.WriteByte(0);
                w.WriteUInt16((ushort)entries.Count);
                foreach (var e in entries)
                    w.WriteBytes(e);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, w.ToArray()));
            }
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Card rewards delivered: {entries.Count} entries");
        }

        // Free card rewards only (used when paid card is still pending after auto-flip).
        private async Task DeliverFreeCardRewardsOnly(EnhancedClientSession session)
        {
            var cards = session.Player.CurCardRewards;
            if (cards == null) return;

            var entries = new List<byte[]>();
            var accountId = session.Account?.AccountId ?? 1;

            if (cards.Count > 0 && cards[0].IsGold && cards[0].GoldAmount > 0)
            {
                _svc.PersistGold(session.Player.CharacterId, accountId, cards[0].GoldAmount);
                int totalGold = _svc.ReadGold(session.Player.CharacterId, accountId);
                entries.Add(DungeonSharedServices.BuildItemEntry(0, 0, (uint)totalGold));
            }
            AddCardItemEntry(session, accountId, cards, 1, entries);

            if (entries.Count > 0)
            {
                var w = new GamePacketWriter();
                w.WriteByte(0);
                w.WriteUInt16((ushort)entries.Count);
                foreach (var e in entries)
                    w.WriteBytes(e);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, w.ToArray()));
            }
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Free card rewards delivered: {entries.Count} entries");
        }

        private void AddCardItemEntry(EnhancedClientSession session, int accountId,
            List<ClearRewardGenerator.CardReward> cards, int cardIndex, List<byte[]> entries)
        {
            if (cards.Count <= cardIndex || cards[cardIndex].IsGold || cards[cardIndex].ItemId <= 0)
                return;

            var card = cards[cardIndex];
            short slot;
            if (!_svc.TryPickupItemToInventory(session.Player.CharacterId, accountId, card.ItemId, card.StackCount, out slot))
                return;

            entries.Add(card.IsEquipment
                ? DungeonSharedServices.BuildEquipEntry(slot, (uint)card.ItemId, durability: card.Durability)
                : DungeonSharedServices.BuildItemEntry(slot, (uint)card.ItemId, (uint)card.StackCount));
        }

        // CMD 0x0045 — client requests card layout after settlement screen.
        // Send the deferred card layout and start a fresh 3 s auto-flip timer.
        internal async Task HandleCardStartRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (session.Player.CurDungeonClearState != 4) return;
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] Card start requested by client (CMD 0x0045), sending deferred layout");

            CancelAutoFlip(session);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0045, new byte[] { 0x01 }));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0046, BuildCardLayoutAck()));
            session.Player.CurDungeonClearState = 5;

            StartDelayedAutoFlip(session, delayMs: 4000);
        }

        // df_game_r CParty::ClearDungeon (0x85A9330)
        // Preamble: if (!cleared_flag) return; Epilogue: cleared_flag = 1;
        // Normal dungeon sends NOTI 31 (ENABLE_CLEAR_DUNGEON), sets CurBossKilled
        // + NOTI 279 (0x0117) SECRET_SHOP_NPC — settlement mystery merchant NPC ID
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

        // Returns true if paid card rewards are available (gold or item at indices 4/5 in card list).
        private static bool HasPaidCardReward(List<ClearRewardGenerator.CardReward> cards)
        {
            if (cards == null) return false;
            return (cards.Count > 4 && cards[4].IsGold && cards[4].GoldAmount > 0) ||
                   (cards.Count > 5 && !cards[5].IsGold && cards[5].ItemId > 0);
        }

        // Synchronous return-to-town: mirrors DungeonTutorialHandler.ReturnToVillage packet sequence.
        // Key points: UserState=0x00 (not 0x01), sync await (not fire-and-forget), includes NOTI 0x00CA.
        private async Task ReturnToVillage(EnhancedClientSession session)
        {
            CancelAutoFlip(session);
            DungeonSharedServices.ResetDungeonState(session);
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

        // CMD ACK 71 body — 86JP 8-seat format
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

        // CMD ACK 70 — card layout: u8 resultCode + u16[8] slotStatus
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
