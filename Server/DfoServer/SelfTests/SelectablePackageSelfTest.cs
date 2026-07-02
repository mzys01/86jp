using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.CeraShop;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Inventory;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Globalization;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class SelectablePackageSelfTest
    {
        private const int AccountId = 1;
        private const int CharacterId = 999208;
        private const short PackageSlot = 66;
        private const short StackedPackageSlot = 67;
        private const short AvatarPackageSlot = 68;
        private const short CrossJobAuraPackageSlot = 69;
        private const short SpecialBoosterPackageSlot = 70;
        private const short MagicHammerBundleSlot = 71;
        private const short WrongMagicHammerSlot = 80;
        private const short MagicBoxSlot = 106;
        private const short MagicHammerSlot = 173;
        private const short RelocatedSeriaLuckSlot = 112;
        private const short LegacyNoExpireTimedRewardSlot = 119;
        // These are PVF sample IDs used only to exercise generic package paths.
        // Production logic resolves package/reward data from PVF, not from these constants.
        private const int SampleTitleSelectablePackageId = 10007993;
        private const int SampleAvatarSelectablePackageId = 10008359;
        private const int SampleAuraSelectablePackageId = 10008357;
        private const int SampleSpecialKindBoosterPackageId = 10007997;
        private const int SampleSpecialKindBoosterRewardId = 400360011;
        private const int SampleMagicHammerBundleProductId = 102930;
        private const int SampleMagicHammerBundleId = 10007477;
        private const int MagicBoxItemTemplateId = 10007368;
        private const int MagicHammerItemTemplateId = 10007367;
        private const int InstantTeleportPotionItemTemplateId = 2600014;
        private const int SeriaLuckItemTemplateId = 2682272;
        private const int MagicBoxBatchCount = 100;
        private const int SeriaLuckBatchCount = 10;
        private const int SampleSelectedTitleRewardId = 400330051;
        private const int SampleCrossJobAuraRewardId = 112590011;
        private const int InvalidRewardItemTemplateId = 1;

        private static readonly byte[] CapturedOpenRequestBody =
        {
            0x42, 0x00, 0x00, 0x00,
            0x43, 0x8D, 0xDC, 0x17,
            0x00,
        };

        private static readonly byte[] CapturedContextOpenRequestBody =
        {
            0x43, 0x00, 0x0A, 0x00,
            0xF2, 0xE1, 0xA6, 0x18,
            0x00,
        };

        private static readonly byte[] CapturedAvatarOpenRequestBody =
        {
            0x44, 0x00, 0x00, 0x00,
            0x27, 0x8A, 0x0D, 0x06,
            0xCE, 0xB1, 0x0D, 0x06,
            0xE4, 0xD7, 0x0D, 0x06,
            0xDF, 0x14, 0x0D, 0x06,
            0x8C, 0xC7, 0x0C, 0x06,
            0x3A, 0xEF, 0x0C, 0x06,
            0xBE, 0x3B, 0x0D, 0x06,
            0x71, 0x63, 0x0D, 0x06,
            0x08,
            0x27, 0x8A, 0x0D, 0x06, 0x01,
            0xCE, 0xB1, 0x0D, 0x06, 0x01,
            0xE4, 0xD7, 0x0D, 0x06, 0x02,
            0xDF, 0x14, 0x0D, 0x06, 0x02,
            0x8C, 0xC7, 0x0C, 0x06, 0x09,
            0x3A, 0xEF, 0x0C, 0x06, 0x00,
            0xBE, 0x3B, 0x0D, 0x06, 0x04,
            0x71, 0x63, 0x0D, 0x06, 0x00,
        };

        private static readonly int[] CapturedAvatarItemTemplateIds =
        {
            101550631,
            101560782,
            101570532,
            101520607,
            101500812,
            101510970,
            101530558,
            101540721,
        };

        private static readonly byte[] CapturedContextAvatarOpenRequestBody =
        {
            0x42, 0x00, 0x02, 0x00,
            0x54, 0xCC, 0x1C, 0x06,
            0xFC, 0xF3, 0x1C, 0x06,
            0x46, 0x1A, 0x1D, 0x06,
            0x03, 0x57, 0x1C, 0x06,
            0xCF, 0x09, 0x1C, 0x06,
            0xD6, 0x30, 0x1C, 0x06,
            0xE5, 0x7D, 0x1C, 0x06,
            0xBB, 0xA5, 0x1C, 0x06,
            0x08,
            0x54, 0xCC, 0x1C, 0x06, 0x00,
            0xFC, 0xF3, 0x1C, 0x06, 0x00,
            0x46, 0x1A, 0x1D, 0x06, 0x02,
            0x03, 0x57, 0x1C, 0x06, 0x02,
            0xCF, 0x09, 0x1C, 0x06, 0x12,
            0xD6, 0x30, 0x1C, 0x06, 0x01,
            0xE5, 0x7D, 0x1C, 0x06, 0x04,
            0xBB, 0xA5, 0x1C, 0x06, 0x02,
        };

        private static readonly byte[] CapturedMagicBoxOpenRequestBody =
        {
            0x00,
            0x6A, 0x00,
            0x48, 0xB3, 0x98, 0x00,
            0xAD, 0x00,
            0x47, 0xB3, 0x98, 0x00,
            0x64, 0x00,
        };

        private static readonly byte[] CapturedSeriaLuckOpenRequestBody =
        {
            0x04,
            0x56, 0x00,
            0xA0, 0xED, 0x28, 0x00,
            0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF,
            0x0A, 0x00,
        };

        private static readonly byte[] CapturedSeriaLuckSingleOpenRequestBody =
        {
            0x04,
            0x5C, 0x00,
            0xFF, 0xFF,
        };

        private static readonly int[] CapturedContextAvatarItemTemplateIds =
        {
            102550612,
            102560764,
            102570566,
            102520579,
            102500815,
            102510806,
            102530533,
            102540731,
        };

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== SELECTABLE_PACKAGE selftest ===");

            Check("parse captured 0x00A0 request", SelectablePackageOpenRequest.TryParse(CapturedOpenRequestBody, out var request));
            if (request == null)
            {
                PrintSummary();
                return 1;
            }

            Check($"request slot={request.SlotIndex}", request.SlotIndex == PackageSlot);
            Check($"request context={request.SelectionContext}", request.SelectionContext == 0);
            Check($"request selected item={request.SelectedItemTemplateId}", request.SelectedItemTemplateId == SampleSelectedTitleRewardId);
            Check($"request selection flag={request.SelectionFlag}", request.SelectionFlag == 0);
            Check("single request has no avatar choices", !request.HasAvatarChoices);

            Check("parse captured 0x00A0 request with nonzero context", SelectablePackageOpenRequest.TryParse(CapturedContextOpenRequestBody, out var contextRequest));
            if (contextRequest != null)
            {
                Check($"context request slot={contextRequest.SlotIndex}", contextRequest.SlotIndex == StackedPackageSlot);
                Check($"context request context={contextRequest.SelectionContext}", contextRequest.SelectionContext == 10);
                Check($"context request selected item=0x{contextRequest.SelectedItemTemplateId:X8}", contextRequest.SelectedItemTemplateId == 0x18A6E1F2);
                Check("context request has no avatar choices", !contextRequest.HasAvatarChoices);
            }

            Check("parse captured 0x00A0 avatar-choice request", SelectablePackageOpenRequest.TryParse(CapturedAvatarOpenRequestBody, out var avatarRequest));
            if (avatarRequest != null)
            {
                Check($"avatar request slot={avatarRequest.SlotIndex}", avatarRequest.SlotIndex == AvatarPackageSlot);
                Check($"avatar request context={avatarRequest.SelectionContext}", avatarRequest.SelectionContext == 0);
                Check($"avatar request choice count={avatarRequest.AvatarChoices.Count}", avatarRequest.AvatarChoices.Count == CapturedAvatarItemTemplateIds.Length);
                for (var i = 0; i < CapturedAvatarItemTemplateIds.Length && i < avatarRequest.AvatarChoices.Count; i++)
                    Check($"avatar choice[{i}] item={avatarRequest.AvatarChoices[i].ItemTemplateId}", avatarRequest.AvatarChoices[i].ItemTemplateId == CapturedAvatarItemTemplateIds[i]);
            }

            Check("parse captured 0x00A0 avatar-choice request with nonzero context", SelectablePackageOpenRequest.TryParse(CapturedContextAvatarOpenRequestBody, out var contextAvatarRequest));
            if (contextAvatarRequest != null)
            {
                Check($"context avatar request slot={contextAvatarRequest.SlotIndex}", contextAvatarRequest.SlotIndex == PackageSlot);
                Check($"context avatar request context={contextAvatarRequest.SelectionContext}", contextAvatarRequest.SelectionContext == 2);
                Check($"context avatar request choice count={contextAvatarRequest.AvatarChoices.Count}", contextAvatarRequest.AvatarChoices.Count == CapturedContextAvatarItemTemplateIds.Length);
                for (var i = 0; i < CapturedContextAvatarItemTemplateIds.Length && i < contextAvatarRequest.AvatarChoices.Count; i++)
                    Check($"context avatar choice[{i}] item={contextAvatarRequest.AvatarChoices[i].ItemTemplateId}", contextAvatarRequest.AvatarChoices[i].ItemTemplateId == CapturedContextAvatarItemTemplateIds[i]);
            }

            var tempDb = Path.Combine(Path.GetTempPath(), "selectable_package_selftest.db");
            DeleteTempDatabase(tempDb);

            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);
            SeedCharacterAndPackages(tempDb);

            var seriaLuckTimedRewardId = FindTimedSeriaLuckRewardId(out var timedRewardUsablePeriod);
            Check("Seria luck PVF contains a usable-period reward", seriaLuckTimedRewardId > 0 && timedRewardUsablePeriod > 0);
            if (seriaLuckTimedRewardId > 0 && timedRewardUsablePeriod > 0)
            {
                InsertLegacyNoExpireTimedReward(tempDb, seriaLuckTimedRewardId, LegacyNoExpireTimedRewardSlot);
                var nowBefore = DateTimeOffset.Now.ToUnixTimeSeconds();
                BoosterRewardResult timedRewardResult = null;
                using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(tempDb)))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        var db = new InventoryDbPrimitives();
                        Check("add Seria luck timed booster reward succeeds",
                            db.TryAddBoosterRewardItem(
                                connection,
                                transaction,
                                CharacterId,
                                AccountId,
                                seriaLuckTimedRewardId,
                                1,
                                out timedRewardResult));
                        transaction.Commit();
                    }
                }

                {
                    var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
                    var expectedMinExpire = nowBefore + (long)timedRewardUsablePeriod * 86400L - 5;
                    var expectedMaxExpire = DateTimeOffset.Now.ToUnixTimeSeconds() + (long)timedRewardUsablePeriod * 86400L + 5;
                    var legacyRow = snapshot.MainItems.Find(x =>
                        x.SlotIndex == LegacyNoExpireTimedRewardSlot &&
                        x.ItemTemplateId == seriaLuckTimedRewardId);
                    var futureRow = snapshot.MainItems.Find(x =>
                        x.ItemTemplateId == seriaLuckTimedRewardId &&
                        x.ExpireTime >= expectedMinExpire &&
                        x.ExpireTime <= expectedMaxExpire);

                    Check("legacy no-expire Seria luck reward stack remains separate",
                        legacyRow != null &&
                        legacyRow.CountOrInstanceValue == 1 &&
                        legacyRow.ExpireTime == 0);
                    Check("Seria luck timed reward stores future expire_time",
                        futureRow != null &&
                        futureRow.CountOrInstanceValue == 1);
                    Check("Seria luck timed reward result points to future stack",
                        timedRewardResult != null &&
                        futureRow != null &&
                        timedRewardResult.SlotIndex == futureRow.SlotIndex);
                }
            }

            SelectablePackageOpenResult result = null;

            {
                Check("open selectable package succeeds", store.TryOpenSelectablePackage(CharacterId, AccountId, request, out result));
            }

            if (result != null)
            {
                Check($"result package item={result.PackageItemTemplateId}", result.PackageItemTemplateId == SampleTitleSelectablePackageId);
                Check($"result reward item={result.RewardItemTemplateId}", result.RewardItemTemplateId == SampleSelectedTitleRewardId);
                Check("result added main item", result.AddedMainItemCount == 1);

                var singleAckBody = SelectablePackageAckBuilder.BuildSuccess(result.SlotIndex, result.GrantedItems);
                Check($"0x00A0 single reward ACK length={singleAckBody.Length}", singleAckBody.Length == 21);
                Check("0x00A0 single reward ACK result flag", singleAckBody.Length >= 1 && singleAckBody[0] == 1);
                Check("0x00A0 single reward ACK source slot", singleAckBody.Length >= 3 && BitConverter.ToInt16(singleAckBody, 1) == PackageSlot);
                Check("0x00A0 single reward ACK reserved context", singleAckBody.Length >= 11 && BitConverter.ToInt32(singleAckBody, 3) == 0 && BitConverter.ToInt32(singleAckBody, 7) == 0);
                Check("0x00A0 single reward ACK popup item count=1", singleAckBody.Length >= 13 && BitConverter.ToUInt16(singleAckBody, 11) == 1);
                Check("0x00A0 single reward ACK item id", singleAckBody.Length >= 21 && BitConverter.ToInt32(singleAckBody, 13) == SampleSelectedTitleRewardId);
                Check("0x00A0 single reward ACK item count", singleAckBody.Length >= 21 && BitConverter.ToInt32(singleAckBody, 17) == 1);
            }

            {
                var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
                Check("snapshot no package in source slot", snapshot.MainItems.Find(x => x.SlotIndex == PackageSlot) == null);

                var reward = snapshot.MainItems.Find(x => x.ItemTemplateId == SampleSelectedTitleRewardId);
                Check("selected title reward exists", reward != null);
                if (reward != null)
                {
                    Check($"selected title reward slot={reward.SlotIndex}", reward.SlotIndex >= 9 && reward.SlotIndex <= 64);
                    Check("selected title reward has instance value", reward.CountOrInstanceValue > 0);
                }

                var mainBody = ItemListPacketBuilder.BuildBody(snapshot, InventoryListType.Main);
                Check("main item-list body type=0", mainBody.Length > 0 && mainBody[0] == (byte)InventoryListType.Main);
            }

            if (avatarRequest != null)
            {
                SelectablePackageOpenResult avatarResult = null;

                {
                    Check("open avatar-choice selectable package succeeds", store.TryOpenSelectablePackage(CharacterId, AccountId, avatarRequest, out avatarResult));
                }

                if (avatarResult != null)
                {
                    Check($"avatar result package item={avatarResult.PackageItemTemplateId}", avatarResult.PackageItemTemplateId == SampleAvatarSelectablePackageId);
                    Check($"avatar result added avatar count={avatarResult.AddedAvatarCount}", avatarResult.AddedAvatarCount == CapturedAvatarItemTemplateIds.Length);
                    Check("avatar result no main items", avatarResult.AddedMainItemCount == 0);
                    Check($"avatar result granted count={avatarResult.GrantedItems.Count}", avatarResult.GrantedItems.Count == CapturedAvatarItemTemplateIds.Length);

                    var ackBody = SelectablePackageAckBuilder.BuildSuccess(avatarResult.SlotIndex, avatarResult.GrantedItems);
                    Check("0x00A0 success ACK carries result flag", ackBody.Length >= 1 && ackBody[0] == 1);
                    Check("0x00A0 success ACK source slot", ackBody.Length >= 3 && BitConverter.ToInt16(ackBody, 1) == AvatarPackageSlot);
                    Check("0x00A0 success ACK reserved context", ackBody.Length >= 11 && BitConverter.ToInt32(ackBody, 3) == 0 && BitConverter.ToInt32(ackBody, 7) == 0);
                    Check($"0x00A0 success ACK popup item count={BitConverter.ToUInt16(ackBody, 11)}", ackBody.Length >= 13 && BitConverter.ToUInt16(ackBody, 11) == CapturedAvatarItemTemplateIds.Length);
                    Check($"0x00A0 success ACK length={ackBody.Length}", ackBody.Length == 13 + CapturedAvatarItemTemplateIds.Length * 8);
                    Check("0x00A0 success ACK first popup item id", ackBody.Length >= 21 && BitConverter.ToInt32(ackBody, 13) == CapturedAvatarItemTemplateIds[0]);
                    Check("0x00A0 success ACK first popup item count", ackBody.Length >= 21 && BitConverter.ToInt32(ackBody, 17) == 1);
                }

                {
                    var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
                    Check("snapshot no avatar package in source slot", snapshot.MainItems.Find(x => x.SlotIndex == AvatarPackageSlot) == null);
                    foreach (var itemId in CapturedAvatarItemTemplateIds)
                        Check($"avatar reward {itemId} exists in avatar inventory", snapshot.AvatarItems.Find(x => x.AvatarItemId == itemId) != null);

                    var avatarBody = ItemListPacketBuilder.BuildBody(snapshot, InventoryListType.Avatar);
                    Check("avatar item-list body type=1", avatarBody.Length > 0 && avatarBody[0] == (byte)InventoryListType.Avatar);
                }
            }

            var crossJobAuraRequest = new SelectablePackageOpenRequest
            {
                SlotIndex = CrossJobAuraPackageSlot,
                SelectionContext = 1,
                SelectedItemTemplateId = SampleCrossJobAuraRewardId,
                SelectionFlag = 0,
            };
            SelectablePackageOpenResult crossJobAuraResult = null;

            {
                Check("open cross-job avatar single-select package succeeds", store.TryOpenSelectablePackage(CharacterId, AccountId, crossJobAuraRequest, out crossJobAuraResult));
            }

            if (crossJobAuraResult != null)
            {
                Check($"cross-job aura result package item={crossJobAuraResult.PackageItemTemplateId}", crossJobAuraResult.PackageItemTemplateId == SampleAuraSelectablePackageId);
                Check($"cross-job aura result reward item={crossJobAuraResult.RewardItemTemplateId}", crossJobAuraResult.RewardItemTemplateId == SampleCrossJobAuraRewardId);
                Check("cross-job aura result added avatar count", crossJobAuraResult.AddedAvatarCount == 1);
                Check("cross-job aura result granted count", crossJobAuraResult.GrantedItems.Count == 1);
            }

            {
                var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
                Check("snapshot no cross-job aura package in source slot", snapshot.MainItems.Find(x => x.SlotIndex == CrossJobAuraPackageSlot) == null);
                Check("cross-job aura reward exists in avatar inventory", snapshot.AvatarItems.Find(x => x.AvatarItemId == SampleCrossJobAuraRewardId) != null);
            }

            BoosterUseResult boosterResult = null;

            {
                Check("open special-kind booster package succeeds", store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
                {
                    SlotIndex = SpecialBoosterPackageSlot,
                    SelectedItemTemplateIds = Array.Empty<int>(),
                }, out boosterResult));
            }

            if (boosterResult != null)
            {
                Check($"special booster source item={boosterResult.SourceItemTemplateId}", boosterResult.SourceItemTemplateId == SampleSpecialKindBoosterPackageId);
                Check("special booster consumed source", boosterResult.SourceRemainingStackCount == 0);
                Check("special booster granted reward", boosterResult.Rewards.Find(x => x.ItemTemplateId == SampleSpecialKindBoosterRewardId) != null);
            }

            {
                var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
                Check("snapshot no special booster package in source slot", snapshot.MainItems.Find(x => x.SlotIndex == SpecialBoosterPackageSlot) == null);
                Check("special booster reward exists", snapshot.MainItems.Find(x => x.ItemTemplateId == SampleSpecialKindBoosterRewardId) != null);
            }

            Check("parse captured 0x03F3 magic-box request", MagicBoxOpenRequest.TryParse(CapturedMagicBoxOpenRequestBody, out var magicBoxRequest));
            if (magicBoxRequest != null)
            {
                Check($"magic-box request slot={magicBoxRequest.SlotIndex}", magicBoxRequest.SlotIndex == MagicBoxSlot);
                Check($"magic-box request item={magicBoxRequest.ItemTemplateId}", magicBoxRequest.ItemTemplateId == MagicBoxItemTemplateId);
                Check($"magic-box request material slot={magicBoxRequest.MaterialSlotIndex}", magicBoxRequest.MaterialSlotIndex == MagicHammerSlot);
                Check($"magic-box request material item={magicBoxRequest.MaterialItemTemplateId}", magicBoxRequest.MaterialItemTemplateId == MagicHammerItemTemplateId);
                Check($"magic-box request count={magicBoxRequest.RequestedCount}", magicBoxRequest.RequestedCount == MagicBoxBatchCount);

                Check("parse captured 0x03F3 Seria luck request", MagicBoxOpenRequest.TryParse(CapturedSeriaLuckOpenRequestBody, out var seriaLuckRequest));
                if (seriaLuckRequest != null)
                {
                    Check($"Seria luck request item={seriaLuckRequest.ItemTemplateId}", seriaLuckRequest.ItemTemplateId == SeriaLuckItemTemplateId);
                    Check($"Seria luck request material slot={seriaLuckRequest.MaterialSlotIndex}", seriaLuckRequest.MaterialSlotIndex == -1);
                    Check($"Seria luck request count={seriaLuckRequest.RequestedCount}", seriaLuckRequest.RequestedCount == SeriaLuckBatchCount);
                }

                Check("parse captured 0x00D0 Seria luck single-open request", MagicBoxOpenRequest.TryParseSingle(CapturedSeriaLuckSingleOpenRequestBody, out var seriaLuckSingleRequest));
                if (seriaLuckSingleRequest != null)
                {
                    Check($"Seria luck single request item={seriaLuckSingleRequest.ItemTemplateId}", seriaLuckSingleRequest.ItemTemplateId == 0);
                    Check($"Seria luck single request count={seriaLuckSingleRequest.RequestedCount}", seriaLuckSingleRequest.RequestedCount == 1);
                }

                Check("0x03F3 magic-box success uses 0x00A0 popup without source ACK", !InventoryHandler.ShouldSendSourceAckForBoosterResponse(0x03F3));
                Check("0x00D0 magic-box single success uses 0x00A0 popup without source ACK", !InventoryHandler.ShouldSendSourceAckForBoosterResponse(0x00D0));
                Check("0x0218 generic booster still keeps source ACK", InventoryHandler.ShouldSendSourceAckForBoosterResponse(0x0218));

                BoosterUseResult hammerBundleResult = null;

                {
                    Check("open consumable magic-hammer bundle succeeds",
                        store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
                        {
                            SlotIndex = MagicHammerBundleSlot,
                            SelectedItemTemplateIds = Array.Empty<int>(),
                            ExpectedItemTemplateId = SampleMagicHammerBundleId,
                        }, out hammerBundleResult));
                }

                short materialHammerSlot = -1;
                short bundleMagicBoxSlot = -1;
                if (hammerBundleResult != null)
                {
                    var materialReward = hammerBundleResult.Rewards.Find(x => x.ItemTemplateId == MagicHammerItemTemplateId);
                    var boxReward = hammerBundleResult.Rewards.Find(x => x.ItemTemplateId == MagicBoxItemTemplateId);
                    Check("magic-hammer bundle grants material hammer", materialReward?.GrantedCount == MagicBoxBatchCount);
                    Check("magic-hammer bundle grants chicken box", boxReward?.GrantedCount == MagicBoxBatchCount);
                }

                {
                    var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
                    Check("snapshot no consumable magic-hammer bundle in source slot", snapshot.MainItems.Find(x => x.SlotIndex == MagicHammerBundleSlot) == null);
                    Check($"wrong-tab magic hammer remains {WrongMagicHammerSlot}", snapshot.MainItems.Find(x => x.SlotIndex == WrongMagicHammerSlot)?.CountOrInstanceValue == 1);
                    var materialHammer = snapshot.MainItems.Find(x => x.ItemTemplateId == MagicHammerItemTemplateId && x.SlotIndex >= 121 && x.SlotIndex <= 176);
                    var bundleBox = snapshot.MainItems.Find(x => x.ItemTemplateId == MagicBoxItemTemplateId && x.SlotIndex >= 65 && x.SlotIndex <= 120);
                    Check("consumable magic-hammer reward enters material tab", materialHammer != null);
                    Check("consumable magic-hammer reward grants chicken box", bundleBox != null);
                    if (materialHammer != null)
                    {
                        materialHammerSlot = materialHammer.SlotIndex;
                        Check($"material-tab magic hammer count", materialHammer.CountOrInstanceValue == MagicBoxBatchCount);
                    }

                    if (bundleBox != null)
                    {
                        bundleMagicBoxSlot = bundleBox.SlotIndex;
                        Check($"chicken box count", bundleBox.CountOrInstanceValue == MagicBoxBatchCount);
                    }
                }

                var requestMaterialSlot = materialHammerSlot >= 0 ? materialHammerSlot : magicBoxRequest.MaterialSlotIndex;
                var requestMagicBoxSlot = bundleMagicBoxSlot >= 0 ? bundleMagicBoxSlot : magicBoxRequest.SlotIndex;
                BoosterUseResult magicBoxResult = null;

                {
                    Check("open magic-box request with hammer material consumes requested boosters",
                        store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
                        {
                            SlotIndex = requestMagicBoxSlot,
                            SelectedItemTemplateIds = Array.Empty<int>(),
                            ExpectedItemTemplateId = magicBoxRequest.ItemTemplateId,
                            MaterialSlotIndex = requestMaterialSlot,
                            ExpectedMaterialItemTemplateId = magicBoxRequest.MaterialItemTemplateId,
                            RequestedCount = magicBoxRequest.RequestedCount,
                        }, out magicBoxResult));
                }

                if (magicBoxResult != null)
                {
                    Check($"magic-box consumed source count={magicBoxResult.ConsumedSourceCount}", magicBoxResult.ConsumedSourceCount == MagicBoxBatchCount);
                    Check($"magic-box consumed material item={magicBoxResult.ConsumedMaterialItemTemplateId}", magicBoxResult.ConsumedMaterialItemTemplateId == MagicHammerItemTemplateId);
                    Check($"magic-box consumed material count={magicBoxResult.ConsumedMaterialCount}", magicBoxResult.ConsumedMaterialCount == MagicBoxBatchCount);
                    Check("magic-box grants at least one reward", magicBoxResult.Rewards.Count > 0);
                    Check($"magic-box grants instant teleport potions", magicBoxResult.Rewards.Find(x => x.ItemTemplateId == InstantTeleportPotionItemTemplateId)?.GrantedCount == MagicBoxBatchCount * 2);
                    Check($"magic-box grants Seria luck boxes", magicBoxResult.Rewards.Find(x => x.ItemTemplateId == SeriaLuckItemTemplateId)?.GrantedCount == MagicBoxBatchCount);
                }

                short seriaLuckSlot = -1;

                {
                    var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
                    Check("snapshot magic-box stack consumed", snapshot.MainItems.Find(x => x.SlotIndex == requestMagicBoxSlot)?.ItemTemplateId != MagicBoxItemTemplateId);
                    Check("snapshot material-tab magic hammer consumed", snapshot.MainItems.Find(x => x.SlotIndex == requestMaterialSlot) == null);
                    Check($"wrong-tab magic hammer still ignored", snapshot.MainItems.Find(x => x.SlotIndex == WrongMagicHammerSlot)?.CountOrInstanceValue == 1);
                    Check($"snapshot instant teleport potion count", snapshot.MainItems.Find(x => x.ItemTemplateId == InstantTeleportPotionItemTemplateId)?.CountOrInstanceValue == MagicBoxBatchCount * 2);
                    var seriaLuckItem = snapshot.MainItems.Find(x => x.ItemTemplateId == SeriaLuckItemTemplateId);
                    Check($"snapshot Seria luck count", seriaLuckItem?.CountOrInstanceValue == MagicBoxBatchCount);
                    if (seriaLuckItem != null)
                        seriaLuckSlot = seriaLuckItem.SlotIndex;
                }

                if (seriaLuckRequest != null && seriaLuckSlot >= 0)
                {
                    var staleSeriaLuckSlot = seriaLuckSlot;
                    RelocateItemSlot(tempDb, staleSeriaLuckSlot, RelocatedSeriaLuckSlot);
                    seriaLuckSlot = RelocatedSeriaLuckSlot;

                    BoosterUseResult seriaLuckResult = null;

                    {
                        Check("open Seria luck request without material succeeds after stale source slot",
                            store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
                            {
                                SlotIndex = staleSeriaLuckSlot,
                                SelectedItemTemplateIds = Array.Empty<int>(),
                                ExpectedItemTemplateId = seriaLuckRequest.ItemTemplateId,
                                MaterialSlotIndex = seriaLuckRequest.MaterialSlotIndex,
                                ExpectedMaterialItemTemplateId = seriaLuckRequest.MaterialItemTemplateId,
                                RequestedCount = seriaLuckRequest.RequestedCount,
                            }, out seriaLuckResult));
                    }

                    if (seriaLuckResult != null)
                    {
                        Check("Seria luck grants at least one reward", seriaLuckResult.Rewards.Count > 0);
                        Check($"Seria luck stale source request uses actual slot {RelocatedSeriaLuckSlot}", seriaLuckResult.SourceSlotIndex == RelocatedSeriaLuckSlot);
                    }

                    {
                        var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
                        Check($"snapshot Seria luck remaining count", snapshot.MainItems.Find(x => x.SlotIndex == seriaLuckSlot)?.CountOrInstanceValue == MagicBoxBatchCount - SeriaLuckBatchCount);
                    }

                    if (seriaLuckSingleRequest != null)
                    {
                        BoosterUseResult seriaLuckSingleResult = null;

                        {
                            Check("open Seria luck single without material succeeds",
                                store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
                                {
                                    SlotIndex = seriaLuckSlot,
                                    SelectedItemTemplateIds = Array.Empty<int>(),
                                    ExpectedItemTemplateId = seriaLuckSingleRequest.ItemTemplateId,
                                    MaterialSlotIndex = seriaLuckSingleRequest.MaterialSlotIndex,
                                    ExpectedMaterialItemTemplateId = seriaLuckSingleRequest.MaterialItemTemplateId,
                                }, out seriaLuckSingleResult));
                        }

                        if (seriaLuckSingleResult != null)
                        {
                            Check("Seria luck single grants at least one reward", seriaLuckSingleResult.Rewards.Count > 0);
                        }

                        {
                            var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
                            Check($"snapshot Seria luck remaining after single count", snapshot.MainItems.Find(x => x.SlotIndex == seriaLuckSlot)?.CountOrInstanceValue == MagicBoxBatchCount - SeriaLuckBatchCount - 1);
                        }
                    }
                }

                DeleteItemAtSlot(tempDb, requestMaterialSlot);

                {
                    Check("magic-box request rejects insufficient hammer material",
                        !store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
                        {
                            SlotIndex = requestMagicBoxSlot,
                            SelectedItemTemplateIds = Array.Empty<int>(),
                            ExpectedItemTemplateId = magicBoxRequest.ItemTemplateId,
                            MaterialSlotIndex = requestMaterialSlot,
                            ExpectedMaterialItemTemplateId = magicBoxRequest.MaterialItemTemplateId,
                        }, out _));
                    Check("magic-box request rejects wrong-tab hammer without material slot",
                        !store.TryUseBoosterItem(CharacterId, AccountId, new BoosterUseRequest
                        {
                            SlotIndex = requestMagicBoxSlot,
                            SelectedItemTemplateIds = Array.Empty<int>(),
                            ExpectedItemTemplateId = magicBoxRequest.ItemTemplateId,
                        }, out _));
                }

                {
                    var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
                    Check($"wrong-tab magic hammer remains after insufficient hammer", snapshot.MainItems.Find(x => x.SlotIndex == WrongMagicHammerSlot)?.CountOrInstanceValue == 1);
                }
            }

            Check("cera-shop magic-hammer bundle purchase succeeds",
                store.TryBuyCeraShopItem(CharacterId, AccountId, SampleMagicHammerBundleProductId, 1, 0, 0, out var ceraShopBundleResult));
            if (ceraShopBundleResult != null)
            {
                var ceraAckBody = CeraShopPurchaseAckBuilder.BuildSuccess(SampleMagicHammerBundleProductId, ceraShopBundleResult);
                Check("cera-shop purchase result has magic-hammer reward", ceraShopBundleResult.ItemTemplateId == MagicHammerItemTemplateId);
                Check("cera-shop purchase result has chicken-box extra reward", ceraShopBundleResult.ExtraResults.Exists(x => x.ItemTemplateId == MagicBoxItemTemplateId));
                Check("cera-shop purchase ACK keeps mall extra item count zero", ceraAckBody.Length == 24 && BitConverter.ToUInt16(ceraAckBody, 22) == 0);
            }

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(tempDb)))
            {
                connection.Open();
                var row = LoadItemRow(connection, SampleSelectedTitleRewardId);
                Check("selected title DB row exists", row.Exists);
                if (row.Exists)
                {
                    Check($"selected title item_kind={row.ItemKind}", row.ItemKind == "equipment");
                    Check($"selected title marker={row.Marker16}", row.Marker16 == -1);
                }
            }

            var invalidRequest = new SelectablePackageOpenRequest
            {
                SlotIndex = StackedPackageSlot,
                SelectedItemTemplateId = InvalidRewardItemTemplateId,
            };

            {
                Check("invalid selected reward is rejected", !store.TryOpenSelectablePackage(CharacterId, AccountId, invalidRequest, out _));
            }

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(tempDb)))
            {
                connection.Open();
                var remaining = LoadStackCount(connection, StackedPackageSlot);
                Check($"invalid open keeps package stack={remaining}", remaining == 2);
            }

            var errorAckBody = SelectablePackageAckBuilder.BuildError();
            Check($"0x00A0 error ACK padded length={errorAckBody.Length}", errorAckBody.Length == 22);
            Check("0x00A0 error ACK safe category sentinel", errorAckBody.Length >= 10 && BitConverter.ToInt32(errorAckBody, 2) == -1 && BitConverter.ToInt32(errorAckBody, 6) == -1);

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static void DeleteTempDatabase(string path)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    var file = path + suffix;
                    if (File.Exists(file))
                        File.Delete(file);
                }
                catch
                {
                }
            }
        }

        private static void SeedCharacterAndPackages(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'selectable-package-selftest', '');

UPDATE accounts
SET cera = 100000000
WHERE account_id = @accountId;

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'selectable-package-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 0, 24);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES
    ('character', @characterId, @characterId, 0, @packageSlot, @templateId, 'special',
     1, 1, 0, 0, 0, @expireTime, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @stackedPackageSlot, @templateId, 'special',
     2, 2, 0, 0, 0, @expireTime, 0, 0, '{}'),
                    ('character', @characterId, @characterId, 0, @avatarPackageSlot, @avatarTemplateId, 'special',
                     1, 1, 0, 0, 0, @expireTime, 0, 0, '{}'),
                    ('character', @characterId, @characterId, 0, @crossJobAuraPackageSlot, @auraTemplateId, 'special',
                     1, 1, 0, 0, 0, @expireTime, 0, 0, '{}'),
                    ('character', @characterId, @characterId, 0, @specialBoosterPackageSlot, @specialBoosterTemplateId, 'special',
                      1, 1, 0, 0, 0, @expireTime, 0, 0, '{}'),
                    ('character', @characterId, @characterId, 0, @magicHammerBundleSlot, @magicHammerBundleTemplateId, 'stackable',
                     1, 1, 0, 0, 0, 0, 0, 0, '{}'),
                    ('character', @characterId, @characterId, 0, @wrongMagicHammerSlot, @magicHammerTemplateId, 'stackable',
                     1, 1, 0, 0, 0, 0, 0, 0, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@packageSlot", PackageSlot);
                    command.Parameters.AddWithValue("@stackedPackageSlot", StackedPackageSlot);
                    command.Parameters.AddWithValue("@avatarPackageSlot", AvatarPackageSlot);
                    command.Parameters.AddWithValue("@crossJobAuraPackageSlot", CrossJobAuraPackageSlot);
                    command.Parameters.AddWithValue("@specialBoosterPackageSlot", SpecialBoosterPackageSlot);
                    command.Parameters.AddWithValue("@magicHammerBundleSlot", MagicHammerBundleSlot);
                    command.Parameters.AddWithValue("@wrongMagicHammerSlot", WrongMagicHammerSlot);
                    command.Parameters.AddWithValue("@templateId", SampleTitleSelectablePackageId);
                    command.Parameters.AddWithValue("@avatarTemplateId", SampleAvatarSelectablePackageId);
                    command.Parameters.AddWithValue("@auraTemplateId", SampleAuraSelectablePackageId);
                    command.Parameters.AddWithValue("@specialBoosterTemplateId", SampleSpecialKindBoosterPackageId);
                    command.Parameters.AddWithValue("@magicHammerBundleTemplateId", SampleMagicHammerBundleId);
                    command.Parameters.AddWithValue("@magicHammerTemplateId", MagicHammerItemTemplateId);
                    command.Parameters.AddWithValue("@expireTime", ToUnixLocal("2027-08-13 06:00:00"));
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int ToUnixLocal(string expirationDate)
        {
            var localDateTime = DateTime.ParseExact(
                expirationDate,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture);
            var offset = new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
            return (int)offset.ToUnixTimeSeconds();
        }

        private static int FindTimedSeriaLuckRewardId(out int usablePeriod)
        {
            usablePeriod = 0;
            var seriaLuck = InventoryDbPrimitives.LoadStackableItem(SeriaLuckItemTemplateId);
            if (seriaLuck == null)
                return 0;

            var pools = new[]
            {
                seriaLuck.RandomBoxRewards,
                seriaLuck.BoosterRewards,
                seriaLuck.PackageRewards,
                seriaLuck.BoosterSelectionRewards,
            };

            foreach (var pool in pools)
            {
                foreach (var reward in pool)
                {
                    if (reward == null || reward.ItemId <= 0)
                        continue;

                    var rewardStackable = InventoryDbPrimitives.LoadStackableItem(reward.ItemId);
                    if (rewardStackable == null || rewardStackable.UsablePeriod <= 0)
                        continue;

                    usablePeriod = rewardStackable.UsablePeriod;
                    return reward.ItemId;
                }
            }

            return 0;
        }

        private static void InsertLegacyNoExpireTimedReward(string databasePath, int itemTemplateId, short slotIndex)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, @slotIndex, @itemTemplateId, 'stackable',
    1, 1, 0, 0, 0, 0, 0, 0, '{}');";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slotIndex", slotIndex);
                    command.Parameters.AddWithValue("@itemTemplateId", itemTemplateId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static (bool Exists, string ItemKind, int Marker16) LoadItemRow(SqliteConnection connection, int itemTemplateId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_kind, marker_16
FROM character_items
WHERE character_id=@characterId AND item_template_id=@itemTemplateId
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", CharacterId);
                command.Parameters.AddWithValue("@itemTemplateId", itemTemplateId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return (false, null, 0);

                    return (true, reader.GetString(0), reader.GetInt32(1));
                }
            }
        }

        private static int LoadStackCount(SqliteConnection connection, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT stack_count
FROM character_items
WHERE character_id=@characterId AND list_type=0 AND slot_index=@slotIndex;";
                command.Parameters.AddWithValue("@characterId", CharacterId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? -1 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        private static void RelocateItemSlot(string databasePath, short fromSlot, short toSlot)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE character_items
SET slot_index=@toSlot
WHERE character_id=@characterId AND list_type=0 AND slot_index=@fromSlot;";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@fromSlot", fromSlot);
                    command.Parameters.AddWithValue("@toSlot", toSlot);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SetStackCount(string databasePath, short slotIndex, int stackCount)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE character_items
SET stack_count=@stackCount, instance_value=@stackCount
WHERE character_id=@characterId AND list_type=0 AND slot_index=@slotIndex;";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slotIndex", slotIndex);
                    command.Parameters.AddWithValue("@stackCount", stackCount);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteItemAtSlot(string databasePath, short slotIndex)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
DELETE FROM character_items
WHERE character_id=@characterId AND list_type=0 AND slot_index=@slotIndex;";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slotIndex", slotIndex);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok)
                _pass++;
            else
                _fail++;
        }

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }
    }
}
