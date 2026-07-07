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

            var hadPending = TryTakePendingLotteryOpen(session.SessionId, request.SlotIndex, out _);
            var isDirectFastOpen = request.Phase == 1 && !hadPending;
            var (cid, aid) = ResolveOwner(session);
            var openPlan = _lotteryOpenPlanner.Resolve(cid, aid, isDirectFastOpen);
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
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00D9, OverflowInfoAckBuilder.Build(body)));
                return;
            }

            if (!TryTakePendingLotteryOpen(session.SessionId, null, out var pending))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00D9, OverflowInfoAckBuilder.Build(body)));
                FileLogger.Log($"[{ProtocolName}] OVERFLOW_INFO: lottery confirm without pending phase0");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00D9, OverflowInfoAckBuilder.Build(body)));
            if (!await TryOpenLotteryItem(session, pending.SlotIndex, LotteryOpenPlan.ConfirmedRegular()))
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
            if (!_sqliteSelectCharacterDataSource.TryUseBoosterItem(
                    cid,
                    aid,
                    openPlan.CreateBoosterUseRequest(slotIndex),
                    out var result))
            {
                return false;
            }

            await SendLotteryItemOpenResult(session, cid, aid, result);
            if (openPlan.RefreshPremiumAfterOpen)
                await SendPremiumServiceRefresh(session, cid, aid);

            FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} remaining={result.SourceRemainingStackCount} gold={result.ConsumedGold}->{result.UpdatedGold} mode={openPlan.Mode} double={openPlan.UseDoubleReward} rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))}");
            return true;
        }

        private async Task SendLotteryItemOpenResult(EnhancedClientSession session, int characterId, int accountId, BoosterUseResult result)
        {
            var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(characterId, accountId);
            var mainRewards = result.Rewards
                .Where(x => x.ListType == InventoryListType.Main)
                .ToList();
            var displayReward = mainRewards.FirstOrDefault();
            var displayItem = ResolveLotteryResultItem(snapshot, displayReward);
            var displayValue = ResolveLotteryDisplayValue(displayItem, displayReward, mainRewards);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001B,
                LotteryItemAckBuilder.BuildCommonItemResult(ResolveLotteryResultSourceSlot(result), displayItem, displayValue)));

            await SendAdditionalLotteryRewardUpdates(session, snapshot, mainRewards.Skip(1).ToList());
            await BroadcastLotteryItemNotices(session, snapshot, mainRewards, displayItem);
            await SendAvatarOrPetUpdateListForBoosterRewards(session, result);
            if (ShouldSendBoosterGoldRefresh(0x001B, result))
                await SendBoosterGoldRefresh(session, result);
        }

        internal static bool ShouldSendBoosterGoldRefresh(ushort responseType, BoosterUseResult result)
        {
            return result != null && result.ConsumedGold > 0;
        }

        private static Task SendBoosterGoldRefresh(EnhancedClientSession session, BoosterUseResult result)
        {
            if (result == null || result.ConsumedGold <= 0)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000E,
                ItemListUpdateBuilder.BuildGoldUpdate(result.UpdatedGold)));
        }

        internal static short ResolveLotteryResultSourceSlot(BoosterUseResult result)
        {
            return result != null ? result.SourceSlotIndex : (short)-1;
        }

        private static CommonInventoryItem ResolveLotteryResultItem(CharacterItemListSnapshot snapshot, BoosterRewardResult reward)
        {
            if (reward == null || reward.ItemTemplateId <= 0)
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

        private async Task SendAdditionalLotteryRewardUpdates(
            EnhancedClientSession session,
            CharacterItemListSnapshot snapshot,
            IReadOnlyList<BoosterRewardResult> additionalMainRewards)
        {
            if (additionalMainRewards == null || additionalMainRewards.Count == 0)
                return;

            var updates = new List<CommonInventoryItem>();
            foreach (var reward in additionalMainRewards)
            {
                var item = FindLotteryResultItem(snapshot, reward);
                if (item != null)
                    updates.Add(item);
            }

            if (updates.Count == 0)
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000E,
                ItemListUpdateBuilder.BuildCommonUpdates(updates)));
            FileLogger.Log($"[{ProtocolName}] USE_LOTTERY_ITEM: refreshed additional rewards {string.Join(",", updates.Select(x => $"0x{x.ItemTemplateId:X8}@{x.SlotIndex}"))}");
        }

        private async Task BroadcastLotteryItemNotices(
            EnhancedClientSession session,
            CharacterItemListSnapshot snapshot,
            IReadOnlyList<BoosterRewardResult> mainRewards,
            CommonInventoryItem firstDisplayItem)
        {
            if (mainRewards == null || mainRewards.Count == 0)
            {
                await BroadcastLotteryItemNotice(session, firstDisplayItem);
                return;
            }

            for (var i = 0; i < mainRewards.Count; i++)
            {
                var item = i == 0 ? firstDisplayItem : ResolveLotteryResultItem(snapshot, mainRewards[i]);
                await BroadcastLotteryItemNotice(session, item);
            }
        }

        private async Task BroadcastLotteryItemNotice(EnhancedClientSession session, CommonInventoryItem displayItem)
        {
            if (_broadcastGamePacket == null || displayItem == null || displayItem.ItemTemplateId <= 0)
                return;

            var metadata = ItemMetadataResolver.Resolve(displayItem.ItemTemplateId);
            if (metadata.IsStackable)
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

        private bool CanOpenLotteryItem(EnhancedClientSession session, short slotIndex, int sourceItemTemplateId)
        {
            var (cid, aid) = ResolveOwner(session);
            return _sqliteSelectCharacterDataSource.CanUseBoosterItem(
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
            var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
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

        private static bool IsLotteryOverflowConfirm(byte[] body)
        {
            return body != null
                && body.Length >= 3
                && body[0] == 0x01
                && body[1] == 0x1B
                && body[2] == 0x00;
        }

        private void SetPendingLotteryOpen(Guid sessionId, short slotIndex)
        {
            var nowUtc = DateTime.UtcNow;
            lock (_pendingLotteryLock)
            {
                CleanupExpiredPendingLotteryOpensLocked(nowUtc);
                _pendingLotteryOpens[sessionId] = new PendingLotteryItemOpen(slotIndex, nowUtc);
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
            public PendingLotteryItemOpen(short slotIndex, DateTime createdAtUtc)
            {
                SlotIndex = slotIndex;
                CreatedAtUtc = createdAtUtc;
            }

            public short SlotIndex { get; }

            public DateTime CreatedAtUtc { get; }
        }
    }
}
