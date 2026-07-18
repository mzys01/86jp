using System;
using System.IO;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class RentalInfoSelfTest
    {
        public static int Run()
        {
            var rental = new RentalInfoSnapshot();
            var shopId = 0xFC85987Au;
            var inventoryId = 0x05FAEAB2u;
            var now = 1000u;
            var expireTime = now + 86400u;

            rental.Items.Add(new RentalItemSnapshot { ItemId = shopId, ExpireTime = 1 });
            rental.UpsertItem(shopId, inventoryId, expireTime);

            if (rental.Items.Count != 1)
                return Fail("legacy shop id entry was not replaced");

            if (rental.Items[0].ItemId != shopId)
                return Fail("rental panel item id must be shop id");

            if (rental.Items[0].InventoryTemplateId != inventoryId)
                return Fail("rental panel item must preserve inventory template id");

            var body = RentalInfoBodyBuilder.BuildWireBody(60, rental, now);
            if (body.Length != 16)
                return Fail("unexpected 0x0357 body length");

            if (BitConverter.ToUInt32(body, 0) != 60)
                return Fail("lucky star field mismatch");

            if (BitConverter.ToUInt32(body, 4) != 1)
                return Fail("item count field mismatch");

            if (BitConverter.ToUInt32(body, 8) != inventoryId)
                return Fail("wire item id must be inventory template id");

            if (BitConverter.ToUInt32(body, 12) != expireTime)
                return Fail("wire item secondary field must be absolute expire time");

            var storage = RentalInfoSnapshot.BuildStorageBody(rental);
            var parsed = new RentalInfoSnapshot();
            RentalInfoSnapshot.ParseStorageBody(storage, parsed);
            if (parsed.Items.Count != 1
                || parsed.Items[0].ItemId != shopId
                || parsed.Items[0].InventoryTemplateId != inventoryId
                || parsed.Items[0].ExpireTime != expireTime)
                return Fail("storage roundtrip must preserve shop id, inventory template id, and expire time");

            rental.UpsertItem(0x7893B721u, 0x05FAEAB4u, now + 3600u);
            rental.UpsertItem(0xE32F509Fu, 0x05FAEAB3u, now + 7200u);
            rental.UpsertItem(0x1E3D6BE4u, 0x05FAEAB4u, now + 8000u);
            rental.UpsertItem(0x1E3D6BE4u, 0x05FAEAB3u, now + 9000u);

            if (rental.Items.Count != 3)
                return Fail("same shop id with different inventory templates must not collapse rental entries");

            body = RentalInfoBodyBuilder.BuildWireBody(30, rental, now);
            if (body.Length != 32)
                return Fail("0x0357 wire body must include three active rental items");

            if (BitConverter.ToUInt32(body, 4) != 3)
                return Fail("wire item count must include three active rentals");

            if (BitConverter.ToUInt32(body, 8) != inventoryId
                || BitConverter.ToUInt32(body, 16) != 0x05FAEAB4u
                || BitConverter.ToUInt32(body, 24) != 0x05FAEAB3u)
                return Fail("wire body should keep all three rental inventory template ids in storage order");

            if (BitConverter.ToUInt32(body, 12) != expireTime
                || BitConverter.ToUInt32(body, 20) != now + 8000u
                || BitConverter.ToUInt32(body, 28) != now + 9000u)
                return Fail("wire body should include absolute expire time as secondary field");

            rental.Items[0].ExpireTime = now;
            if (rental.RemoveExpired(now) != 1)
                return Fail("expired rental entries must be removed from snapshot");

            var cleanupFailure = RunPersistedRentalCleanupRegression();
            if (cleanupFailure != null)
                return Fail(cleanupFailure);

            Console.WriteLine("RentalInfoSelfTest OK");
            return 0;
        }

        private static string RunPersistedRentalCleanupRegression()
        {
            const int accountId = 997001;
            const int characterId = 997002;
            const int legacyRentalTemplateId = int.MaxValue - 1;
            const int activeLegacyRentalTemplateId = int.MaxValue - 2;
            const int outOfRangeRentalTemplateId = int.MaxValue - 3;
            const uint nowUnixSeconds = 1_700_000_000;
            const int expiredAtUnixSeconds = 1_699_999_999;
            const int activeUntilUnixSeconds = 1_700_000_001;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "rental-info-cleanup-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                var store = new SqliteInventoryStore(
                    databasePath,
                    ServerPaths.SchemaFilePath,
                    new FixedRentalTimeProvider(nowUnixSeconds));

                using (var connection = new SqliteConnection(store.ConnectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT OR IGNORE INTO accounts(account_id, m_id, password_hash)
VALUES(@accountId, 'rental-cleanup-selftest', '');
INSERT OR IGNORE INTO characters(character_id, account_id, name)
VALUES(@characterId, @accountId, 'rental-cleanup-main');
INSERT INTO character_items(
    owner_scope, owner_id, character_id, list_type, slot_index,
    item_template_id, item_kind, expire_time)
VALUES
    ('character', @characterId, @characterId, 0, @rentalSlot, @legacyRentalTemplateId, 'special', @expiredAt),
    ('character', @characterId, @characterId, 0, @activeRentalSlot, @activeLegacyRentalTemplateId, 'special', @activeUntil),
    ('character', @characterId, @characterId, 0, @outOfRangeSlot, @outOfRangeRentalTemplateId, 'special', @expiredAt);
INSERT INTO character_equipped_entries(character_id, slot, item_id, expire_time, raw_entry)
VALUES(@characterId, 0, @legacyRentalTemplateId, @expiredAt, X'00');
INSERT INTO character_rental_items(
    character_id, shop_entry_id, inventory_template_id, expire_time)
VALUES
    (@characterId, 123, @legacyRentalTemplateId, @expiredAt),
    (@characterId, 124, @activeLegacyRentalTemplateId, @activeUntil),
    (@characterId, 125, @outOfRangeRentalTemplateId, @expiredAt);";
                        command.Parameters.AddWithValue("@accountId", accountId);
                        command.Parameters.AddWithValue("@characterId", characterId);
                        command.Parameters.AddWithValue("@legacyRentalTemplateId", legacyRentalTemplateId);
                        command.Parameters.AddWithValue("@activeLegacyRentalTemplateId", activeLegacyRentalTemplateId);
                        command.Parameters.AddWithValue("@outOfRangeRentalTemplateId", outOfRangeRentalTemplateId);
                        command.Parameters.AddWithValue("@rentalSlot", SqliteInventoryStore.QuickSlotStart);
                        command.Parameters.AddWithValue("@activeRentalSlot", SqliteInventoryStore.QuickSlotStart + 1);
                        command.Parameters.AddWithValue("@outOfRangeSlot", SqliteInventoryStore.RentalBagSlotEnd + 1);
                        command.Parameters.AddWithValue("@expiredAt", expiredAtUnixSeconds);
                        command.Parameters.AddWithValue("@activeUntil", activeUntilUnixSeconds);
                        command.ExecuteNonQuery();
                    }
                }

                var removed = store.DeleteExpiredRentalEquipment(characterId, accountId);

                if (removed != 2)
                    return "cleanup must remove persisted legacy rentals from bag and equipment";

                using (var connection = new SqliteConnection(store.ConnectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM character_items
     WHERE character_id = @characterId AND item_template_id = @legacyRentalTemplateId),
    (SELECT COUNT(*) FROM character_equipped_entries
     WHERE character_id = @characterId AND item_id = @legacyRentalTemplateId),
    (SELECT COUNT(*) FROM character_items
     WHERE character_id = @characterId AND item_template_id = @activeLegacyRentalTemplateId),
    (SELECT COUNT(*) FROM character_items
     WHERE character_id = @characterId AND item_template_id = @outOfRangeRentalTemplateId);";
                        command.Parameters.AddWithValue("@characterId", characterId);
                        command.Parameters.AddWithValue("@legacyRentalTemplateId", legacyRentalTemplateId);
                        command.Parameters.AddWithValue("@activeLegacyRentalTemplateId", activeLegacyRentalTemplateId);
                        command.Parameters.AddWithValue("@outOfRangeRentalTemplateId", outOfRangeRentalTemplateId);
                        using (var reader = command.ExecuteReader())
                        {
                            if (!reader.Read())
                                return "cleanup verification query returned no row";
                            if (reader.GetInt32(0) != 0 || reader.GetInt32(1) != 0)
                                return "expired persisted rentals must be removed from the rental bag range and equipment";
                            if (reader.GetInt32(2) != 1)
                                return "active persisted rentals must remain unchanged";
                            if (reader.GetInt32(3) != 1)
                                return "cleanup must not extend beyond the existing rental bag range";
                        }
                    }
                }
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }

            return null;
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private sealed class FixedRentalTimeProvider : IRentalTimeProvider
        {
            private readonly uint _now;

            internal FixedRentalTimeProvider(uint now)
            {
                _now = now;
            }

            public uint UtcNowUnixSeconds() => _now;
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine("RentalInfoSelfTest FAILED: " + message);
            return 1;
        }
    }
}
