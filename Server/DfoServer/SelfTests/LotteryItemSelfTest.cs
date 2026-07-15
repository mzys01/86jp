using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Inventory;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class LotteryItemSelfTest
    {
        private const int AccountId = 1;
        private const int CharacterId = 999218;
        private const short LotterySlot = 105;
        private const short DoubleLotterySlot = 106;
        private const short UpgradableLegacySlot = 107;
        private const short HeroLotterySlot = 108;
        private const short AncientHeroLotterySlot = 109;
        private const short RewardSlot = 120;
        private const int SampleBoosterItemId = 10007997;
        private const int SampleRewardItemId = 400360011;
        private const int DoubleRewardBoosterItemId = 10007477;
        private const int DoubleRewardMagicBoxItemId = 10007368;
        private const int DoubleRewardMagicHammerItemId = 10007367;
        private const int DoubleRewardPerItemCount = 100;
        private const int SampleUpgradableLegacyItemId = 10014964;
        private const int SampleHeroLotteryItemId = 8095;
        private const int SampleAncientHeroLotteryItemId = 8213;
        private const int OrdinaryStackableItemId = 2600014;
        private const int HeroLotteryGoldCost = 40000000;
        private const int AncientHeroLotteryGoldCost = 5000000;
        private const int CannedAvatarItemId = 39075;
        private const int NormalRareEquipmentItemId = 100150193;
        private const int LegacyEquipmentItemId = 100150516;
        private const int EpicEquipmentItemId = 101000004;

        public static int Run()
        {
            Console.WriteLine("=== LOTTERY_ITEM selftest ===");
            var failures = 0;

            Check("parse phase0", LotteryItemUseRequest.TryParse(new byte[] { 0x00, 0x00, 0x69, 0x00 }, out var phase0)
                && phase0.Phase == 0
                && phase0.SlotIndex == LotterySlot, ref failures);
            Check("parse phase1", LotteryItemUseRequest.TryParse(new byte[] { 0x01, 0x00, 0x6A, 0x00 }, out var phase1)
                && phase1.Phase == 1
                && phase1.SlotIndex == DoubleLotterySlot, ref failures);
            Check("reject short body", !LotteryItemUseRequest.TryParse(new byte[] { 0x01, 0x00, 0x6A }, out _), ref failures);
            Check("direct phase1 uses double while active and under cap",
                LotteryOpenPlanner.ResolveDirectFastOpen(true, true, PremiumService.LotteryDoubleRewardDailyLimit - 1).UseDoubleReward,
                ref failures);
            Check("direct phase1 falls back after double cap",
                LotteryOpenPlanner.ResolveDirectFastOpen(true, true, PremiumService.LotteryDoubleRewardDailyLimit).ShouldSendRegularPhaseStart,
                ref failures);
            Check("pending phase1 never consumes double count",
                !LotteryOpenPlanner.ResolveDirectFastOpen(false, true, 0).UseDoubleReward,
                ref failures);
            var doubleOpenPlan = LotteryOpenPlanner.ResolveDirectFastOpen(true, true, 0);
            var doubleOpenRequest = doubleOpenPlan.CreateBoosterUseRequest(DoubleLotterySlot);
            Check("double open plan creates multiplier request",
                doubleOpenRequest.SlotIndex == DoubleLotterySlot
                && doubleOpenRequest.RewardMultiplier == 2
                && doubleOpenRequest.ConsumeLotteryDoubleRewardUse,
                ref failures);
            var regularOpenPlan = LotteryOpenPlanner.ResolveDirectFastOpen(true, false, 0);
            var regularOpenRequest = regularOpenPlan.CreateBoosterUseRequest(DoubleLotterySlot);
            Check("regular fallback plan keeps normal reward request",
                regularOpenPlan.ShouldSendRegularPhaseStart
                && regularOpenRequest.RewardMultiplier == 1
                && !regularOpenRequest.ConsumeLotteryDoubleRewardUse,
                ref failures);

            var phaseStart = LotteryItemAckBuilder.BuildPhaseStart(LotterySlot, SampleBoosterItemId);
            Check("phase start ack length", phaseStart.Length == 13, ref failures);
            Check("phase start ack slot", BitConverter.ToInt16(phaseStart, 1) == LotterySlot, ref failures);
            Check("phase start ack preview item", BitConverter.ToInt32(phaseStart, 5) == SampleBoosterItemId, ref failures);
            Check("phase start ack mirrored preview item", BitConverter.ToInt32(phaseStart, 9) == SampleBoosterItemId, ref failures);
            var phaseStartWithoutPreview = LotteryItemAckBuilder.BuildPhaseStartWithoutPreview();
            Check("phase start without preview ack length", phaseStartWithoutPreview.Length == 13, ref failures);
            Check("phase start without preview ack slot", BitConverter.ToInt16(phaseStartWithoutPreview, 1) == -1, ref failures);
            Check("phase start without preview item", BitConverter.ToInt32(phaseStartWithoutPreview, 5) == 0, ref failures);
            Check("phase start without preview mirrored item", BitConverter.ToInt32(phaseStartWithoutPreview, 9) == 0, ref failures);
            Check("lottery result keeps gold refresh packet",
                InventoryHandler.ShouldSendLotteryGoldRefresh(new BoosterUseResult
                {
                    ConsumedGold = HeroLotteryGoldCost,
                    UpdatedGold = 123456,
                }),
                ref failures);
            Check("zero-cost result suppresses gold refresh packet",
                !InventoryHandler.ShouldSendLotteryGoldRefresh(new BoosterUseResult
                {
                    ConsumedGold = 0,
                    UpdatedGold = 123456,
                }),
                ref failures);
            var goldUpdate = ItemListUpdateBuilder.BuildGoldUpdate(123456);
            Check("gold refresh update list type", goldUpdate[0] == 0, ref failures);
            Check("gold refresh update count", BitConverter.ToUInt16(goldUpdate, 1) == 1, ref failures);
            Check("gold refresh update slot", BitConverter.ToInt16(goldUpdate, 3) == 0, ref failures);
            Check("gold refresh update item id", BitConverter.ToInt32(goldUpdate, 5) == 0, ref failures);
            Check("gold refresh update amount", BitConverter.ToInt32(goldUpdate, 9) == 123456, ref failures);

            var rewardItem = new CommonInventoryItem
            {
                SlotIndex = RewardSlot,
                ItemTemplateId = SampleRewardItemId,
                CountOrInstanceValue = 0x13572468,
                Durability = 100,
                ExtData0 = 7,
                PrefixData0E = new byte[] { 0, 0, 0, 0, 0, 3, 0x34, 0x12 },
            };
            var nativeResult = LotteryItemAckBuilder.BuildCommonItemResult(LotterySlot, rewardItem, 1);
            Check("native result ack carries socket extension and native tail", nativeResult.Length == 52, ref failures);
            Check("native result ack success", nativeResult[0] == 1, ref failures);
            Check("native result ack source slot", BitConverter.ToInt16(nativeResult, 1) == LotterySlot, ref failures);
            Check("native result ack reward slot", BitConverter.ToInt16(nativeResult, 3) == RewardSlot, ref failures);
            Check("native result ack reward item", BitConverter.ToInt32(nativeResult, 5) == SampleRewardItemId, ref failures);
            Check("native result ack display count", BitConverter.ToInt32(nativeResult, 9) == 1, ref failures);
            Check("native result ack durability", BitConverter.ToUInt16(nativeResult, 13) == 100, ref failures);
            Check("native result ack attr", nativeResult[15] == 7, ref failures);
            Check("native result ack amplify", nativeResult[16] == 3 && BitConverter.ToUInt16(nativeResult, 17) == 0x1234, ref failures);
            Check("native result empty socket extension marker", nativeResult[19] == 0xEF, ref failures);
            Check("native result empty socket extension length", BitConverter.ToInt32(nativeResult, 20) == 25, ref failures);
            Check("native result empty socket extension data", nativeResult.Skip(24).Take(25).All(x => x == 0), ref failures);
            Check("native result random option empty", nativeResult[49] == 0, ref failures);
            Check("native result upgrade separate flag", nativeResult[50] == 0, ref failures);
            Check("native result trade restriction flag", nativeResult[51] == 0, ref failures);
            Check("lottery equipment display value uses granted count",
                InventoryHandler.ResolveLotteryDisplayValue(
                    rewardItem,
                    new BoosterRewardResult
                    {
                        ListType = InventoryListType.Main,
                        SlotIndex = RewardSlot,
                        ItemTemplateId = SampleRewardItemId,
                        GrantedCount = 1,
                    }) == 1,
                ref failures);
            Check("lottery equipment display value aggregates double reward count",
                InventoryHandler.ResolveLotteryDisplayValue(
                    rewardItem,
                    new BoosterRewardResult
                    {
                        ListType = InventoryListType.Main,
                        SlotIndex = RewardSlot,
                        ItemTemplateId = SampleRewardItemId,
                        GrantedCount = 1,
                    },
                    new[]
                    {
                        new BoosterRewardResult
                        {
                            ListType = InventoryListType.Main,
                            SlotIndex = RewardSlot,
                            ItemTemplateId = SampleRewardItemId,
                            GrantedCount = 1,
                        },
                        new BoosterRewardResult
                        {
                            ListType = InventoryListType.Main,
                            SlotIndex = (short)(RewardSlot + 1),
                            ItemTemplateId = SampleRewardItemId,
                            GrantedCount = 1,
                        },
                    }) == 2,
                ref failures);
            Check("lottery stackable display value uses granted count",
                InventoryHandler.ResolveLotteryDisplayValue(
                    new CommonInventoryItem
                    {
                        SlotIndex = RewardSlot,
                        ItemTemplateId = SampleBoosterItemId,
                        CountOrInstanceValue = 99,
                    },
                    new BoosterRewardResult
                    {
                        ListType = InventoryListType.Main,
                        SlotIndex = RewardSlot,
                        ItemTemplateId = SampleBoosterItemId,
                        GrantedCount = 2,
                    }) == 2,
                ref failures);

            var duplicateEquipmentRewards = new[]
            {
                new BoosterRewardResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = RewardSlot,
                    ItemTemplateId = SampleRewardItemId,
                    GrantedCount = 1,
                },
                new BoosterRewardResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = (short)(RewardSlot + 1),
                    ItemTemplateId = SampleRewardItemId,
                    GrantedCount = 1,
                },
            };
            Check("duplicate equipment lottery notice is suppressed",
                InventoryHandler.ShouldSuppressLotteryItemNotice(duplicateEquipmentRewards[0], duplicateEquipmentRewards),
                ref failures);
            var duplicateRefreshRewards = InventoryHandler.ResolveLotteryMainRefreshRewards(duplicateEquipmentRewards);
            Check("duplicate equipment refresh keeps both slots",
                duplicateRefreshRewards.Count == 2
                && duplicateRefreshRewards[0].SlotIndex == RewardSlot
                && duplicateRefreshRewards[1].SlotIndex == RewardSlot + 1,
                ref failures);
            Check("double reward uses isolated double result flow",
                InventoryHandler.ShouldUseLotteryDoubleRewardResultFlow(true, duplicateEquipmentRewards),
                ref failures);
            Check("regular multi reward keeps native single result",
                !InventoryHandler.ShouldUseLotteryDoubleRewardResultFlow(false, duplicateEquipmentRewards),
                ref failures);
            Check("double reward native result carries x2 marker",
                InventoryHandler.ResolveLotteryNativeDisplayValue(1, true) == 2,
                ref failures);
            Check("regular reward native result preserves display value",
                InventoryHandler.ResolveLotteryNativeDisplayValue(1, false) == 1,
                ref failures);
            Check("empty double reward native result stays empty",
                InventoryHandler.ResolveLotteryNativeDisplayValue(0, true) == 0,
                ref failures);
            var avatarReward = new BoosterRewardResult
            {
                ListType = InventoryListType.Avatar,
                SlotIndex = 3,
                ItemTemplateId = CannedAvatarItemId,
                GrantedCount = 1,
            };
            var avatarDisplayRewards = InventoryHandler.ResolveLotteryDisplayRewards(new[] { avatarReward });
            var avatarSnapshot = new CharacterItemListSnapshot();
            avatarSnapshot.AvatarItems.Add(new AvatarInventoryItem
            {
                SlotIndex = avatarReward.SlotIndex,
                AvatarItemId = avatarReward.ItemTemplateId,
                UnknownFixed30 = 0x1E00,
                UnknownFixed4 = 4,
            });
            var avatarResultItem = InventoryHandler.ResolveLotteryAvatarResultItem(avatarSnapshot, avatarReward);
            var avatarNativeResult = LotteryItemAckBuilder.BuildAvatarItemResult(
                LotterySlot,
                avatarResultItem);
            Check("lottery display includes avatar reward",
                avatarDisplayRewards.Count == 1
                && avatarDisplayRewards[0].ListType == InventoryListType.Avatar,
                ref failures);
            Check("avatar reward cannot be resolved as a common result item",
                InventoryHandler.ResolveLotteryResultItem(avatarSnapshot, avatarReward) == null,
                ref failures);
            Check("avatar lottery result remains a success packet",
                avatarNativeResult.Length == 129
                && avatarNativeResult[0] == 1
                && BitConverter.ToInt16(avatarNativeResult, 3) == avatarReward.SlotIndex
                && BitConverter.ToInt32(avatarNativeResult, 5) == CannedAvatarItemId,
                ref failures);
            Check("avatar lottery result carries avatar entry fixed fields",
                BitConverter.ToInt32(avatarNativeResult, 86) == 0x1E00
                && BitConverter.ToUInt16(avatarNativeResult, 120) == 4,
                ref failures);
            Check("ordinary rare equipment is not lottery-announcement eligible",
                !InventoryHandler.IsLotteryItemNoticeEligible(ItemMetadataResolver.Resolve(NormalRareEquipmentItemId)),
                ref failures);
            Check("PVF legacy equipment is lottery-announcement eligible",
                InventoryHandler.IsLotteryItemNoticeEligible(ItemMetadataResolver.Resolve(LegacyEquipmentItemId)),
                ref failures);
            Check("epic equipment is lottery-announcement eligible",
                InventoryHandler.IsLotteryItemNoticeEligible(ItemMetadataResolver.Resolve(EpicEquipmentItemId)),
                ref failures);
            Check("regular single reward does not refresh display slot after native result",
                InventoryHandler.ResolveLotteryRegularPostResultRefreshRewards(
                    new[] { duplicateEquipmentRewards[0] }).Count == 0,
                ref failures);
            var doubleExtraRefreshRewards = InventoryHandler.ResolveLotteryDoubleRewardExtraRefreshRewards(duplicateEquipmentRewards);
            Check("double reward refreshes only extra slots",
                doubleExtraRefreshRewards.Count == 1
                && doubleExtraRefreshRewards[0].SlotIndex == RewardSlot + 1,
                ref failures);
            var avatarDisplayedMainRefreshRewards =
                InventoryHandler.ResolveLotteryPostResultMainRefreshRewards(
                    avatarReward,
                    new[] { duplicateEquipmentRewards[0] },
                    useDoubleRewardResultFlow: true);
            Check("avatar display refreshes every undisplayed main reward",
                avatarDisplayedMainRefreshRewards.Count == 1
                && avatarDisplayedMainRefreshRewards[0].SlotIndex == RewardSlot,
                ref failures);
            var mainDisplayedDoubleRefreshRewards =
                InventoryHandler.ResolveLotteryPostResultMainRefreshRewards(
                    duplicateEquipmentRewards[0],
                    duplicateEquipmentRewards,
                    useDoubleRewardResultFlow: true);
            Check("main display keeps double refresh limited to extra slots",
                mainDisplayedDoubleRefreshRewards.Count == 1
                && mainDisplayedDoubleRefreshRewards[0].SlotIndex == RewardSlot + 1,
                ref failures);
            var distinctEquipmentRewards = new[]
            {
                duplicateEquipmentRewards[0],
                new BoosterRewardResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = (short)(RewardSlot + 1),
                    ItemTemplateId = SampleRewardItemId + 1,
                    GrantedCount = 1,
                },
            };
            Check("distinct equipment lottery notice is preserved",
                !InventoryHandler.ShouldSuppressLotteryItemNotice(distinctEquipmentRewards[0], distinctEquipmentRewards),
                ref failures);
            var distinctRefreshRewards = InventoryHandler.ResolveLotteryMainRefreshRewards(distinctEquipmentRewards);
            Check("distinct equipment refresh skips display slot",
                distinctRefreshRewards.Count == 1 && distinctRefreshRewards[0].SlotIndex == RewardSlot + 1,
                ref failures);
            var regularMultiRefreshRewards = InventoryHandler.ResolveLotteryRegularPostResultRefreshRewards(
                distinctEquipmentRewards);
            Check("regular multi reward keeps existing additional-slot refresh policy",
                regularMultiRefreshRewards.Count == 1
                && regularMultiRefreshRewards[0].SlotIndex == RewardSlot + 1,
                ref failures);
            var notice = LotteryItemNoticeBuilder.Build(0x03EA, SampleRewardItemId, 7);
            Check("lottery notice length", notice.Length == 9, ref failures);
            Check("lottery notice kind", notice[0] == 2 && notice[1] == 1, ref failures);
            Check("lottery notice user unique id", BitConverter.ToUInt16(notice, 2) == 0x03EA, ref failures);
            Check("lottery notice item id", BitConverter.ToInt32(notice, 4) == SampleRewardItemId, ref failures);
            Check("lottery notice upgrade level", notice[8] == 7, ref failures);

            var lotteryBuffer = LotteryBufferBodyBuilder.BuildDisplaySnapshot(SampleRewardItemId);
            Check("lottery buffer length", lotteryBuffer.Length == 204, ref failures);
            Check("lottery buffer display item", BitConverter.ToInt32(lotteryBuffer, 12) == SampleRewardItemId, ref failures);
            Check("lottery buffer marker start", lotteryBuffer[16] == 1, ref failures);
            Check("lottery buffer marker end", lotteryBuffer[23] == 8, ref failures);
            var initSnapshot = new SelectCharacterDataSnapshot();
            initSnapshot.InitializationSnapshot.LotteryBufferBlob = lotteryBuffer;
            new LotteryBufferBodyBuilder().TryBuild(initSnapshot, 0, out var sanitizedLotteryBuffer);
            Check("legacy lottery buffer init snapshot is cleared", sanitizedLotteryBuffer.All(x => x == 0), ref failures);

            var overflowAck = OverflowInfoAckBuilder.Build(new byte[] { 0x01, 0x1B, 0x00 });
            Check("overflow ack echoes lottery command", overflowAck.Length == 3 && overflowAck[0] == 1 && overflowAck[1] == 0x1B && overflowAck[2] == 0, ref failures);
            Check("overflow confirm accepts only captured lottery body",
                InventoryHandler.IsLotteryOverflowConfirm(new byte[] { 0x01, 0x1B, 0x00 }), ref failures);
            Check("overflow confirm rejects another command",
                !InventoryHandler.IsLotteryOverflowConfirm(new byte[] { 0x01, 0x1C, 0x00 }), ref failures);
            Check("overflow confirm rejects prefixed non-lottery payload",
                !InventoryHandler.IsLotteryOverflowConfirm(new byte[] { 0x01, 0x1B, 0x00, 0x00 }), ref failures);

            var clearedSlot = ItemListUpdateBuilder.BuildCommonUpdates(new[] { ItemListUpdateBuilder.CreateClearedCommonSlot(LotterySlot) });
            Check("cleared source update uses empty slot sentinel", BitConverter.ToInt32(clearedSlot, 5) == -1, ref failures);
            Check("cleared source update clears count", BitConverter.ToInt32(clearedSlot, 9) == 0, ref failures);
            var compactClearedSlot = ItemListUpdateBuilder.BuildCompactCommonUpdates(new[]
            {
                new InventoryMutationResult
                {
                    SlotIndex = LotterySlot,
                    ItemTemplateId = SampleBoosterItemId,
                    RemainingStackCount = 0,
                },
            });
            Check("compact source update uses empty slot sentinel", BitConverter.ToInt32(compactClearedSlot, 5) == -1, ref failures);
            Check("compact source update clears count", BitConverter.ToInt32(compactClearedSlot, 9) == 0, ref failures);

            var purchasedLotteryRefresh = ItemListUpdateBuilder.BuildCommonUpdates(new[]
            {
                new CommonInventoryItem
                {
                    SlotIndex = LotterySlot,
                    ItemTemplateId = SampleUpgradableLegacyItemId,
                    CountOrInstanceValue = 7,
                }
            });
            Check("purchased lottery item refresh list type", purchasedLotteryRefresh[0] == 0, ref failures);
            Check("purchased lottery item refresh count", BitConverter.ToUInt16(purchasedLotteryRefresh, 1) == 1, ref failures);
            Check("purchased lottery item refresh slot", BitConverter.ToInt16(purchasedLotteryRefresh, 3) == LotterySlot, ref failures);
            Check("purchased lottery item refresh item", BitConverter.ToInt32(purchasedLotteryRefresh, 5) == SampleUpgradableLegacyItemId, ref failures);
            Check("purchased lottery item refresh stack", BitConverter.ToInt32(purchasedLotteryRefresh, 9) == 7, ref failures);

            Check("lottery source context keeps current stack count",
                InventoryHandler.ResolveLotterySourceContextCount(2) == 2, ref failures);
            Check("lottery source context clamps invalid count",
                InventoryHandler.ResolveLotterySourceContextCount(-1) == 0, ref failures);
            Check("lottery final result keeps source slot for native window",
                InventoryHandler.ResolveLotteryResultSourceSlot(new BoosterUseResult
                {
                    SourceSlotIndex = LotterySlot,
                    SourceItemTemplateId = SampleBoosterItemId,
                }) == LotterySlot, ref failures);

            var stackedBuyAck = BuyItemAckBuilder.Build(new InventoryMutationResult
            {
                SlotIndex = LotterySlot,
                ItemTemplateId = OrdinaryStackableItemId,
                RemainingStackCount = 52,
                InstanceValue = 1,
                UpdatedGold = 123456,
            });
            Check("NPC buy ACK stackable carries slot", BitConverter.ToInt16(stackedBuyAck, 17) == LotterySlot, ref failures);
            Check("NPC buy ACK ordinary stackable carries item", BitConverter.ToInt32(stackedBuyAck, 19) == OrdinaryStackableItemId, ref failures);
            Check("NPC buy ACK ordinary stackable carries buy delta", BitConverter.ToInt32(stackedBuyAck, 23) == 1, ref failures);
            Check("NPC buy normal stackable summary is not hidden",
                !InventoryHandler.ShouldHideNpcBuyItemSummary(OrdinaryStackableItemId, new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    ItemTemplateId = OrdinaryStackableItemId,
                }), ref failures);
            Check("NPC buy package summary is hidden for lottery-like items",
                InventoryHandler.ShouldHideNpcBuyItemSummary(SampleUpgradableLegacyItemId, new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    ItemTemplateId = SampleUpgradableLegacyItemId,
                }), ref failures);
            Check("NPC buy non-lottery booster summary is not hidden",
                !InventoryHandler.ShouldHideNpcBuyItemSummary(SampleBoosterItemId, new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    ItemTemplateId = SampleBoosterItemId,
                }), ref failures);
            var hiddenPackageBuyAck = BuyItemAckBuilder.Build(new InventoryMutationResult
            {
                SlotIndex = LotterySlot,
                ItemTemplateId = SampleUpgradableLegacyItemId,
                RemainingStackCount = 52,
                InstanceValue = 52,
                UpdatedGold = 123456,
            }, includePurchasedItemSummary: false);
            Check("NPC buy hidden ACK keeps body shape", hiddenPackageBuyAck.Length == stackedBuyAck.Length, ref failures);
            Check("NPC buy hidden ACK keeps item tab slot", BitConverter.ToInt16(hiddenPackageBuyAck, 17) == LotterySlot, ref failures);
            Check("NPC buy hidden ACK clears item", BitConverter.ToInt32(hiddenPackageBuyAck, 19) == 0, ref failures);
            Check("NPC buy hidden ACK clears stack", BitConverter.ToInt32(hiddenPackageBuyAck, 23) == 0, ref failures);
            Check("NPC buy hidden ACK still carries updated gold", BitConverter.ToInt32(hiddenPackageBuyAck, 1) == 123456, ref failures);
            var hiddenPackageBuyAckWithCost = BuyItemAckBuilder.Build(new InventoryMutationResult
            {
                SlotIndex = LotterySlot,
                ItemTemplateId = SampleUpgradableLegacyItemId,
                RemainingStackCount = 52,
                InstanceValue = 52,
                UpdatedGold = 123456,
            }, new[] { new CostItemUpdate { ItemTemplateId = 3314, NewStackCount = 7 } }.ToList(), includePurchasedItemSummary: false);
            Check("NPC buy hidden ACK keeps cost item count", hiddenPackageBuyAckWithCost[47] == 1, ref failures);
            Check("NPC buy hidden ACK keeps cost item id", BitConverter.ToInt32(hiddenPackageBuyAckWithCost, 48) == 3314, ref failures);
            Check("NPC buy hidden ACK keeps cost item stack", BitConverter.ToInt32(hiddenPackageBuyAckWithCost, 52) == 7, ref failures);

            var tempDb = Path.Combine(Path.GetTempPath(), "lottery_item_selftest.db");
            DeleteTempDatabase(tempDb);
            var dailyReset = new DailyResetService(tempDb, ServerPaths.SchemaFilePath);
            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath, dailyReset);
            Seed(tempDb);

            List<BoosterRewardResult> cannedAvatarResults = null;
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(tempDb)))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    Check("canned avatar reward inserts through booster inventory routing",
                        store._db.TryAddBoosterRewardItems(
                            connection,
                            transaction,
                            CharacterId,
                            AccountId,
                            CannedAvatarItemId,
                            1,
                            out cannedAvatarResults),
                        ref failures);
                    transaction.Commit();
                }
            }
            Check("canned avatar reward is stored in avatar inventory",
                cannedAvatarResults != null
                && cannedAvatarResults.Count == 1
                && cannedAvatarResults[0].ListType == InventoryListType.Avatar,
                ref failures);

            InventoryMutationResult ordinaryBuyResult = null;
            Check("ordinary stackable NPC buy succeeds", store.TryBuyItem(CharacterId, AccountId, OrdinaryStackableItemId, 2, out ordinaryBuyResult), ref failures);
            if (ordinaryBuyResult != null)
            {
                Check("ordinary stackable buy result keeps current stack", ordinaryBuyResult.RemainingStackCount == 7, ref failures);
                Check("ordinary stackable buy ACK value keeps buy delta", ordinaryBuyResult.InstanceValue == 2, ref failures);
                using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(tempDb)))
                {
                    conn.Open();
                    Check("ordinary stackable DB stack updated", LoadStackCount(conn, (short)(AncientHeroLotterySlot + 1), OrdinaryStackableItemId) == 7, ref failures);
                }
            }

            Check("hero lottery precheck rejects insufficient gold",
                !store.CanUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
                {
                    SlotIndex = HeroLotterySlot,
                    ExpectedItemTemplateId = SampleHeroLotteryItemId,
                    SelectedItemTemplateIds = Array.Empty<int>(),
                }), ref failures);

            SetGold(tempDb, HeroLotteryGoldCost);
            BoosterUseResult heroResult = null;
            Check("hero lottery precheck accepts gold without medal",
                store.CanUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
                {
                    SlotIndex = HeroLotterySlot,
                    ExpectedItemTemplateId = SampleHeroLotteryItemId,
                    SelectedItemTemplateIds = Array.Empty<int>(),
                }), ref failures);
            Check("hero lottery open succeeds with gold cost", store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
            {
                SlotIndex = HeroLotterySlot,
                ExpectedItemTemplateId = SampleHeroLotteryItemId,
                SelectedItemTemplateIds = Array.Empty<int>(),
            }, out heroResult), ref failures);

            if (heroResult != null)
            {
                Check("hero lottery consumes one source", heroResult.SourceRemainingStackCount == 1, ref failures);
                Check("hero lottery consumes gold", heroResult.ConsumedGold == HeroLotteryGoldCost && heroResult.UpdatedGold == 0, ref failures);
                Check("hero lottery does not consume medal", heroResult.ConsumedMaterialItemTemplateId == 0 && heroResult.ConsumedMaterialCount == 0, ref failures);
                Check("hero lottery grants reward", heroResult.Rewards.Count > 0, ref failures);
            }

            SetGold(tempDb, AncientHeroLotteryGoldCost);
            BoosterUseResult ancientHeroResult = null;
            Check("ancient hero lottery open succeeds with gold cost", store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
            {
                SlotIndex = AncientHeroLotterySlot,
                ExpectedItemTemplateId = SampleAncientHeroLotteryItemId,
                SelectedItemTemplateIds = Array.Empty<int>(),
            }, out ancientHeroResult), ref failures);

            if (ancientHeroResult != null)
            {
                Check("ancient hero lottery consumes one source", ancientHeroResult.SourceRemainingStackCount == 0, ref failures);
                Check("ancient hero lottery consumes 5m gold", ancientHeroResult.ConsumedGold == AncientHeroLotteryGoldCost && ancientHeroResult.UpdatedGold == 0, ref failures);
                Check("ancient hero lottery does not consume medal", ancientHeroResult.ConsumedMaterialItemTemplateId == 0 && ancientHeroResult.ConsumedMaterialCount == 0, ref failures);
                Check("ancient hero lottery grants reward", ancientHeroResult.Rewards.Count > 0, ref failures);
            }

            BoosterUseResult normalResult = null;
            Check("normal lottery open succeeds", store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
            {
                SlotIndex = LotterySlot,
                SelectedItemTemplateIds = Array.Empty<int>(),
            }, out normalResult), ref failures);

            if (normalResult != null)
            {
                Check("normal source consumed", normalResult.SourceRemainingStackCount == 0, ref failures);
                Check("normal reward granted", normalResult.Rewards.Any(x => x.ItemTemplateId == SampleRewardItemId), ref failures);
            }

            BoosterUseResult legacyResult = null;
            Check("upgradable legacy lottery open succeeds", store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
            {
                SlotIndex = UpgradableLegacySlot,
                SelectedItemTemplateIds = Array.Empty<int>(),
            }, out legacyResult), ref failures);

            if (legacyResult != null)
            {
                Check("upgradable legacy source consumed", legacyResult.SourceRemainingStackCount == 0, ref failures);
                Check("upgradable legacy grants one reward", legacyResult.Rewards.Count == 1, ref failures);
                Check("upgradable legacy reward is equipment-like id", legacyResult.Rewards[0].ItemTemplateId >= 100000000, ref failures);
            }

            BoosterUseResult doubleResult = null;
            Check("double lottery open succeeds", store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
            {
                SlotIndex = DoubleLotterySlot,
                SelectedItemTemplateIds = Array.Empty<int>(),
                RewardMultiplier = 2,
                ConsumeLotteryDoubleRewardUse = true,
            }, out doubleResult), ref failures);

            if (doubleResult != null)
            {
                Check("double source consumes one pot", doubleResult.SourceRemainingStackCount == 1, ref failures);
                Check("double reward doubles magic boxes",
                    doubleResult.Rewards.Where(x => x.ItemTemplateId == DoubleRewardMagicBoxItemId)
                        .Sum(x => Math.Max(1, x.GrantedCount)) == DoubleRewardPerItemCount * 2,
                    ref failures);
                Check("double reward doubles magic hammers",
                    doubleResult.Rewards.Where(x => x.ItemTemplateId == DoubleRewardMagicHammerItemId)
                        .Sum(x => Math.Max(1, x.GrantedCount)) == DoubleRewardPerItemCount * 2,
                    ref failures);
            }

            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(tempDb)))
            {
                conn.Open();
                Check("hero source stack decremented to one", LoadStackCount(conn, HeroLotterySlot, SampleHeroLotteryItemId) == 1, ref failures);
                Check("ancient hero source consumed", LoadStackCount(conn, AncientHeroLotterySlot, SampleAncientHeroLotteryItemId) == -1, ref failures);
                Check("hero lottery gold deducted", LoadStackCount(conn, 0, 0) == 0, ref failures);
                Check("double source stack decremented to one", LoadStackCount(conn, DoubleLotterySlot, DoubleRewardBoosterItemId) == 1, ref failures);
                using (var tx = conn.BeginTransaction())
                {
                    Check("premium use count persisted in daily reset counter",
                        PremiumService.GetLotteryDoubleRewardUsedCount(dailyReset, conn, tx, CharacterId) == 1,
                        ref failures);
                    tx.Commit();
                }
            }

            var connStr = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
            var serviceData = PremiumService.BuildPremiumServiceData(connStr, AccountId, CharacterId, dailyReset);
            Check("premium service data length", serviceData.Length == 74, ref failures);
            Check("premium service lottery used count offset",
                BitConverter.ToInt32(serviceData, 10 + PremiumService.LotteryDoubleRewardServiceIndex * 9)
                == 1,
                ref failures);

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var accepted = 0;
                    for (var i = 1; i < PremiumService.LotteryDoubleRewardDailyLimit; i++)
                    {
                        if (PremiumService.TryConsumeLotteryDoubleRewardUse(conn, tx, dailyReset, CharacterId, AccountId))
                            accepted++;
                    }

                    Check("premium daily counter rejects above cap",
                        accepted == PremiumService.LotteryDoubleRewardDailyLimit - 1
                        && !PremiumService.TryConsumeLotteryDoubleRewardUse(conn, tx, dailyReset, CharacterId, AccountId),
                        ref failures);
                    tx.Commit();
                }
            }

            serviceData = PremiumService.BuildPremiumServiceData(connStr, AccountId, CharacterId, dailyReset);
            Check("premium service lottery used count reaches cap",
                BitConverter.ToInt32(serviceData, 10 + PremiumService.LotteryDoubleRewardServiceIndex * 9) == PremiumService.LotteryDoubleRewardDailyLimit,
                ref failures);

            BoosterUseResult cappedNormalResult = null;
            Check("normal fast lottery still opens after double cap", store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
            {
                SlotIndex = DoubleLotterySlot,
                SelectedItemTemplateIds = Array.Empty<int>(),
            }, out cappedNormalResult), ref failures);
            if (cappedNormalResult != null)
            {
                Check("normal fast lottery after cap consumes one source", cappedNormalResult.SourceRemainingStackCount == 0, ref failures);
                Check("normal fast lottery after cap grants single reward",
                    cappedNormalResult.Rewards.Where(x => x.ItemTemplateId == DoubleRewardMagicBoxItemId)
                        .Sum(x => Math.Max(1, x.GrantedCount)) == DoubleRewardPerItemCount
                    && cappedNormalResult.Rewards.Where(x => x.ItemTemplateId == DoubleRewardMagicHammerItemId)
                        .Sum(x => Math.Max(1, x.GrantedCount)) == DoubleRewardPerItemCount,
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Seed(string databasePath)
        {
            var connStr = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connStr))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'lottery-item-selftest', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'lottery-item-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 0, 24);

INSERT OR REPLACE INTO account_premiums (account_id, premium_type, end_time)
VALUES (@accountId, @premiumType, @endTime);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES
    ('character', @characterId, @characterId, 0, 0, 0, 'special',
     @initialGold, @initialGold, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @lotterySlot, @lotteryItemId, 'stackable',
     1, 1, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @upgradableLegacySlot, @upgradableLegacyItemId, 'stackable',
     1, 1, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @heroLotterySlot, @heroLotteryItemId, 'stackable',
     2, 2, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @ancientHeroLotterySlot, @ancientHeroLotteryItemId, 'stackable',
     1, 1, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @ordinaryStackableSlot, @ordinaryStackableItemId, 'stackable',
     5, 5, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @doubleLotterySlot, @doubleLotteryItemId, 'stackable',
     2, 2, 0, 0, 0, 0, 0, 0, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@premiumType", DevilContractCatalog.SlotToPremiumType(PremiumService.LotteryDoubleRewardServiceIndex));
                    command.Parameters.AddWithValue("@endTime", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400);
                    command.Parameters.AddWithValue("@lotterySlot", LotterySlot);
                    command.Parameters.AddWithValue("@doubleLotterySlot", DoubleLotterySlot);
                    command.Parameters.AddWithValue("@upgradableLegacySlot", UpgradableLegacySlot);
                    command.Parameters.AddWithValue("@heroLotterySlot", HeroLotterySlot);
                    command.Parameters.AddWithValue("@ancientHeroLotterySlot", AncientHeroLotterySlot);
                    command.Parameters.AddWithValue("@ordinaryStackableSlot", AncientHeroLotterySlot + 1);
                    command.Parameters.AddWithValue("@upgradableLegacyItemId", SampleUpgradableLegacyItemId);
                    command.Parameters.AddWithValue("@heroLotteryItemId", SampleHeroLotteryItemId);
                    command.Parameters.AddWithValue("@ancientHeroLotteryItemId", SampleAncientHeroLotteryItemId);
                    command.Parameters.AddWithValue("@ordinaryStackableItemId", OrdinaryStackableItemId);
                    command.Parameters.AddWithValue("@lotteryItemId", SampleBoosterItemId);
                    command.Parameters.AddWithValue("@doubleLotteryItemId", DoubleRewardBoosterItemId);
                    command.Parameters.AddWithValue("@initialGold", HeroLotteryGoldCost - 1);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SetGold(string databasePath, int gold)
        {
            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(databasePath);
            using (var connection = new SqliteConnection(connStr))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE character_items
SET stack_count=@gold, instance_value=@gold
WHERE character_id=@characterId AND list_type=0 AND slot_index=0;";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@gold", gold);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int LoadStackCount(SqliteConnection connection, short slotIndex, int itemTemplateId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT stack_count
FROM character_items
WHERE character_id=@characterId AND list_type=0 AND slot_index=@slotIndex AND item_template_id=@itemTemplateId;";
                command.Parameters.AddWithValue("@characterId", CharacterId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                command.Parameters.AddWithValue("@itemTemplateId", itemTemplateId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? -1 : Convert.ToInt32(value);
            }
        }

        private static void DeleteTempDatabase(string path)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file))
                    File.Delete(file);
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
