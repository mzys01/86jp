using DfoServer.Game.Inventory;
using DfoServer.Game.Lottery;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Lottery;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class LotteryItemHandler
    {
        private const string ProtocolName = "GameProtocol";

        private readonly IInventoryStore _inventoryStore;
        private readonly LotteryItemOpenService _openService;
        private readonly LotteryOpenPlanner _openPlanner;
        private readonly LotteryOpenSessionCoordinator _sessions;
        private readonly LotteryDoubleRewardPolicy _doubleRewardPolicy;
        private readonly InventoryRefreshSender _refresh;
        private readonly Func<byte[], Task> _broadcastGamePacket;

        public LotteryItemHandler(
            IInventoryStore inventoryStore,
            LotteryItemOpenService openService,
            LotteryOpenPlanner openPlanner,
            LotteryOpenSessionCoordinator sessions,
            LotteryDoubleRewardPolicy doubleRewardPolicy,
            InventoryRefreshSender refresh,
            Func<byte[], Task> broadcastGamePacket = null)
        {
            _inventoryStore = inventoryStore ?? throw new ArgumentNullException(nameof(inventoryStore));
            _openService = openService ?? throw new ArgumentNullException(nameof(openService));
            _openPlanner = openPlanner ?? throw new ArgumentNullException(nameof(openPlanner));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _doubleRewardPolicy = doubleRewardPolicy
                ?? throw new ArgumentNullException(nameof(doubleRewardPolicy));
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
            _broadcastGamePacket = broadcastGamePacket;
        }

        public async Task HandleUseLotteryItem(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
            if (!LotteryItemUseRequest.TryParse(body, out var request))
            {
                await SendError(session);
                return;
            }

            if (request.Phase == 0)
            {
                if (!TryInspect(session, request.SlotIndex, out var source))
                {
                    await SendError(session);
                    FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: phase0 rejected slot={request.SlotIndex}");
                    return;
                }

                _sessions.Set(session.SessionId, request.SlotIndex);
                await SendPhaseStart(session);
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: phase0 slot={request.SlotIndex} item=0x{source.ItemTemplateId:X8} count={source.StackCount} ackSlot=-1 ackPreview=0");
                return;
            }

            var hadPending = _sessions.TryTake(
                session.SessionId,
                request.SlotIndex,
                out var pendingOpen);
            var isDirectFastOpen = request.Phase == 1 && !hadPending;
            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);
            var openPlan = pendingOpen?.OpenPlan
                ?? _openPlanner.Resolve(characterId, accountId, isDirectFastOpen);
            if (isDirectFastOpen && openPlan.UseDoubleReward)
            {
                if (!TryInspect(session, request.SlotIndex, out var source))
                {
                    await SendError(session);
                    FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: double phase start rejected slot={request.SlotIndex}");
                    return;
                }

                _sessions.Set(session.SessionId, request.SlotIndex, openPlan);
                await SendPhaseStart(session);
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: double phase start slot={request.SlotIndex} item=0x{source.ItemTemplateId:X8} count={source.StackCount}");
                return;
            }

            if (openPlan.ShouldSendRegularPhaseStart)
            {
                if (!TryInspect(session, request.SlotIndex, out var source))
                {
                    await SendError(session);
                    FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: direct phase1 fallback rejected slot={request.SlotIndex}");
                    return;
                }

                _sessions.Set(session.SessionId, request.SlotIndex);
                if (openPlan.RefreshPremiumBeforePhaseStart)
                    await SendPremiumServiceRefresh(session, characterId, accountId);
                await SendPhaseStart(session);
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: direct phase1 fallback to phase0 slot={request.SlotIndex} item=0x{source.ItemTemplateId:X8} count={source.StackCount} used={openPlan.UsedCount} activeDouble={openPlan.HasActiveDoubleReward} ackPreview=0");
                return;
            }

            if (!await TryOpen(session, request.SlotIndex, openPlan))
            {
                await SendError(session);
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: open failed phase={request.Phase} slot={request.SlotIndex} mode={openPlan.Mode}");
            }
        }

        public async Task HandleOverflowInfo(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] OVERFLOW_INFO raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
            if (!IsLotteryOverflowConfirm(body))
                return;

            if (!_sessions.TryTake(session.SessionId, null, out var pending))
            {
                FileLogger.Log($"[{ProtocolName}] OVERFLOW_INFO: ignored lottery-shaped confirm without pending phase0");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x00D9,
                LotteryOverflowConfirmAckBuilder.Build(body)));
            var openPlan = pending.OpenPlan ?? LotteryOpenPlan.ConfirmedRegular();
            if (!await TryOpen(session, pending.SlotIndex, openPlan))
            {
                await SendError(session);
                FileLogger.Log($"[{ProtocolName}] OVERFLOW_INFO: pending lottery open failed slot={pending.SlotIndex}");
            }
        }

        internal static bool IsLotteryOverflowConfirm(byte[] body)
        {
            return body != null
                && body.Length == 3
                && body[0] == 0x01
                && body[1] == 0x1B
                && body[2] == 0x00;
        }

        public void ClearSession(Guid sessionId)
        {
            _sessions.Remove(sessionId);
        }

        private bool TryInspect(
            EnhancedClientSession session,
            short slotIndex,
            out LotterySourceContext source)
        {
            var (characterId, _) = SessionOwnerResolver.Resolve(session);
            return _openService.CanOpen(characterId, slotIndex, out source);
        }

        private async Task<bool> TryOpen(
            EnhancedClientSession session,
            short slotIndex,
            LotteryOpenPlan openPlan)
        {
            openPlan = openPlan ?? LotteryOpenPlan.ConfirmedRegular();
            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);
            if (!_openService.TryOpen(
                    characterId,
                    accountId,
                    slotIndex,
                    openPlan.UseDoubleReward,
                    out var result))
            {
                return false;
            }

            await SendOpenResult(session, characterId, accountId, result);
            if (openPlan.RefreshPremiumAfterOpen)
                await SendPremiumServiceRefresh(session, characterId, accountId);

            FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} remaining={result.SourceRemainingStackCount} gold={result.ConsumedGold}->{result.UpdatedGold} mode={openPlan.Mode} double={result.UsedDoubleReward} rewards={string.Join(",", result.Rewards.Select(reward => $"{reward.ListType}:0x{reward.ItemTemplateId:X8}x{reward.GrantedCount}@{reward.SlotIndex}"))}");
            return true;
        }

        private async Task SendOpenResult(
            EnhancedClientSession session,
            int characterId,
            int accountId,
            LotteryOpenResult result)
        {
            var snapshot = _inventoryStore.LoadCharacterItemListSnapshot(characterId, accountId);
            var displayRewards = LotteryPresentationPolicy.ResolveDisplayRewards(result?.Rewards);
            var mainRewards = displayRewards
                .Where(reward => reward.ListType == InventoryListType.Main)
                .ToList();
            var displayReward = displayRewards.FirstOrDefault();
            var displayItem = LotteryPresentationPolicy.ResolveResultItem(snapshot, displayReward);
            var displayValue = LotteryPresentationPolicy.ResolveDisplayValue(
                displayItem,
                displayReward,
                displayRewards);
            var useDoubleResultFlow = LotteryPresentationPolicy.ShouldUseDoubleRewardResultFlow(
                result.UsedDoubleReward,
                displayRewards);
            displayValue = LotteryPresentationPolicy.ResolveNativeDisplayValue(
                displayValue,
                useDoubleResultFlow);

            await SendNativeResult(
                session,
                result,
                snapshot,
                displayReward,
                displayItem,
                displayValue);

            var refreshRewards = LotteryPresentationPolicy.ResolvePostResultMainRefreshRewards(
                displayReward,
                mainRewards,
                useDoubleResultFlow);
            await SendRewardUpdates(session, snapshot, refreshRewards);

            var firstNoticeItem = LotteryPresentationPolicy.ResolveResultItem(
                snapshot,
                mainRewards.FirstOrDefault());
            await BroadcastNotices(
                session,
                snapshot,
                mainRewards,
                firstNoticeItem,
                suppressDuplicateNotices: !useDoubleResultFlow);
            await SendAvatarOrPetUpdates(session, result.Rewards);
            if (LotteryPresentationPolicy.ShouldSendGoldRefresh(result))
                await SendGoldRefresh(session, result.UpdatedGold);
        }

        private static async Task SendNativeResult(
            EnhancedClientSession session,
            LotteryOpenResult result,
            CharacterItemListSnapshot snapshot,
            BoosterRewardResult displayReward,
            CommonInventoryItem displayItem,
            int displayValue)
        {
            byte[] resultBody;
            if (displayReward?.ListType == InventoryListType.Avatar)
            {
                resultBody = LotteryItemAckBuilder.BuildAvatarItemResult(
                    result?.SourceSlotIndex ?? (short)-1,
                    LotteryPresentationPolicy.ResolveAvatarResultItem(snapshot, displayReward));
            }
            else
            {
                resultBody = LotteryItemAckBuilder.BuildCommonItemResult(
                    result?.SourceSlotIndex ?? (short)-1,
                    displayItem,
                    displayValue);
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B, resultBody));
        }

        private static async Task SendRewardUpdates(
            EnhancedClientSession session,
            CharacterItemListSnapshot snapshot,
            IReadOnlyList<BoosterRewardResult> rewards)
        {
            if (rewards == null || rewards.Count == 0)
                return;

            var updates = rewards
                .Select(reward => LotteryPresentationPolicy.FindResultItem(snapshot, reward))
                .Where(item => item != null)
                .ToList();
            if (updates.Count == 0)
                return;

            var body = ItemListUpdateBuilder.BuildCommonUpdates(updates);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, body));
            FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: refreshed reward slots {string.Join(",", updates.Select(item => $"0x{item.ItemTemplateId:X8}@{item.SlotIndex}"))}");
        }

        private async Task BroadcastNotices(
            EnhancedClientSession session,
            CharacterItemListSnapshot snapshot,
            IReadOnlyList<BoosterRewardResult> mainRewards,
            CommonInventoryItem firstDisplayItem,
            bool suppressDuplicateNotices)
        {
            if (mainRewards == null || mainRewards.Count == 0)
            {
                await BroadcastNotice(session, firstDisplayItem);
                return;
            }

            for (var index = 0; index < mainRewards.Count; index++)
            {
                if (suppressDuplicateNotices
                    && LotteryPresentationPolicy.ShouldSuppressNotice(
                        mainRewards[index],
                        mainRewards))
                {
                    continue;
                }

                var item = index == 0
                    ? firstDisplayItem
                    : LotteryPresentationPolicy.ResolveResultItem(snapshot, mainRewards[index]);
                await BroadcastNotice(session, item);
            }
        }

        private async Task BroadcastNotice(
            EnhancedClientSession session,
            CommonInventoryItem displayItem)
        {
            if (_broadcastGamePacket == null
                || displayItem == null
                || displayItem.ItemTemplateId <= 0)
            {
                return;
            }

            var metadata = ItemMetadataResolver.Resolve(displayItem.ItemTemplateId);
            if (!LotteryPresentationPolicy.IsNoticeEligible(metadata))
                return;

            try
            {
                var userUniqueId = session?.Player?.UserId ?? 0;
                if (userUniqueId == 0 && session?.Player?.CharacterId > 0)
                    userUniqueId = (ushort)session.Player.CharacterId;

                var upgradeLevel = (byte)(displayItem.ExtData0 & 0x1F);
                var noticeBody = LotteryItemNoticeBuilder.Build(
                    userUniqueId,
                    displayItem.ItemTemplateId,
                    upgradeLevel);
                await _broadcastGamePacket(GamePacketEnvelopeBuilder.Build(0x00, 0x0056, noticeBody));
            }
            catch (Exception exception)
            {
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: notice broadcast failed: {exception.Message}");
            }
        }

        private async Task SendAvatarOrPetUpdates(
            EnhancedClientSession session,
            IReadOnlyList<BoosterRewardResult> rewards)
        {
            if (rewards == null)
                return;

            var avatarSlots = rewards
                .Where(reward => reward.ListType == InventoryListType.Avatar)
                .Select(reward => reward.SlotIndex)
                .ToHashSet();
            var petSlots = rewards
                .Where(reward => reward.ListType == InventoryListType.Pet)
                .Select(reward => reward.SlotIndex)
                .ToHashSet();
            if (avatarSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Avatar, avatarSlots);
            if (petSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Pet, petSlots);
        }

        private static Task SendGoldRefresh(EnhancedClientSession session, int updatedGold)
        {
            var body = ItemListUpdateBuilder.BuildGoldUpdate(updatedGold);
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, body));
        }

        private async Task SendPremiumServiceRefresh(
            EnhancedClientSession session,
            int characterId,
            int accountId)
        {
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var serviceData = PremiumService.BuildPremiumServiceData(
                    connectionString,
                    accountId,
                    _doubleRewardPolicy.BuildPremiumServiceUsage(characterId));
                var writer = new GamePacketWriter();
                writer.WriteByte(1);
                writer.WriteUInt16(1);
                writer.WriteBytes(serviceData);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0312,
                    writer.ToArray()));
            }
            catch (Exception exception)
            {
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: premium service refresh failed: {exception.Message}");
            }
        }

        private static Task SendPhaseStart(EnhancedClientSession session)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x001B,
                LotteryItemAckBuilder.BuildPhaseStartWithoutPreview()));
        }

        private static Task SendError(EnhancedClientSession session)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x001B,
                LotteryItemAckBuilder.BuildError()));
        }
    }
}
