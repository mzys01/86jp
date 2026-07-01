using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class PetConsumableSelfTest
    {
        private const int AccountId = 163001;
        private const int CharacterId = 163001;
        private const short PetConsumableSlot = 189;
        private const int PetConsumableItemTemplateId = 10000163;
        private const int InitialCount = 999;

        public static int Run()
        {
            Console.WriteLine("=== PET_CONSUMABLE selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "pet-consumable.db");
            DeleteTempDatabase(dbPath);

            var store = new SqliteInventoryStore(dbPath, ServerPaths.SchemaFilePath);
            SeedPetConsumable(dbPath);

            var failures = 0;
            using (store.BeginScope(CharacterId, AccountId))
            {
                Check("using one pet-list consumable succeeds",
                    store.TryDeleteItem(InventoryListType.Pet, PetConsumableSlot, 1, out var result),
                    ref failures);
                if (result != null)
                {
                    Check("result remains in pet list", result.ListType == InventoryListType.Pet, ref failures);
                    Check("remaining count is decremented by one", result.RemainingStackCount == InitialCount - 1, ref failures);
                    Check("instance mirrors remaining count", result.InstanceValue == InitialCount - 1, ref failures);
                }
            }

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                connection.Open();
                var row = LoadPetConsumableRow(connection);
                Check("pet-list consumable row remains after single use", row.Exists, ref failures);
                if (row.Exists)
                {
                    Check("stack_count decremented", row.StackCount == InitialCount - 1, ref failures);
                    Check("instance_value decremented", row.InstanceValue == InitialCount - 1, ref failures);
                    Check("pet_serial_or_handle mirrors count", row.PetSerialOrHandle == InitialCount - 1, ref failures);
                }
            }

            using (store.BeginScope(CharacterId, AccountId))
            {
                var snapshot = store.LoadCharacterItemListSnapshot();
                var petItem = snapshot.PetItems.FirstOrDefault(x => x.SlotIndex == PetConsumableSlot);
                Check("pet item-list update can still serialize the remaining stack",
                    petItem != null && petItem.CreatureSerialOrHandle == InitialCount - 1,
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void SeedPetConsumable(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'pet-consumable-selftest', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'pet-consumable-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 7, 0);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 7, @slotIndex, @itemTemplateId, 'pet',
    @stackCount, @stackCount, 0, 0, 0, 0, 0,
    @stackCount, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slotIndex", PetConsumableSlot);
                    command.Parameters.AddWithValue("@itemTemplateId", PetConsumableItemTemplateId);
                    command.Parameters.AddWithValue("@stackCount", InitialCount);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static (bool Exists, int StackCount, int InstanceValue, int PetSerialOrHandle) LoadPetConsumableRow(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT stack_count, instance_value, pet_serial_or_handle
FROM character_items
WHERE character_id = @characterId
  AND list_type = 7
  AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", CharacterId);
                command.Parameters.AddWithValue("@slotIndex", PetConsumableSlot);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return (false, 0, 0, 0);

                    return (true, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
                }
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
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok)
                failures++;
        }
    }
}
