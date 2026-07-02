using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class PetHatchSelfTest
    {
        private const int AccountId = 163003;
        private const int CharacterId = 163003;
        private const short EggSlot = 48;
        private const int BoboEggItemId = 0x0000F62E;
        private const int BoboPetItemId = 0x0000F62F;
        private const int PetSerial = 123;

        public static int Run()
        {
            Console.WriteLine("=== PET_HATCH selftest ===");

            var failures = 0;
            Check("pet egg is pet inventory equipment",
                ItemMetadataResolver.IsPetInventoryEquipment(BoboEggItemId),
                ref failures);
            Check("pet egg PVF output index resolves hatched creature",
                CreatureEggResolver.TryResolveHatchedCreatureItemId(BoboEggItemId, out var outputItemId)
                && outputItemId == BoboPetItemId,
                ref failures);

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "pet-hatch.db");
            DeleteTempDatabase(dbPath);

            var store = new SqliteInventoryStore(dbPath, ServerPaths.SchemaFilePath);
            SeedPetEgg(dbPath);

            Check("hatching pet egg succeeds",
                store.TryHatchCreatureEgg(CharacterId, InventoryListType.Pet, EggSlot, BoboEggItemId, out var result)
                && result != null
                && result.SlotIndex == EggSlot
                && result.EggItemTemplateId == BoboEggItemId
                && result.HatchedItemTemplateId == BoboPetItemId
                && result.PetSerialOrHandle == PetSerial,
                ref failures);

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                connection.Open();
                var row = LoadPetRow(connection);
                Check("pet egg row remains in same slot", row.Exists && row.SlotIndex == EggSlot, ref failures);
                Check("pet egg row changed to hatched pet", row.ItemTemplateId == BoboPetItemId, ref failures);
                Check("hatched pet keeps serial", row.PetSerialOrHandle == PetSerial, ref failures);
                var creatureRow = LoadCreatureRow(connection);
                Check("hatched pet has creature-list row", creatureRow.Exists && creatureRow.CreatureKey == PetSerial, ref failures);
                Check("hatched pet creature-list row starts at level 1", creatureRow.FieldAfterValue == 1, ref failures);
            }

            using (store.BeginScope(CharacterId, AccountId))
            {
                var snapshot = store.LoadCharacterItemListSnapshot();
                var petItem = snapshot.PetItems.FirstOrDefault(x => x.SlotIndex == EggSlot);
                Check("pet item-list update serializes hatched pet",
                    petItem != null
                    && petItem.CreatureItemId == BoboPetItemId
                    && petItem.CreatureSerialOrHandle == PetSerial,
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void SeedPetEgg(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'pet-hatch-selftest', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'pet-hatch-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 7, 0);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 7, @slotIndex, @eggItemId, 'pet',
    0, 0, 0, 0, 0, 0, 0,
    @petSerial, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slotIndex", EggSlot);
                    command.Parameters.AddWithValue("@eggItemId", BoboEggItemId);
                    command.Parameters.AddWithValue("@petSerial", PetSerial);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static (bool Exists, short SlotIndex, int ItemTemplateId, int PetSerialOrHandle) LoadPetRow(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT slot_index, item_template_id, pet_serial_or_handle
FROM character_items
WHERE character_id = @characterId
  AND list_type = 7
  AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", CharacterId);
                command.Parameters.AddWithValue("@slotIndex", EggSlot);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return (false, 0, 0, 0);

                    return (true, (short)reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
                }
            }
        }

        private static (bool Exists, int CreatureKey, int FieldAfterValue) LoadCreatureRow(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT creature_key, field_after_value
FROM character_creatures
WHERE character_id = @characterId
  AND creature_key = @petSerial;";
                command.Parameters.AddWithValue("@characterId", CharacterId);
                command.Parameters.AddWithValue("@petSerial", PetSerial);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return (false, 0, 0);

                    return (true, reader.GetInt32(0), reader.GetInt32(1));
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
