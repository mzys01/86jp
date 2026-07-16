using DfoServer.Game.Inventory;
using DfoServer.Game.Lottery;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class LotteryItemResponseSender
    {
        private const string ProtocolName = "GameProtocol";

        private readonly IInventoryStore _inventoryStore;
        private readonly LotteryDoubleRewardPolicy _doubleRewardPolicy;
        private readonly InventoryRefreshSender _refresh;
        private readonly string _connectionString;
        private readonly Func<byte[], Task> _broadcastGamePacket;

        public LotteryItemResponseSender(
            IInventoryStore inventoryStore,
            LotteryDoubleRewardPolicy doubleRewardPolicy,
            InventoryRefreshSender refresh,
            string connectionString,
            Func<byte[], Task> broadcastGamePacket = null)
        {
            _inventoryStore = inventoryStore
                ?? throw new ArgumentNullException(nameof(inventoryStore));
            _doubleRewardPolicy = doubleRewardPolicy
                ?? throw new ArgumentNullException(nameof(doubleRewardPolicy));
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
            _connectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentException("A database connection string is required.", nameof(connectionString));
            _broadcastGamePacket = broadcastGamePacket;
        }

        public async Task SendOpenResult(
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

        public async Task SendPremiumServiceRefresh(
            EnhancedClientSession session,
            int characterId,
            int accountId)
        {
            try
            {
                var serviceData = PremiumService.BuildPremiumServiceData(
                    _connectionString,
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

        private static async Task SendNativeResult(
            EnhancedClientSession session,
            LotteryOpenResult result,
            CharacterItemListSnapshot snapshot,
            LotteryRewardGrant displayReward,
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
            IReadOnlyList<LotteryRewardGrant> rewards)
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
            IReadOnlyList<LotteryRewardGrant> mainRewards,
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
            IReadOnlyList<LotteryRewardGrant> rewards)
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
    }
}
