using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.Lottery;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Lottery;
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
        private const int SampleLotteryItemId = 10014964;
        private const int SampleRewardItemId = 400360011;
        private const int MagicBoxItemId = 10007368;
        private const int HeroLotteryItemId = 8095;
        private const int AncientHeroLotteryItemId = 8213;
        private const int HeroLotteryGoldCost = 40000000;
        private const int AncientHeroLotteryGoldCost = 5000000;
        private const int CannedAvatarItemId = 39075;
        private const int LegacyEquipmentItemId = 100150516;
        private const int EpicEquipmentItemId = 101000004;

        public static int Run()
        {
            Console.WriteLine("=== LOTTERY_ITEM selftest ===");
            var failures = 0;

            TestProtocolAndPresentation(ref failures);
            TestDefinitionAndSession(ref failures);
            TestIndependentService(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void TestProtocolAndPresentation(ref int failures)
        {
            Check("parse phase0", LotteryItemUseRequest.TryParse(
                new byte[] { 0x00, 0x00, 0x69, 0x00 }, out var phase0)
                && phase0.Phase == 0
                && phase0.SlotIndex == LotterySlot, ref failures);
            Check("parse phase1", LotteryItemUseRequest.TryParse(
                new byte[] { 0x01, 0x00, 0x6A, 0x00 }, out var phase1)
                && phase1.Phase == 1
                && phase1.SlotIndex == DoubleLotterySlot, ref failures);
            Check("reject short body", !LotteryItemUseRequest.TryParse(
                new byte[] { 0x01, 0x00, 0x6A }, out _), ref failures);
            Check("reject unknown phase", !LotteryItemUseRequest.TryParse(
                new byte[] { 0x02, 0x00, 0x6A, 0x00 }, out _), ref failures);
            Check("exact lottery overflow confirm", LotteryItemHandler.IsLotteryOverflowConfirm(
                new byte[] { 0x01, 0x1B, 0x00 }), ref failures);
            Check("reject unrelated overflow confirm", !LotteryItemHandler.IsLotteryOverflowConfirm(
                new byte[] { 0x01, 0x1A, 0x00 }), ref failures);

            var phaseStart = LotteryItemAckBuilder.BuildPhaseStartWithoutPreview();
            Check("phase start body length", phaseStart.Length == 13, ref failures);
            Check("phase start hides source slot", BitConverter.ToInt16(phaseStart, 1) == -1, ref failures);
            Check("phase start hides preview", BitConverter.ToInt32(phaseStart, 5) == 0
                && BitConverter.ToInt32(phaseStart, 9) == 0, ref failures);

            var rewardItem = new CommonInventoryItem
            {
                SlotIndex = RewardSlot,
                ItemTemplateId = SampleRewardItemId,
                CountOrInstanceValue = 0x13572468,
                Durability = 100,
                ExtData0 = 7,
                PrefixData0E = new byte[] { 0, 0, 0, 0, 0, 3, 0x34, 0x12 },
            };
            var nativeResult = LotteryItemAckBuilder.BuildCommonItemResult(
                LotterySlot,
                rewardItem,
                2);
            Check("common result body length", nativeResult.Length == 52, ref failures);
            Check("common result source and reward", nativeResult[0] == 1
                && BitConverter.ToInt16(nativeResult, 1) == LotterySlot
                && BitConverter.ToInt16(nativeResult, 3) == RewardSlot
                && BitConverter.ToInt32(nativeResult, 5) == SampleRewardItemId, ref failures);
            Check("common result x2 display", BitConverter.ToInt32(nativeResult, 9) == 2, ref failures);
            Check("common result native tail", nativeResult[19] == 0xEF
                && BitConverter.ToInt32(nativeResult, 20) == 25
                && nativeResult.Skip(24).Take(25).All(value => value == 0)
                && nativeResult.Skip(49).All(value => value == 0), ref failures);

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
                    SlotIndex = RewardSlot + 1,
                    ItemTemplateId = SampleRewardItemId,
                    GrantedCount = 1,
                },
            };
            Check("double presentation aggregates x2", LotteryPresentationPolicy.ResolveDisplayValue(
                rewardItem,
                duplicateEquipmentRewards[0],
                duplicateEquipmentRewards) == 2, ref failures);
            Check("double result flow is isolated", LotteryPresentationPolicy.ShouldUseDoubleRewardResultFlow(
                true,
                duplicateEquipmentRewards), ref failures);
            Check("regular multi reward remains regular", !LotteryPresentationPolicy.ShouldUseDoubleRewardResultFlow(
                false,
                duplicateEquipmentRewards), ref failures);
            Check("duplicate equipment refresh keeps both rows", LotteryPresentationPolicy.ResolveMainRefreshRewards(
                duplicateEquipmentRewards).Count == 2, ref failures);
            Check("regular duplicate notice is suppressed", LotteryPresentationPolicy.ShouldSuppressNotice(
                duplicateEquipmentRewards[0],
                duplicateEquipmentRewards), ref failures);

            var avatarReward = new BoosterRewardResult
            {
                ListType = InventoryListType.Avatar,
                SlotIndex = 3,
                ItemTemplateId = CannedAvatarItemId,
                GrantedCount = 1,
            };
            var avatarSnapshot = new CharacterItemListSnapshot();
            avatarSnapshot.AvatarItems.Add(new AvatarInventoryItem
            {
                SlotIndex = avatarReward.SlotIndex,
                AvatarItemId = avatarReward.ItemTemplateId,
                UnknownFixed30 = 0x1E00,
                UnknownFixed4 = 4,
            });
            var avatarBody = LotteryItemAckBuilder.BuildAvatarItemResult(
                LotterySlot,
                LotteryPresentationPolicy.ResolveAvatarResultItem(avatarSnapshot, avatarReward));
            Check("avatar result body length", avatarBody.Length == 129, ref failures);
            Check("avatar result success", avatarBody[0] == 1
                && BitConverter.ToInt16(avatarBody, 1) == LotterySlot, ref failures);

            Check("legacy reward announcement eligible", LotteryPresentationPolicy.IsNoticeEligible(
                ItemMetadataResolver.Resolve(LegacyEquipmentItemId)), ref failures);
            Check("epic reward announcement eligible", LotteryPresentationPolicy.IsNoticeEligible(
                ItemMetadataResolver.Resolve(EpicEquipmentItemId)), ref failures);
            Check("stackable reward announcement excluded", !LotteryPresentationPolicy.IsNoticeEligible(
                ItemMetadataResolver.Resolve(SampleLotteryItemId)), ref failures);

            var goldUpdate = ItemListUpdateBuilder.BuildGoldUpdate(123456);
            Check("gold refresh payload", goldUpdate[0] == 0
                && BitConverter.ToUInt16(goldUpdate, 1) == 1
                && BitConverter.ToInt16(goldUpdate, 3) == 0
                && BitConverter.ToInt32(goldUpdate, 9) == 123456, ref failures);
        }

        private static void TestDefinitionAndSession(ref int failures)
        {
            var definitions = new LotteryItemDefinitionProvider();
            Check("PVF ordinary lottery definition", definitions.TryGet(
                SampleLotteryItemId,
                out var ordinaryDefinition)
                && ordinaryDefinition.RewardPool.Count > 0, ref failures);
            Check("PVF magic box is not a lottery", !definitions.TryGet(
                MagicBoxItemId,
                out _), ref failures);
            Check("hero lottery gold cost comes from PVF", definitions.TryGet(
                HeroLotteryItemId,
                out var heroDefinition)
                && heroDefinition.GoldCost == HeroLotteryGoldCost, ref failures);
            Check("ancient hero lottery gold cost comes from PVF", definitions.TryGet(
                AncientHeroLotteryItemId,
                out var ancientDefinition)
                && ancientDefinition.GoldCost == AncientHeroLotteryGoldCost, ref failures);
            Check("ordinary stackable is not a lottery", !definitions.TryGet(2600014, out _), ref failures);

            Check("direct fast open uses double below cap", LotteryOpenPlanner.ResolveDirectFastOpen(
                true,
                true,
                LotteryDoubleRewardPolicy.DailyLimit - 1).UseDoubleReward, ref failures);
            Check("direct fast open falls back at cap", LotteryOpenPlanner.ResolveDirectFastOpen(
                true,
                true,
                LotteryDoubleRewardPolicy.DailyLimit).ShouldSendRegularPhaseStart, ref failures);
            Check("confirmed open never consumes double", !LotteryOpenPlanner.ResolveDirectFastOpen(
                false,
                true,
                0).UseDoubleReward, ref failures);

            var now = new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc);
            var sessions = new LotteryOpenSessionCoordinator(
                TimeSpan.FromMinutes(2),
                () => now);
            var sessionId = Guid.NewGuid();
            sessions.Set(sessionId, LotterySlot, LotteryOpenPlan.DirectDoubleReward(0));
            Check("pending open keeps slot and plan", sessions.TryTake(
                sessionId,
                LotterySlot,
                out var pending)
                && pending.OpenPlan.UseDoubleReward, ref failures);
            sessions.Set(sessionId, LotterySlot);
            now = now.AddMinutes(3);
            Check("pending open expires", !sessions.TryTake(sessionId, null, out _), ref failures);
        }

        private static void TestIndependentService(ref int failures)
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "lottery-item-service-selftest.db");
            DeleteTempDatabase(databasePath);
            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            Seed(databasePath);

            var dailyReset = new DailyResetService(databasePath, ServerPaths.SchemaFilePath);
            var inventoryStore = new SqliteInventoryStore(databasePath, ServerPaths.SchemaFilePath);
            var doublePolicy = new LotteryDoubleRewardPolicy(dailyReset, connectionString);
            var service = new LotteryItemOpenService(
                inventoryStore,
                new LotteryItemDefinitionProvider(),
                doublePolicy);
            var planner = new LotteryOpenPlanner(doublePolicy);

            Check("generic booster path rejects lottery type", !inventoryStore.TryUseBoosterItem(
                CharacterId,
                AccountId,
                new BoosterUseRequest
                {
                    SlotIndex = LotterySlot,
                    SelectedItemTemplateIds = Array.Empty<int>(),
                },
                out _), ref failures);
            Check("generic rejection does not consume pot", LoadStackCount(
                connectionString,
                LotterySlot,
                SampleLotteryItemId) == 1, ref failures);

            Check("normal lottery precheck", service.CanOpen(
                CharacterId,
                LotterySlot,
                out var source)
                && source.ItemTemplateId == SampleLotteryItemId, ref failures);
            Check("normal lottery opens through dedicated service", service.TryOpen(
                CharacterId,
                AccountId,
                LotterySlot,
                false,
                out var normalResult), ref failures);
            Check("normal lottery consumes one and grants reward", normalResult != null
                && normalResult.SourceRemainingStackCount == 0
                && normalResult.Rewards.Count > 0
                && !normalResult.UsedDoubleReward, ref failures);

            Check("upgradable legacy opens through dedicated service", service.TryOpen(
                CharacterId,
                AccountId,
                UpgradableLegacySlot,
                false,
                out var legacyResult)
                && legacyResult.Rewards.Count > 0, ref failures);

            Check("hero pot rejects insufficient gold", !service.CanOpen(
                CharacterId,
                HeroLotterySlot,
                out _), ref failures);
            SetGold(connectionString, HeroLotteryGoldCost);
            Check("hero pot accepts exact PVF gold cost", service.CanOpen(
                CharacterId,
                HeroLotterySlot,
                out _), ref failures);
            Check("hero pot deducts gold without exchange material", service.TryOpen(
                CharacterId,
                AccountId,
                HeroLotterySlot,
                false,
                out var heroResult)
                && heroResult.ConsumedGold == HeroLotteryGoldCost
                && heroResult.UpdatedGold == 0, ref failures);

            SetGold(connectionString, AncientHeroLotteryGoldCost);
            Check("ancient hero pot deducts PVF gold cost", service.TryOpen(
                CharacterId,
                AccountId,
                AncientHeroLotterySlot,
                false,
                out var ancientResult)
                && ancientResult.ConsumedGold == AncientHeroLotteryGoldCost
                && ancientResult.UpdatedGold == 0, ref failures);

            var firstDoublePlan = planner.Resolve(CharacterId, AccountId, true);
            Check("active contract plans double open", firstDoublePlan.UseDoubleReward, ref failures);
            Check("double open grants two result units", service.TryOpen(
                CharacterId,
                AccountId,
                DoubleLotterySlot,
                firstDoublePlan.UseDoubleReward,
                out var doubleResult)
                && doubleResult.UsedDoubleReward
                && doubleResult.Rewards.Sum(reward => Math.Max(1, reward.GrantedCount)) == 2, ref failures);
            Check("double open consumes one daily use", doublePolicy.GetUsedCount(CharacterId) == 1, ref failures);

            for (var index = 1; index < LotteryDoubleRewardPolicy.DailyLimit; index++)
            {
                var plan = planner.Resolve(CharacterId, AccountId, true);
                Check($"double plan remains active #{index + 1}", plan.UseDoubleReward, ref failures);
                Check($"double open succeeds #{index + 1}", service.TryOpen(
                    CharacterId,
                    AccountId,
                    DoubleLotterySlot,
                    plan.UseDoubleReward,
                    out _), ref failures);
            }

            Check("daily double count reaches cap", doublePolicy.GetUsedCount(CharacterId)
                == LotteryDoubleRewardPolicy.DailyLimit, ref failures);
            var remainingBeforeRejectedDouble = LoadStackCount(
                connectionString,
                DoubleLotterySlot,
                SampleLotteryItemId);
            Check("stale double plan above cap falls back atomically", service.TryOpen(
                CharacterId,
                AccountId,
                DoubleLotterySlot,
                true,
                out var staleDoubleResult)
                && staleDoubleResult != null
                && !staleDoubleResult.UsedDoubleReward
                && staleDoubleResult.Rewards.Sum(reward => Math.Max(1, reward.GrantedCount)) == 1
                && LoadStackCount(connectionString, DoubleLotterySlot, SampleLotteryItemId)
                    == remainingBeforeRejectedDouble - 1
                && doublePolicy.GetUsedCount(CharacterId) == LotteryDoubleRewardPolicy.DailyLimit,
                ref failures);

            var cappedPlan = planner.Resolve(CharacterId, AccountId, true);
            Check("planner falls back to regular phase after cap", cappedPlan.ShouldSendRegularPhaseStart
                && !cappedPlan.UseDoubleReward, ref failures);
            Check("regular open still succeeds after cap", service.TryOpen(
                CharacterId,
                AccountId,
                DoubleLotterySlot,
                false,
                out var cappedRegularResult)
                && cappedRegularResult.Rewards.Sum(reward => Math.Max(1, reward.GrantedCount)) == 1, ref failures);

            var serviceData = PremiumService.BuildPremiumServiceData(
                connectionString,
                AccountId,
                doublePolicy.BuildPremiumServiceUsage(CharacterId));
            Check("premium payload is unchanged length", serviceData.Length == 74, ref failures);
            Check("premium payload carries lottery slot usage", BitConverter.ToInt32(
                serviceData,
                10 + LotteryDoubleRewardPolicy.PremiumServiceSlot * 9)
                == LotteryDoubleRewardPolicy.DailyLimit, ref failures);

            Check("NPC purchase workaround is scoped to lottery", InventoryHandler.ShouldHideNpcBuyItemSummary(
                HeroLotteryItemId,
                new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    ItemTemplateId = HeroLotteryItemId,
                }), ref failures);
            Check("NPC purchase workaround excludes ordinary stackable", !InventoryHandler.ShouldHideNpcBuyItemSummary(
                2600014,
                new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    ItemTemplateId = 2600014,
                }), ref failures);

            DeleteTempDatabase(databasePath);
        }

        private static void Seed(string databasePath)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'lottery-item-service-selftest', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'lottery-item-service-selftest');

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
     0, 0, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @lotterySlot, @lotteryItemId, 'stackable',
     1, 1, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @doubleLotterySlot, @lotteryItemId, 'stackable',
     12, 12, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @legacySlot, @legacyItemId, 'stackable',
     1, 1, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @heroSlot, @heroItemId, 'stackable',
     1, 1, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @ancientSlot, @ancientItemId, 'stackable',
     1, 1, 0, 0, 0, 0, 0, 0, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue(
                        "@premiumType",
                        DevilContractCatalog.SlotToPremiumType(
                            LotteryDoubleRewardPolicy.PremiumServiceSlot));
                    command.Parameters.AddWithValue(
                        "@endTime",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400);
                    command.Parameters.AddWithValue("@lotterySlot", LotterySlot);
                    command.Parameters.AddWithValue("@doubleLotterySlot", DoubleLotterySlot);
                    command.Parameters.AddWithValue("@legacySlot", UpgradableLegacySlot);
                    command.Parameters.AddWithValue("@heroSlot", HeroLotterySlot);
                    command.Parameters.AddWithValue("@ancientSlot", AncientHeroLotterySlot);
                    command.Parameters.AddWithValue("@lotteryItemId", SampleLotteryItemId);
                    command.Parameters.AddWithValue("@legacyItemId", SampleLotteryItemId);
                    command.Parameters.AddWithValue("@heroItemId", HeroLotteryItemId);
                    command.Parameters.AddWithValue("@ancientItemId", AncientHeroLotteryItemId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SetGold(string connectionString, int gold)
        {
            using (var connection = new SqliteConnection(connectionString))
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

        private static int LoadStackCount(
            string connectionString,
            short slotIndex,
            int itemTemplateId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT stack_count
FROM character_items
WHERE character_id=@characterId
  AND list_type=0
  AND slot_index=@slotIndex
  AND item_template_id=@itemTemplateId;";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slotIndex", slotIndex);
                    command.Parameters.AddWithValue("@itemTemplateId", itemTemplateId);
                    var value = command.ExecuteScalar();
                    return value == null || value == DBNull.Value
                        ? -1
                        : Convert.ToInt32(value);
                }
            }
        }

        private static void DeleteTempDatabase(string path)
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
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
