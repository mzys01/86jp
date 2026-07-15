using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        private static readonly TimeSpan PendingLotteryOpenTimeout = TimeSpan.FromMinutes(2);

        private readonly object _pendingLotteryLock = new object();
        private readonly Dictionary<Guid, PendingLotteryItemOpen> _pendingLotteryOpens = new Dictionary<Guid, PendingLotteryItemOpen>();

        public async Task Handle_USE_LOTTERY_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
            if (!LotteryItemUseRequest.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B, LotteryItemAckBuilder.BuildError()));
                return;
            }

            if (request.Phase == 0)
            {
                if (!TryLoadLotterySourceItem(session, request.SlotIndex, out var sourceItemTemplateId, out var sourceStackCount))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B, LotteryItemAckBuilder.BuildError()));
                    FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: phase0 rejected empty slot={request.SlotIndex}");
                    return;
                }

                if (!CanOpenLotteryItem(session, request.SlotIndex, sourceItemTemplateId))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B, LotteryItemAckBuilder.BuildError()));
                    FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: phase0 rejected unusable slot={request.SlotIndex} item=0x{sourceItemTemplateId:X8}");
                    return;
                }

                SetPendingLotteryOpen(session.SessionId, request.SlotIndex);
                // The request itself carries the source slot. Echoing either the
                // slot or source item id here makes 86JP run a local side-effect
                // before the real result packet; leave source context to the final result.
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B,
                    LotteryItemAckBuilder.BuildPhaseStartWithoutPreview()));
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: phase0 slot={request.SlotIndex} item=0x{sourceItemTemplateId:X8} count={sourceStackCount} ackSlot=-1 ackPreview=0");
                return;
            }

            var hadPending = TryTakePendingLotteryOpen(session.SessionId, request.SlotIndex, out var pendingOpen);
            var isDirectFastOpen = request.Phase == 1 && !hadPending;
            var (cid, aid) = ResolveOwner(session);
            var openPlan = pendingOpen?.OpenPlan ?? _lotteryOpenPlanner.Resolve(cid, aid, isDirectFastOpen);
            if (isDirectFastOpen && openPlan.UseDoubleReward)
            {
                if (!TryLoadLotterySourceItem(session, request.SlotIndex, out var sourceItemTemplateId, out var sourceStackCount)
                    || !CanOpenLotteryItem(session, request.SlotIndex, sourceItemTemplateId))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B, LotteryItemAckBuilder.BuildError()));
                    FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: double phase start rejected slot={request.SlotIndex}");
                    return;
                }

                SetPendingLotteryOpen(session.SessionId, request.SlotIndex, openPlan);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B,
                    LotteryItemAckBuilder.BuildPhaseStartWithoutPreview()));
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: double phase start slot={request.SlotIndex} item=0x{sourceItemTemplateId:X8} count={sourceStackCount}");
                return;
            }

            if (openPlan.ShouldSendRegularPhaseStart)
            {
                if (!TryLoadLotterySourceItem(session, request.SlotIndex, out var sourceItemTemplateId, out var sourceStackCount)
                    || !CanOpenLotteryItem(session, request.SlotIndex, sourceItemTemplateId))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B, LotteryItemAckBuilder.BuildError()));
                    FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: direct phase1 fallback rejected slot={request.SlotIndex}");
                    return;
                }

                SetPendingLotteryOpen(session.SessionId, request.SlotIndex);
                if (openPlan.RefreshPremiumBeforePhaseStart)
                    await SendPremiumServiceRefresh(session, cid, aid);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B,
                    LotteryItemAckBuilder.BuildPhaseStartWithoutPreview()));
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: direct phase1 fallback to phase0 slot={request.SlotIndex} item=0x{sourceItemTemplateId:X8} count={sourceStackCount} used={openPlan.UsedCount} activeDouble={openPlan.HasActiveDoubleReward} ackPreview=0");
                return;
            }

            if (!await TryOpenLotteryItem(session, request.SlotIndex, openPlan))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B, LotteryItemAckBuilder.BuildError()));
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: open failed phase={request.Phase} slot={request.SlotIndex} mode={openPlan.Mode}");
            }
        }

        public async Task Handle_OVERFLOW_INFO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] OVERFLOW_INFO raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
            if (!IsLotteryOverflowConfirm(body))
                return;

            if (!TryTakePendingLotteryOpen(session.SessionId, null, out var pending))
            {
                FileLogger.Log($"[{ProtocolName}] OVERFLOW_INFO: ignored lottery-shaped confirm without pending phase0");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00D9, OverflowInfoAckBuilder.Build(body)));
            var pendingPlan = pending.OpenPlan ?? LotteryOpenPlan.ConfirmedRegular();
            if (!await TryOpenLotteryItem(session, pending.SlotIndex, pendingPlan))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B, LotteryItemAckBuilder.BuildError()));
                FileLogger.Log($"[{ProtocolName}] OVERFLOW_INFO: pending lottery open failed slot={pending.SlotIndex}");
                return;
            }
        }

        private async Task<bool> TryOpenLotteryItem(EnhancedClientSession session, short slotIndex, LotteryOpenPlan openPlan)
        {
            openPlan = openPlan ?? LotteryOpenPlan.ConfirmedRegular();
            var (cid, aid) = ResolveOwner(session);
            var boosterRequest = openPlan.CreateBoosterUseRequest(slotIndex);
            if (!_inventoryStore.TryUseBoosterItem(
                    cid,
                    aid,
                    boosterRequest,
                    out var result))
            {
                return false;
            }

            await SendLotteryItemOpenResult(session, cid, aid, result, openPlan.UseDoubleReward);
            if (openPlan.RefreshPremiumAfterOpen)
                await SendPremiumServiceRefresh(session, cid, aid);

            FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} remaining={result.SourceRemainingStackCount} gold={result.ConsumedGold}->{result.UpdatedGold} mode={openPlan.Mode} double={openPlan.UseDoubleReward} rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))}");
            return true;
        }

        private async Task SendLotteryItemOpenResult(EnhancedClientSession session, int characterId, int accountId, BoosterUseResult result, bool useDoubleReward)
        {
            var snapshot = _inventoryStore.LoadCharacterItemListSnapshot(characterId, accountId);
            var displayRewards = ResolveLotteryDisplayRewards(result?.Rewards);
            var mainRewards = displayRewards
                .Where(x => x.ListType == InventoryListType.Main)
                .ToList();
            var displayReward = displayRewards.FirstOrDefault();
            var displayItem = ResolveLotteryResultItem(snapshot, displayReward);
            var displayValue = ResolveLotteryDisplayValue(displayItem, displayReward, displayRewards);
            var useDoubleRewardResultFlow = ShouldUseLotteryDoubleRewardResultFlow(useDoubleReward, displayRewards);
            displayValue = ResolveLotteryNativeDisplayValue(displayValue, useDoubleRewardResultFlow);

            await SendLotteryNativeResult(
                session,
                result,
                snapshot,
                displayReward,
                displayItem,
                displayValue);

            var refreshRewards = useDoubleRewardResultFlow
                ? ResolveLotteryPostResultMainRefreshRewards(displayReward, mainRewards, true)
                : ResolveLotteryPostResultMainRefreshRewards(displayReward, mainRewards, false);
            await SendLotteryRewardUpdates(session, snapshot, refreshRewards);

            var firstNoticeItem = ResolveLotteryResultItem(snapshot, mainRewards.FirstOrDefault());
            if (useDoubleRewardResultFlow)
                await BroadcastLotteryItemNotices(session, snapshot, mainRewards, firstNoticeItem, suppressDuplicateNotices: false);
            else
                await BroadcastLotteryItemNotices(session, snapshot, mainRewards, firstNoticeItem);
            await SendAvatarOrPetUpdateListForBoosterRewards(session, result);
            if (ShouldSendLotteryGoldRefresh(result))
                await SendBoosterGoldRefresh(session, result);
        }

        private async Task SendLotteryNativeResult(
            EnhancedClientSession session,
            BoosterUseResult result,
            CharacterItemListSnapshot snapshot,
            BoosterRewardResult displayReward,
            CommonInventoryItem displayItem,
            int displayValue)
        {
            byte[] resultBody;
            if (displayReward?.ListType == InventoryListType.Avatar)
            {
                var avatarItem = ResolveLotteryAvatarResultItem(snapshot, displayReward);
                resultBody = LotteryItemAckBuilder.BuildAvatarItemResult(
                    ResolveLotteryResultSourceSlot(result),
                    avatarItem);
            }
            else
            {
                resultBody = LotteryItemAckBuilder.BuildCommonItemResult(
                    ResolveLotteryResultSourceSlot(result),
                    displayItem,
                    displayValue);
            }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B, resultBody));
        }

        internal static bool ShouldSendLotteryGoldRefresh(BoosterUseResult result)
        {
            return result != null && result.ConsumedGold > 0;
        }

        private Task SendBoosterGoldRefresh(EnhancedClientSession session, BoosterUseResult result)
        {
            if (result == null || result.ConsumedGold <= 0)
                return Task.CompletedTask;

            var body = ItemListUpdateBuilder.BuildGoldUpdate(result.UpdatedGold);
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, body));
        }

        internal static short ResolveLotteryResultSourceSlot(BoosterUseResult result)
        {
            return result != null ? result.SourceSlotIndex : (short)-1;
        }

        internal static CommonInventoryItem ResolveLotteryResultItem(CharacterItemListSnapshot snapshot, BoosterRewardResult reward)
        {
            if (reward == null || reward.ListType != InventoryListType.Main || reward.ItemTemplateId <= 0)
                return null;

            var item = FindLotteryResultItem(snapshot, reward);
            if (item != null)
                return item;

            var metadata = ItemMetadataResolver.Resolve(reward.ItemTemplateId);
            return new CommonInventoryItem
            {
                SlotIndex = reward.SlotIndex,
                ItemTemplateId = reward.ItemTemplateId,
                CountOrInstanceValue = reward.StackCount > 0 ? reward.StackCount : Math.Max(1, reward.GrantedCount),
                Durability = metadata.Durability,
                Marker16 = metadata.IsStackable ? 0 : -1,
                ExpireTime = metadata.IsStackable ? 0 : -1,
            };
        }

        internal static bool ShouldUseLotteryDoubleRewardResultFlow(
            bool useDoubleReward,
            IReadOnlyList<BoosterRewardResult> displayRewards)
        {
            return useDoubleReward
                && displayRewards != null
                && displayRewards.Count > 1;
        }

        internal static AvatarInventoryItem ResolveLotteryAvatarResultItem(
            CharacterItemListSnapshot snapshot,
            BoosterRewardResult reward)
        {
            if (reward == null || reward.ListType != InventoryListType.Avatar || reward.ItemTemplateId <= 0)
                return null;

            return snapshot?.AvatarItems?.FirstOrDefault(item => item != null
                && item.SlotIndex == reward.SlotIndex
                && item.AvatarItemId == reward.ItemTemplateId);
        }

        internal static IReadOnlyList<BoosterRewardResult> ResolveLotteryDisplayRewards(
            IReadOnlyList<BoosterRewardResult> rewards)
        {
            if (rewards == null || rewards.Count == 0)
                return Array.Empty<BoosterRewardResult>();

            return rewards
                .Where(reward => reward != null
                    && reward.ItemTemplateId > 0
                    && (reward.ListType == InventoryListType.Main
                        || reward.ListType == InventoryListType.Avatar))
                .ToList();
        }

        internal static int ResolveLotteryNativeDisplayValue(int resolvedDisplayValue, bool useDoubleRewardResultFlow)
        {
            if (!useDoubleRewardResultFlow)
                return resolvedDisplayValue;

            return resolvedDisplayValue > 0 ? 2 : 0;
        }

        internal static IReadOnlyList<BoosterRewardResult> ResolveLotteryDoubleRewardExtraRefreshRewards(
            IReadOnlyList<BoosterRewardResult> mainRewards)
        {
            if (mainRewards == null || mainRewards.Count <= 1)
                return Array.Empty<BoosterRewardResult>();

            return mainRewards.Skip(1).ToList();
        }

        internal static IReadOnlyList<BoosterRewardResult> ResolveLotteryRegularPostResultRefreshRewards(
            IReadOnlyList<BoosterRewardResult> mainRewards)
        {
            if (mainRewards == null || mainRewards.Count <= 1)
                return Array.Empty<BoosterRewardResult>();

            return ResolveLotteryMainRefreshRewards(mainRewards);
        }

        internal static IReadOnlyList<BoosterRewardResult> ResolveLotteryPostResultMainRefreshRewards(
            BoosterRewardResult displayReward,
            IReadOnlyList<BoosterRewardResult> mainRewards,
            bool useDoubleRewardResultFlow)
        {
            if (mainRewards == null || mainRewards.Count == 0)
                return Array.Empty<BoosterRewardResult>();

            if (displayReward == null || displayReward.ListType != InventoryListType.Main)
                return mainRewards.ToList();

            return useDoubleRewardResultFlow
                ? ResolveLotteryDoubleRewardExtraRefreshRewards(mainRewards)
                : ResolveLotteryRegularPostResultRefreshRewards(mainRewards);
        }

        internal static int ResolveLotteryDisplayValue(
            CommonInventoryItem item,
            BoosterRewardResult reward,
            IReadOnlyList<BoosterRewardResult> sameOpenRewards = null)
        {
            if (item == null)
                return 0;

            var grantedCount = ResolveLotteryDisplayGrantedCount(reward, sameOpenRewards);
            var metadata = ItemMetadataResolver.Resolve(item.ItemTemplateId);
            if (metadata.IsStackable)
                return grantedCount;

            // 86JP treats this field as a display amount in the lottery window.
            // Sending the equipment instance value makes chat show it as gold.
            return grantedCount;
        }

        private static int ResolveLotteryDisplayGrantedCount(
            BoosterRewardResult reward,
            IReadOnlyList<BoosterRewardResult> sameOpenRewards)
        {
            var fallback = Math.Max(1, reward?.GrantedCount ?? 1);
            if (reward == null || sameOpenRewards == null || sameOpenRewards.Count == 0)
                return fallback;

            var total = sameOpenRewards
                .Where(x => x != null
                    && x.ListType == reward.ListType
                    && x.ItemTemplateId == reward.ItemTemplateId)
                .Sum(x => Math.Max(1, x.GrantedCount));
            return Math.Max(fallback, total);
        }

        private static CommonInventoryItem FindLotteryResultItem(CharacterItemListSnapshot snapshot, BoosterRewardResult reward)
        {
            if (snapshot == null || reward == null)
                return null;

            return snapshot.MainItems?.FirstOrDefault(x =>
                x.SlotIndex == reward.SlotIndex && x.ItemTemplateId == reward.ItemTemplateId);
        }

        private async Task SendLotteryRewardUpdates(
            EnhancedClientSession session,
            CharacterItemListSnapshot snapshot,
            IReadOnlyList<BoosterRewardResult> mainRewardsToRefresh)
        {
            if (mainRewardsToRefresh == null || mainRewardsToRefresh.Count == 0)
                return;

            var updates = new List<CommonInventoryItem>();
            foreach (var reward in mainRewardsToRefresh)
            {
                var item = FindLotteryResultItem(snapshot, reward);
                if (item != null)
                    updates.Add(item);
            }

            if (updates.Count == 0)
                return;

            var body = ItemListUpdateBuilder.BuildCommonUpdates(updates);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, body));
            FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: refreshed reward slots {string.Join(",", updates.Select(x => $"0x{x.ItemTemplateId:X8}@{x.SlotIndex}"))}");
        }

        private async Task BroadcastLotteryItemNotices(
            EnhancedClientSession session,
            CharacterItemListSnapshot snapshot,
            IReadOnlyList<BoosterRewardResult> mainRewards,
            CommonInventoryItem firstDisplayItem,
            bool suppressDuplicateNotices = true)
        {
            if (mainRewards == null || mainRewards.Count == 0)
            {
                await BroadcastLotteryItemNotice(session, firstDisplayItem);
                return;
            }

            for (var i = 0; i < mainRewards.Count; i++)
            {
                if (suppressDuplicateNotices && ShouldSuppressLotteryItemNotice(mainRewards[i], mainRewards))
                {
                    FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: notice skipped duplicate item=0x{mainRewards[i].ItemTemplateId:X8}");
                    continue;
                }

                var item = i == 0 ? firstDisplayItem : ResolveLotteryResultItem(snapshot, mainRewards[i]);
                await BroadcastLotteryItemNotice(session, item);
            }
        }

        internal static bool ShouldSuppressLotteryItemNotice(
            BoosterRewardResult reward,
            IReadOnlyList<BoosterRewardResult> sameOpenRewards)
        {
            if (reward == null || sameOpenRewards == null || reward.ItemTemplateId <= 0)
                return false;

            var metadata = ItemMetadataResolver.Resolve(reward.ItemTemplateId);
            if (metadata.IsStackable)
                return false;

            return ResolveDuplicateNoticeTotal(reward, sameOpenRewards) > 1;
        }

        internal static IReadOnlyList<BoosterRewardResult> ResolveLotteryMainRefreshRewards(
            IReadOnlyList<BoosterRewardResult> mainRewards)
        {
            if (mainRewards == null || mainRewards.Count <= 1)
                return Array.Empty<BoosterRewardResult>();

            var duplicateNonStackableKeys = new HashSet<string>(mainRewards
                .Where(reward => reward != null && reward.ItemTemplateId > 0)
                .GroupBy(reward => $"{(byte)reward.ListType}:0x{reward.ItemTemplateId:X8}")
                .Where(group =>
                {
                    var metadata = ItemMetadataResolver.Resolve(group.First().ItemTemplateId);
                    return !metadata.IsStackable && group.Sum(reward => Math.Max(1, reward.GrantedCount)) > 1;
                })
                .Select(group => group.Key));

            if (duplicateNonStackableKeys.Count == 0)
                return mainRewards.Skip(1).ToList();

            return mainRewards
                .Where(reward => reward != null && duplicateNonStackableKeys.Contains($"{(byte)reward.ListType}:0x{reward.ItemTemplateId:X8}"))
                .ToList();
        }

        private async Task BroadcastLotteryItemNotice(EnhancedClientSession session, CommonInventoryItem displayItem)
        {
            if (_broadcastGamePacket == null || displayItem == null || displayItem.ItemTemplateId <= 0)
                return;

            var metadata = ItemMetadataResolver.Resolve(displayItem.ItemTemplateId);
            if (!IsLotteryItemNoticeEligible(metadata))
                return;

            try
            {
                var userUniqueId = session?.Player?.UserId ?? 0;
                if (userUniqueId == 0 && session?.Player?.CharacterId > 0)
                    userUniqueId = (ushort)session.Player.CharacterId;

                var upgradeLevel = (byte)(displayItem.ExtData0 & 0x1F);
                var noticeBody = LotteryItemNoticeBuilder.Build(userUniqueId, displayItem.ItemTemplateId, upgradeLevel);
                await _broadcastGamePacket(GamePacketEnvelopeBuilder.Build(0x00, 0x0056, noticeBody));
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: notice broadcast type=0x0056 uniqueId={userUniqueId} item=0x{displayItem.ItemTemplateId:X8} upgrade={upgradeLevel}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: notice broadcast failed: {ex.Message}");
            }
        }

        internal static bool IsLotteryItemNoticeEligible(ItemMetadata metadata)
        {
            if (metadata == null || metadata.IsStackable)
                return false;

            return metadata.Rarity >= 3
                || string.Equals(metadata.ItemCategory, "legacy", StringComparison.OrdinalIgnoreCase);
        }

        private bool CanOpenLotteryItem(EnhancedClientSession session, short slotIndex, int sourceItemTemplateId)
        {
            var (cid, aid) = ResolveOwner(session);
            return _inventoryStore.CanUseBoosterItem(
                cid,
                aid,
                new BoosterUseRequest
                {
                    SlotIndex = slotIndex,
                    ExpectedItemTemplateId = sourceItemTemplateId,
                    SelectedItemTemplateIds = Array.Empty<int>(),
                });
        }

        internal static int ResolveLotterySourceContextCount(int sourceStackCount)
        {
            return Math.Max(0, sourceStackCount);
        }

        private bool TryLoadLotterySourceItem(EnhancedClientSession session, short slotIndex, out int sourceItemTemplateId, out int sourceStackCount)
        {
            sourceItemTemplateId = 0;
            sourceStackCount = 0;
            var (cid, aid) = ResolveOwner(session);
            var snapshot = _inventoryStore.LoadCharacterItemListSnapshot(cid, aid);
            var item = snapshot?.MainItems?.FirstOrDefault(x => x.SlotIndex == slotIndex);
            if (item == null || item.ItemTemplateId <= 0)
                return false;

            sourceItemTemplateId = item.ItemTemplateId;
            sourceStackCount = ResolveLotterySourceContextCount(item.CountOrInstanceValue);
            return true;
        }

        private async Task SendPremiumServiceRefresh(EnhancedClientSession session, int characterId, int accountId)
        {
            try
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var serviceData = PremiumService.BuildPremiumServiceData(connStr, accountId, characterId, _dailyResetService);
                var writer = new GamePacketWriter();
                writer.WriteByte(1);
                writer.WriteUInt16(1);
                writer.WriteBytes(serviceData);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0312, writer.ToArray()));
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: premium service refresh sent char={characterId} account={accountId}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: premium service refresh failed: {ex.Message}");
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

        private void SetPendingLotteryOpen(Guid sessionId, short slotIndex, LotteryOpenPlan openPlan = null)
        {
            var nowUtc = DateTime.UtcNow;
            lock (_pendingLotteryLock)
            {
                CleanupExpiredPendingLotteryOpensLocked(nowUtc);
                _pendingLotteryOpens[sessionId] = new PendingLotteryItemOpen(slotIndex, nowUtc, openPlan);
            }
        }

        private bool TryTakePendingLotteryOpen(Guid sessionId, short? expectedSlotIndex, out PendingLotteryItemOpen pending)
        {
            lock (_pendingLotteryLock)
            {
                CleanupExpiredPendingLotteryOpensLocked(DateTime.UtcNow);
                if (!_pendingLotteryOpens.TryGetValue(sessionId, out pending))
                    return false;

                if (expectedSlotIndex.HasValue && pending.SlotIndex != expectedSlotIndex.Value)
                    return false;

                _pendingLotteryOpens.Remove(sessionId);
                return true;
            }
        }

        private void CleanupExpiredPendingLotteryOpensLocked(DateTime nowUtc)
        {
            if (_pendingLotteryOpens.Count == 0)
                return;

            var expired = _pendingLotteryOpens
                .Where(pair => nowUtc - pair.Value.CreatedAtUtc > PendingLotteryOpenTimeout)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var sessionId in expired)
                _pendingLotteryOpens.Remove(sessionId);
        }

        private sealed class PendingLotteryItemOpen
        {
            public PendingLotteryItemOpen(short slotIndex, DateTime createdAtUtc, LotteryOpenPlan openPlan)
            {
                SlotIndex = slotIndex;
                CreatedAtUtc = createdAtUtc;
                OpenPlan = openPlan;
            }

            public short SlotIndex { get; }

            public DateTime CreatedAtUtc { get; }

            public LotteryOpenPlan OpenPlan { get; }
        }

        private static int ResolveDuplicateNoticeTotal(
            BoosterRewardResult reward,
            IReadOnlyList<BoosterRewardResult> sameOpenRewards)
        {
            if (reward == null || sameOpenRewards == null)
                return 0;

            return sameOpenRewards
                .Where(x => x != null
                    && x.ListType == reward.ListType
                    && x.ItemTemplateId == reward.ItemTemplateId)
                .Sum(x => Math.Max(1, x.GrantedCount));
        }

    }
}
