using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
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
        private const int PackageItemTemplateId = 10007993;
        private const int AvatarPackageItemTemplateId = 10008359;
        private const int AuraPackageItemTemplateId = 10008357;
        private const int SelectedRewardItemTemplateId = 400330051;
        private const int CapturedCrossJobAuraRewardItemTemplateId = 112590011;
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
            Check($"request selected item={request.SelectedItemTemplateId}", request.SelectedItemTemplateId == SelectedRewardItemTemplateId);
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

            SelectablePackageOpenResult result = null;
            using (store.BeginScope(CharacterId, AccountId))
            {
                Check("open selectable package succeeds", store.TryOpenSelectablePackage(request, out result));
            }

            if (result != null)
            {
                Check($"result package item={result.PackageItemTemplateId}", result.PackageItemTemplateId == PackageItemTemplateId);
                Check($"result reward item={result.RewardItemTemplateId}", result.RewardItemTemplateId == SelectedRewardItemTemplateId);
                Check("result added main item", result.AddedMainItemCount == 1);

                var singleAckBody = SelectablePackageAckBuilder.BuildSuccess(result.GrantedItems);
                Check($"0x00A0 single reward ACK length={singleAckBody.Length}", singleAckBody.Length == 32);
                Check("0x00A0 single reward ACK popup item count=1", singleAckBody.Length >= 24 && BitConverter.ToUInt16(singleAckBody, 22) == 1);
                Check("0x00A0 single reward ACK item id", singleAckBody.Length >= 32 && BitConverter.ToInt32(singleAckBody, 24) == SelectedRewardItemTemplateId);
                Check("0x00A0 single reward ACK item count", singleAckBody.Length >= 32 && BitConverter.ToInt32(singleAckBody, 28) == 1);
            }

            using (store.BeginScope(CharacterId, AccountId))
            {
                var snapshot = store.LoadCharacterItemListSnapshot();
                Check("snapshot no package in source slot", snapshot.MainItems.Find(x => x.SlotIndex == PackageSlot) == null);

                var reward = snapshot.MainItems.Find(x => x.ItemTemplateId == SelectedRewardItemTemplateId);
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
                using (store.BeginScope(CharacterId, AccountId))
                {
                    Check("open avatar-choice selectable package succeeds", store.TryOpenSelectablePackage(avatarRequest, out avatarResult));
                }

                if (avatarResult != null)
                {
                    Check($"avatar result package item={avatarResult.PackageItemTemplateId}", avatarResult.PackageItemTemplateId == AvatarPackageItemTemplateId);
                    Check($"avatar result added avatar count={avatarResult.AddedAvatarCount}", avatarResult.AddedAvatarCount == CapturedAvatarItemTemplateIds.Length);
                    Check("avatar result no main items", avatarResult.AddedMainItemCount == 0);
                    Check($"avatar result granted count={avatarResult.GrantedItems.Count}", avatarResult.GrantedItems.Count == CapturedAvatarItemTemplateIds.Length);

                    var ackBody = SelectablePackageAckBuilder.BuildSuccess(avatarResult.GrantedItems);
                    Check("0x00A0 success ACK carries result flag", ackBody.Length >= 24 && ackBody[0] == 1);
                    Check("0x00A0 success ACK popup category sentinel", ackBody.Length >= 24 && BitConverter.ToInt32(ackBody, 2) == -1);
                    Check("0x00A0 success ACK popup commodity sentinel", ackBody.Length >= 24 && BitConverter.ToInt32(ackBody, 6) == -1);
                    Check("0x00A0 success ACK keeps mall-compatible var_824", ackBody.Length >= 24 && BitConverter.ToInt32(ackBody, 18) == 0);
                    Check($"0x00A0 success ACK popup item count={BitConverter.ToUInt16(ackBody, 22)}", ackBody.Length >= 24 && BitConverter.ToUInt16(ackBody, 22) == CapturedAvatarItemTemplateIds.Length);
                    Check($"0x00A0 success ACK length={ackBody.Length}", ackBody.Length == 24 + CapturedAvatarItemTemplateIds.Length * 8);
                    Check("0x00A0 success ACK first popup item id", ackBody.Length >= 32 && BitConverter.ToInt32(ackBody, 24) == CapturedAvatarItemTemplateIds[0]);
                    Check("0x00A0 success ACK first popup item count", ackBody.Length >= 32 && BitConverter.ToInt32(ackBody, 28) == 1);
                }

                using (store.BeginScope(CharacterId, AccountId))
                {
                    var snapshot = store.LoadCharacterItemListSnapshot();
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
                SelectedItemTemplateId = CapturedCrossJobAuraRewardItemTemplateId,
                SelectionFlag = 0,
            };
            SelectablePackageOpenResult crossJobAuraResult = null;
            using (store.BeginScope(CharacterId, AccountId))
            {
                Check("open cross-job avatar single-select package succeeds", store.TryOpenSelectablePackage(crossJobAuraRequest, out crossJobAuraResult));
            }

            if (crossJobAuraResult != null)
            {
                Check($"cross-job aura result package item={crossJobAuraResult.PackageItemTemplateId}", crossJobAuraResult.PackageItemTemplateId == AuraPackageItemTemplateId);
                Check($"cross-job aura result reward item={crossJobAuraResult.RewardItemTemplateId}", crossJobAuraResult.RewardItemTemplateId == CapturedCrossJobAuraRewardItemTemplateId);
                Check("cross-job aura result added avatar count", crossJobAuraResult.AddedAvatarCount == 1);
                Check("cross-job aura result granted count", crossJobAuraResult.GrantedItems.Count == 1);
            }

            using (store.BeginScope(CharacterId, AccountId))
            {
                var snapshot = store.LoadCharacterItemListSnapshot();
                Check("snapshot no cross-job aura package in source slot", snapshot.MainItems.Find(x => x.SlotIndex == CrossJobAuraPackageSlot) == null);
                Check("cross-job aura reward exists in avatar inventory", snapshot.AvatarItems.Find(x => x.AvatarItemId == CapturedCrossJobAuraRewardItemTemplateId) != null);
            }

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(tempDb)))
            {
                connection.Open();
                var row = LoadItemRow(connection, SelectedRewardItemTemplateId);
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
            using (store.BeginScope(CharacterId, AccountId))
            {
                Check("invalid selected reward is rejected", !store.TryOpenSelectablePackage(invalidRequest, out _));
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
                     1, 1, 0, 0, 0, @expireTime, 0, 0, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@packageSlot", PackageSlot);
                    command.Parameters.AddWithValue("@stackedPackageSlot", StackedPackageSlot);
                    command.Parameters.AddWithValue("@avatarPackageSlot", AvatarPackageSlot);
                    command.Parameters.AddWithValue("@crossJobAuraPackageSlot", CrossJobAuraPackageSlot);
                    command.Parameters.AddWithValue("@templateId", PackageItemTemplateId);
                    command.Parameters.AddWithValue("@avatarTemplateId", AvatarPackageItemTemplateId);
                    command.Parameters.AddWithValue("@auraTemplateId", AuraPackageItemTemplateId);
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
