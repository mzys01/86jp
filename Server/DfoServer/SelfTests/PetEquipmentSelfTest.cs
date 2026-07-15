using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class PetEquipmentSelfTest
    {
        private const int AccountId = 163002;
        private const int CharacterId = 163002;
        private const short PetInventorySourceSlot = 48;
        private const short EquippedPetSlot = 24;
        private const int MiniBloodPetItemId = 0x17E69F80;
        private const int PetSerial = 37;
        private const int PetEnchantCardItemId = 920024;
        private const byte PetEnchantUpgradeCount = 3;

        public static int Run()
        {
            Console.WriteLine("=== PET_EQUIPMENT selftest ===");

            var failures = 0;
            Check("sample pet is pet inventory equipment",
                ItemMetadataResolver.IsPetInventoryEquipment(MiniBloodPetItemId),
                ref failures);
            Check("seal creature ACK matches 86 client 19-byte success body",
                BytesEqual(
                    PetSealCreatureAckBuilder.BuildSuccess(new PetCreatureSealResult
                    {
                        CapsuleSlotIndex = 98,
                        CreatureSlotIndex = 14,
                    }),
                    new byte[]
                    {
                        0x01,
                        0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00,
                        0x62, 0x00,
                        0x0E, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                    }),
                ref failures);
            Check("compound item success ACK carries deleted and reward entries",
                BytesEqual(
                    CompoundItemAckBuilder.Build(new CompoundItemRecipeResult
                    {
                        SourceSlotIndex = 106,
                        RequestedCount = 1,
                        DeletedEntries =
                        {
                            new CompoundItemDeletedEntry
                            {
                                ListType = InventoryListType.Main,
                                SlotIndex = 106,
                                Count = 1,
                                ItemTemplateId = 0x0029F420,
                            },
                        },
                        Rewards =
                        {
                            new BoosterRewardResult
                            {
                                ListType = InventoryListType.Main,
                                SlotIndex = 106,
                                ItemTemplateId = 0x0029F42C,
                                StackCount = 1,
                                GrantedCount = 1,
                            },
                        },
                    }),
                    new byte[]
                    {
                        0x01,
                        0x01,
                        0x00, 0x6A, 0x00, 0x01, 0x00, 0x00, 0x00,
                        0x01,
                        0x00, 0x6A, 0x00, 0x2C, 0xF4, 0x29, 0x00, 0x01, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                    }),
                ref failures);
            Check("compound item error ACK is compact failure body",
                BytesEqual(
                    CompoundItemAckBuilder.BuildError(21),
                    new byte[] { 0x00, 0x15 }),
                ref failures);

            var raw = MakeEquipListCodec.BuildEntryFromDisplayFields(
                EquippedPetSlot,
                MiniBloodPetItemId,
                new MakeEquipListCodec.DisplayFields { InstanceValue = PetSerial });
            Check("pet body equipment raw carries creature extra from serial",
                raw.Length >= 28 && BitConverter.ToInt32(raw, 24) == PetSerial,
                ref failures);

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "pet-equipment.db");
            DeleteTempDatabase(dbPath);

            var store = new SqliteInventoryStore(dbPath, ServerPaths.SchemaFilePath);
            SeedPetInventoryPet(dbPath);

            {
                Check("pet body can move from pet inventory into equipped slot 24",
                    store.TryMoveItem(CharacterId, AccountId, new InventoryMoveRequest
                    {
                        SourceListType = InventoryListType.Pet,
                        SourceSlotIndex = PetInventorySourceSlot,
                        SourceInstanceValue = MiniBloodPetItemId,
                        MoveCount = 1,
                        DestinationListType = InventoryListType.Equipment,
                        DestinationSlotIndex = EquippedPetSlot,
                        DestinationInstanceValue = 0,
                    }, out var result)
                    && result != null
                    && result.Mutated
                    && !result.AckError,
                    ref failures);
            }

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                connection.Open();
                var equipped = LoadEquippedEntry(connection);
                Check("equipped slot 24 stores the pet item", equipped.Exists && equipped.ItemId == MiniBloodPetItemId, ref failures);
                Check("equipped slot 24 raw keeps pet serial as instance",
                    equipped.Raw != null && equipped.Raw.Length >= 9 && BitConverter.ToInt32(equipped.Raw, 5) == PetSerial,
                    ref failures);
            }

            var petEnchantExtraJson = SqliteInventoryStore.SetPetCreatureEnchantExtraJson(
                "{}",
                PetEnchantCardItemId,
                PetEnchantUpgradeCount);
            var petEnchantTail = InventoryItemView.ForPet(new SqliteInventoryStore.ItemRecord
            {
                ExtraJson = petEnchantExtraJson,
            }).PetTailData0A;
            Check("pet enchant extra writes pet tail field",
                petEnchantTail.Length > 8
                && BitConverter.ToInt32(petEnchantTail, 4) == PetEnchantCardItemId
                && petEnchantTail[8] == PetEnchantUpgradeCount,
                ref failures);
            SaveCreatureExtraJson(dbPath, petEnchantExtraJson);

            var commonRefresh = store.LoadEquipmentCommonItemForRefresh(CharacterId, EquippedPetSlot);
            Check("equipped pet common refresh carries pet enchant",
                commonRefresh != null
                && commonRefresh.PrefixData0E != null
                && commonRefresh.PrefixData0E.Length >= 5
                && BitConverter.ToInt32(commonRefresh.PrefixData0E, 0) == PetEnchantCardItemId
                && commonRefresh.PrefixData0E[4] == PetEnchantUpgradeCount,
                ref failures);

            store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                connection.Open();
                var equipped = LoadEquippedEntry(connection);
                var parsed = equipped.Raw == null ? null : InvenItem.Parse(equipped.Raw);
                Check("load snapshot repairs equipped pet raw enchant",
                    parsed != null
                    && parsed.EnchantIndex == unchecked((uint)PetEnchantCardItemId)
                    && parsed.EnchantUpgradeCount == PetEnchantUpgradeCount,
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void SeedPetInventoryPet(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'pet-equipment-selftest', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'pet-equipment-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 7, 0);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 7, @slotIndex, @petItemId, 'pet',
    0, 0, 0, 0, 0, 0, 0,
    @petSerial, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slotIndex", PetInventorySourceSlot);
                    command.Parameters.AddWithValue("@petItemId", MiniBloodPetItemId);
                    command.Parameters.AddWithValue("@petSerial", PetSerial);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SaveCreatureExtraJson(string databasePath, string extraJson)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE character_creatures
SET extra_json = @extraJson
WHERE character_id = @characterId
  AND creature_key = @petSerial;";
                    command.Parameters.AddWithValue("@extraJson", extraJson);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@petSerial", PetSerial);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static (bool Exists, int ItemId, byte[] Raw) LoadEquippedEntry(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_id, raw_entry
FROM character_equipped_entries
WHERE character_id = @characterId
  AND slot = @slot;";
                command.Parameters.AddWithValue("@characterId", CharacterId);
                command.Parameters.AddWithValue("@slot", EquippedPetSlot);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return (false, 0, null);

                    return (true, reader.GetInt32(0), (byte[])reader[1]);
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

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (var index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                    return false;
            }
            return true;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok)
                failures++;
        }
    }
}
