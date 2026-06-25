using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class AvatarPackageSelfTest
    {
        private const int AccountId = 1;
        private const int CharacterId = 999207;
        private const short PackageSlot = 67;
        private const int PackageItemTemplateId = 10008287;
        private const int ExpectedAvatarUnknownFixed30 = 0x00001E00;
        private const ushort ExpectedAvatarUnknownFixed4 = 0x0400;
        private const int AvatarEntryLength = 126;

        private static readonly int[] ExpectedAvatarItemIds =
        {
            108550662,
            108560645,
            108570739,
            108520635,
            108500651,
            108510647,
            108530609,
            108540619,
            108580171,
        };

        private static readonly byte[] CapturedOpenRequestBody =
        {
            0x43, 0x00, 0x09,
            0x06, 0x5A, 0x78, 0x06, 0x00,
            0x05, 0x81, 0x78, 0x06, 0x00,
            0x73, 0xA8, 0x78, 0x06, 0x02,
            0xBB, 0xE4, 0x77, 0x06, 0x02,
            0xAB, 0x96, 0x77, 0x06, 0x3F,
            0xB7, 0xBD, 0x77, 0x06, 0x01,
            0xB1, 0x0B, 0x78, 0x06, 0x04,
            0xCB, 0x32, 0x78, 0x06, 0x02,
            0x4B, 0xCD, 0x78, 0x06, 0x00,
        };

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== AVATAR_PACKAGE selftest ===");

            Check("parse captured 0x0207 request", AvatarPackageOpenRequest.TryParse(CapturedOpenRequestBody, out var request));
            if (request == null)
            {
                PrintSummary();
                return 1;
            }

            Check($"request slot={request.SlotIndex}", request.SlotIndex == PackageSlot);
            Check($"request choice count={request.Choices.Count}", request.Choices.Count == ExpectedAvatarItemIds.Length);
            for (var i = 0; i < ExpectedAvatarItemIds.Length && i < request.Choices.Count; i++)
                Check($"choice[{i}] item={request.Choices[i].ItemTemplateId}", request.Choices[i].ItemTemplateId == ExpectedAvatarItemIds[i]);

            var tempDb = Path.Combine(Path.GetTempPath(), "avatar_package_selftest.db");
            DeleteTempDatabase(tempDb);

            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);
            SeedCharacterAndPackage(tempDb);

            AvatarPackageOpenResult result = null;
            using (store.BeginScope(CharacterId, AccountId))
            {
                Check("open avatar package succeeds", store.TryOpenAvatarPackage(request, out result));
            }

            if (result != null)
            {
                Check($"result package item={result.PackageItemTemplateId}", result.PackageItemTemplateId == PackageItemTemplateId);
                Check($"result avatar count={result.AddedAvatarCount}", result.AddedAvatarCount == ExpectedAvatarItemIds.Length);
                Check("result granted popup item count covers known rewards", result.GrantedItems.Count >= ExpectedAvatarItemIds.Length + 6);
                foreach (var expectedItemId in ExpectedAvatarItemIds)
                    Check($"granted avatar reward {expectedItemId}", result.GrantedItems.Exists(x => x.ListType == InventoryListType.Avatar && x.ItemTemplateId == expectedItemId && x.DisplayCount == 1));
                Check("granted expiring reward 10008359", result.GrantedItems.Exists(x => x.ListType == InventoryListType.Main && x.ItemTemplateId == 10008359 && x.DisplayCount == 1));
                Check("granted expiring reward 10008357", result.GrantedItems.Exists(x => x.ListType == InventoryListType.Main && x.ItemTemplateId == 10008357 && x.DisplayCount == 1));
                Check("granted expiring reward 10008358", result.GrantedItems.Exists(x => x.ListType == InventoryListType.Main && x.ItemTemplateId == 10008358 && x.DisplayCount == 1));
                Check("granted expiring reward 10008360", result.GrantedItems.Exists(x => x.ListType == InventoryListType.Main && x.ItemTemplateId == 10008360 && x.DisplayCount == 1));
                Check("granted expiring reward 10008361", result.GrantedItems.Exists(x => x.ListType == InventoryListType.Main && x.ItemTemplateId == 10008361 && x.DisplayCount == 1));
                Check("granted expiring reward 10008362", result.GrantedItems.Exists(x => x.ListType == InventoryListType.Main && x.ItemTemplateId == 10008362 && x.DisplayCount == 5));

                var ackBody = AvatarPackageAckBuilder.BuildSuccess(result.SlotIndex);
                Check("0x0207 success ACK carries result flag", ackBody.Length >= 1 && ackBody[0] == 1);
                Check("0x0207 success ACK carries source slot", ackBody.Length == 3 && BitConverter.ToInt16(ackBody, 1) == PackageSlot);

                var popupAckBody = SelectablePackageAckBuilder.BuildSuccess(result.SlotIndex, result.GrantedItems);
                Check("0x0207 popup notification uses 0x00A0 result flag", popupAckBody.Length >= 1 && popupAckBody[0] == 1);
                Check("0x0207 popup notification source slot", popupAckBody.Length >= 3 && BitConverter.ToInt16(popupAckBody, 1) == PackageSlot);
                Check("0x0207 popup notification reward count field", popupAckBody.Length >= 13 && BitConverter.ToUInt16(popupAckBody, 11) == result.GrantedItems.Count);
                Check("0x0207 popup notification length matches granted rewards", popupAckBody.Length == 13 + result.GrantedItems.Count * 8);
                Check("0x0207 popup notification first item id", popupAckBody.Length >= 21 && BitConverter.ToInt32(popupAckBody, 13) == ExpectedAvatarItemIds[0]);
                Check("0x0207 popup notification first item count", popupAckBody.Length >= 21 && BitConverter.ToInt32(popupAckBody, 17) == 1);
            }

            using (store.BeginScope(CharacterId, AccountId))
            {
                var snapshot = store.LoadCharacterItemListSnapshot();
                Check($"snapshot avatar count={snapshot.AvatarItems.Count}", snapshot.AvatarItems.Count == ExpectedAvatarItemIds.Length);
                Check("snapshot no package in main inventory", snapshot.MainItems.Find(x => x.SlotIndex == PackageSlot) == null);
                CheckExpiringReward(snapshot, 10008359, 1, "2027-11-19 06:00:00");
                CheckExpiringReward(snapshot, 10008357, 1, "2027-11-19 06:00:00");
                CheckExpiringReward(snapshot, 10008358, 1, "2027-11-19 06:00:00");
                CheckExpiringReward(snapshot, 10008360, 1, "2027-12-29 06:00:00");
                CheckExpiringReward(snapshot, 10008361, 1, "2027-12-29 06:00:00");
                CheckExpiringReward(snapshot, 10008362, 5, "2027-11-19 06:00:00");

                var optionByItemId = ToOptionMap(request);
                foreach (var expectedItemId in ExpectedAvatarItemIds)
                {
                    var item = snapshot.AvatarItems.Find(x => x.AvatarItemId == expectedItemId);
                    Check($"avatar {expectedItemId} exists", item != null);
                    if (item != null)
                    {
                        Check($"avatar {expectedItemId} option={item.OptionValue}", item.OptionValue == optionByItemId[expectedItemId]);
                        Check($"avatar {expectedItemId} fixed30={item.UnknownFixed30}", item.UnknownFixed30 == ExpectedAvatarUnknownFixed30);
                        Check($"avatar {expectedItemId} fixed4={item.UnknownFixed4}", item.UnknownFixed4 == ExpectedAvatarUnknownFixed4);
                    }
                }

                var avatarBody = ItemListPacketBuilder.BuildBody(snapshot, InventoryListType.Avatar);
                Check($"avatar item-list body length={avatarBody.Length}", avatarBody.Length == 5 + ExpectedAvatarItemIds.Length * AvatarEntryLength);
                Check("avatar item-list type=1", avatarBody.Length > 0 && avatarBody[0] == (byte)InventoryListType.Avatar);
                Check("avatar item-list count=9", avatarBody.Length >= 5 && BitConverter.ToUInt16(avatarBody, 3) == ExpectedAvatarItemIds.Length);
                for (var i = 0; i < ExpectedAvatarItemIds.Length && avatarBody.Length >= 5 + (i + 1) * AvatarEntryLength; i++)
                {
                    var offset = 5 + i * AvatarEntryLength;
                    Check($"avatar packet[{i}] slot={i}", BitConverter.ToInt16(avatarBody, offset) == i);
                    Check($"avatar packet[{i}] item={ExpectedAvatarItemIds[i]}", BitConverter.ToInt32(avatarBody, offset + 2) == ExpectedAvatarItemIds[i]);
                    Check($"avatar packet[{i}] fixed30", BitConverter.ToInt32(avatarBody, offset + 83) == ExpectedAvatarUnknownFixed30);
                    Check($"avatar packet[{i}] fixed4", BitConverter.ToUInt16(avatarBody, offset + 117) == ExpectedAvatarUnknownFixed4);
                }
            }

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static void CheckExpiringReward(
            CharacterItemListSnapshot snapshot,
            int itemTemplateId,
            int expectedCount,
            string expirationDate)
        {
            var expectedExpireTime = ToUnixLocal(expirationDate);
            var item = snapshot.MainItems.Find(x => x.ItemTemplateId == itemTemplateId);

            if (expectedExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
            {
                Check($"expired reward {itemTemplateId} is hidden", item == null);
                return;
            }

            Check($"expiring reward {itemTemplateId} exists", item != null);
            if (item != null)
            {
                Check($"expiring reward {itemTemplateId} count={expectedCount}", item.CountOrInstanceValue == expectedCount);
                Check($"expiring reward {itemTemplateId} expire={expectedExpireTime}", item.ExpireTime == expectedExpireTime);
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

        private static Dictionary<int, byte> ToOptionMap(AvatarPackageOpenRequest request)
        {
            var map = new Dictionary<int, byte>();
            foreach (var choice in request.Choices)
                map[choice.ItemTemplateId] = choice.OptionValue;
            return map;
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

        private static void SeedCharacterAndPackage(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'avatar-package-selftest', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'avatar-package-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 0, 24);

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 1, 0);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, @slot, @templateId, 'stackable',
    1, 1, 0, 0, 0, 0, 0,
    0, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slot", PackageSlot);
                    command.Parameters.AddWithValue("@templateId", PackageItemTemplateId);
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
