using DfoServer.Game.Inventory;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Game.Dungeon
{
    internal sealed class CardRewardService
    {
        private readonly DungeonSharedServices _svc;
        private readonly IAssetService _assetService;

        internal CardRewardService(DungeonSharedServices svc, IAssetService assetService)
        {
            _svc = svc ?? throw new ArgumentNullException(nameof(svc));
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
        }

        internal void ScheduleAutoFlow(EnhancedClientSession session, int layoutDelayMs, int autoFlipDelayMs)
        {
            DungeonRunLifecycle.CancelAutoFlip(session);
            var run = session.Player.CurrentRun;
            if (run == null) return;

            var cts = new CancellationTokenSource();
            run.AutoFlipCts = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(layoutDelayMs, token);
                    if (token.IsCancellationRequested) return;
                    if (!ReferenceEquals(session.Player.CurrentRun, run)) return;
                    if (run.Phase != DungeonRunPhase.ResultShown) return;

                    FileLogger.Log("[CardReward] Auto-layout timer fired");
                    await SendCardLayout(session);
                    run.Phase = DungeonRunPhase.CardsRevealed;

                    await Task.Delay(autoFlipDelayMs, token);
                    if (token.IsCancellationRequested) return;
                    if (!ReferenceEquals(session.Player.CurrentRun, run)) return;

                    FileLogger.Log("[CardReward] Auto-flip timer fired");
                    await AutoFlipFreeCard(session, run);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex) { FileLogger.Log($"[CardReward] Auto-flow error: {ex}"); }
            }, token);
        }

        internal void StartDelayedAutoFlip(EnhancedClientSession session, int delayMs)
        {
            DungeonRunLifecycle.CancelAutoFlip(session);
            var run = session.Player.CurrentRun;
            if (run == null) return;

            var cts = new CancellationTokenSource();
            run.AutoFlipCts = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, token);
                    if (token.IsCancellationRequested) return;
                    if (!ReferenceEquals(session.Player.CurrentRun, run)) return;
                    FileLogger.Log("[CardReward] Standalone auto-flip timer fired");
                    await AutoFlipFreeCard(session, run);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex) { FileLogger.Log($"[CardReward] Auto-flip error: {ex}"); }
            }, token);
        }

        internal async Task HandleSelectCard(EnhancedClientSession session, byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null || body == null || body.Length < 2) return;
            byte cardType = body[0];
            byte cardIndex = body[1];

            if (run.Phase == DungeonRunPhase.ResultShown)
            {
                DungeonRunLifecycle.CancelAutoFlip(session);
                await SendCardLayout(session);
                run.Phase = DungeonRunPhase.CardsRevealed;
                StartDelayedAutoFlip(session, 4000);
                return;
            }

            if (cardType > 1 || cardIndex > 3) return;
            lock (run.SyncRoot)
            {
                if (run.CardRewards == null) return;
                var slots = cardType == 0 ? run.FreeCardSlots : run.PaidCardSlots;
                if (slots[cardIndex] != 0xFF) return;

                run.CardFlipCount++;
                slots[cardIndex] = 0x00;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047, BuildCardInfoAck(session)));
        }

        internal async Task HandleCardStartRequest(EnhancedClientSession session)
        {
            var run = session.Player.CurrentRun;
            if (run == null || run.Phase != DungeonRunPhase.ResultShown) return;

            DungeonRunLifecycle.CancelAutoFlip(session);
            await SendCardLayout(session);
            run.Phase = DungeonRunPhase.CardsRevealed;
            StartDelayedAutoFlip(session, 4000);
        }

        // Returns true if caller should proceed to ReturnToVillage.
        internal async Task<bool> HandleEplpCommand(EnhancedClientSession session, byte[] body)
        {
            if (body == null || body.Length < 2) return false;
            byte state = body[0];
            byte option = body[1];
            var run = session.Player.CurrentRun;

            if (run != null && run.Phase == DungeonRunPhase.ResultShown)
            {
                DungeonRunLifecycle.CancelAutoFlip(session);
                await SendCardLayout(session);
                run.Phase = DungeonRunPhase.CardsRevealed;
                StartDelayedAutoFlip(session, 4000);
                return false;
            }

            if (state == 1)
            {
                DungeonRunLifecycle.CancelAutoFlip(session);
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0048,
                new byte[] { 0x01, state, option }));

            return state == 1;
        }

        private async Task AutoFlipFreeCard(EnhancedClientSession session, DungeonRun run)
        {
            var sendCardInfo = false;
            lock (run.SyncRoot)
            {
                if (run.CardRewards == null) return;
                if (run.FreeCardSlots[0] == 0xFF)
                {
                    run.CardFlipCount++;
                    run.FreeCardSlots[0] = 0x00;
                    sendCardInfo = true;
                }
            }

            if (sendCardInfo)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0047, BuildCardInfoAck(session)));

            await DeliverCardRewards(session);
        }

        private async Task DeliverCardRewards(EnhancedClientSession session)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;

            List<ClearRewardGenerator.CardReward> cards;
            bool freeSelected;
            bool paidSelected;
            lock (run.SyncRoot)
            {
                cards = run.CardRewards;
                if (cards == null) return;

                freeSelected = run.FreeCardSlots[0] != 0xFF;
                paidSelected = run.PaidCardSlots[0] != 0xFF;
                if (!freeSelected && !paidSelected) return;

                run.CardRewards = null;
            }

            if (cards == null) return;
            var cid = session.Player.CharacterId;
            var aid = session.Account?.AccountId ?? 1;
            var entries = new List<byte[]>();

            if (freeSelected)
            {
                CollectGoldReward(cid, aid, cards, 0, entries);
                CollectItemReward(cid, aid, cards, 1, entries);
            }
            if (paidSelected)
            {
                CollectGoldReward(cid, aid, cards, 4, entries);
                CollectItemReward(cid, aid, cards, 5, entries);
            }

            await SendItemUpdates(session, entries);
            FileLogger.Log($"[CardReward] Rewards delivered: {entries.Count} entries");
        }

        private void CollectGoldReward(int cid, int aid, List<ClearRewardGenerator.CardReward> cards, int index, List<byte[]> entries)
        {
            if (cards.Count <= index || !cards[index].IsGold || cards[index].GoldAmount <= 0) return;
            try
            {
                using (var scope = _assetService.OpenScope(cid, aid))
                {
                    _assetService.GrantGold(scope, cards[index].GoldAmount);
                    scope.Commit();
                    var wallet = _assetService.LoadWallet(scope);
                    entries.Add(ItemListUpdateBuilder.BuildRawItemEntry(0, 0, (uint)wallet.Gold));
                }
            }
            catch (Exception ex) { FileLogger.Log($"[CardReward] CollectGoldReward ERROR: {ex.Message}"); }
        }

        private void CollectItemReward(int cid, int aid, List<ClearRewardGenerator.CardReward> cards, int index, List<byte[]> entries)
        {
            if (cards.Count <= index || cards[index].IsGold || cards[index].ItemId <= 0) return;
            var card = cards[index];
            try
            {
                using (var scope = _assetService.OpenScope(cid, aid))
                {
                    if (!_assetService.TryAddItem(scope, card.ItemId, card.StackCount, out var slot)) return;
                    scope.Commit();
                    var sealFlag = card.IsEquipment && ItemMetadataResolver.Resolve(card.ItemId).IsSealed ? (byte)1 : (byte)0;
                    entries.Add(card.IsEquipment
                        ? ItemListUpdateBuilder.BuildRawEquipEntry(slot, (uint)card.ItemId, durability: card.Durability, sealFlag: sealFlag)
                        : ItemListUpdateBuilder.BuildRawItemEntry(slot, (uint)card.ItemId, (uint)card.StackCount));
                }
            }
            catch (Exception ex) { FileLogger.Log($"[CardReward] CollectItemReward ERROR: {ex.Message}"); }
        }

        private static async Task SendItemUpdates(EnhancedClientSession session, List<byte[]> entries)
        {
            if (entries.Count == 0) return;
            var w = new GamePacketWriter();
            w.WriteByte(0);
            w.WriteUInt16((ushort)entries.Count);
            foreach (var e in entries) w.WriteBytes(e);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, w.ToArray()));
        }

        private static async Task SendCardLayout(EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0045, new byte[] { 0x01 }));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0046, BuildCardLayoutAck()));
        }

        private static byte[] BuildCardInfoAck(EnhancedClientSession session)
        {
            var run = session.Player.CurrentRun;
            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            for (int i = 0; i < 8; i++)
            {
                if (i >= 4) { w.WriteByte(0xFF); w.WriteByte(0xFF); w.WriteByte(0xFF); w.WriteByte(0xFF); continue; }
                bool freeSelected = run.FreeCardSlots[i] != 0xFF;
                bool paidSelected = run.PaidCardSlots[i] != 0xFF;
                if (i != 0) { w.WriteByte(0xFF); w.WriteByte(0xFF); w.WriteByte(0x00); w.WriteByte(0x00); continue; }
                w.WriteByte(freeSelected ? (byte)0x00 : (byte)0xFF);
                w.WriteByte(paidSelected ? (byte)0x00 : (byte)0xFF);
                if (paidSelected)
                {
                    var cards = run.CardRewards;
                    int paidGoldAmt = (cards != null && cards.Count > 4 && cards[4].IsGold) ? cards[4].GoldAmount : 0;
                    int paidItemId = (cards != null && cards.Count > 5 && !cards[5].IsGold) ? cards[5].ItemId : 0;
                    int paidItemCnt = (cards != null && cards.Count > 5 && !cards[5].IsGold) ? cards[5].StackCount : 0;
                    w.WriteByte(2);
                    w.WriteUInt32(0);
                    w.WriteInt32(paidGoldAmt);
                    w.WriteUInt32((uint)paidItemId);
                    w.WriteInt32(paidItemCnt);
                }
                else { w.WriteByte(0x00); }
                w.WriteByte(0x00);
            }
            return w.ToArray();
        }

        private static byte[] BuildCardLayoutAck()
        {
            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(0x0001);
            for (int i = 1; i < 8; i++) w.WriteUInt16(0xFFFF);
            return w.ToArray();
        }
    }
}
