using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        private static Dictionary<int, string> LoadPetCreatureExtraJsonMap(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var result = new Dictionary<int, string>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT creature_key, extra_json
FROM character_creatures
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", characterId);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var serial = reader.GetInt32(0);
                        if (serial > 0)
                            result[serial] = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
                    }
                }
            }

            return result;
        }

        private static string ResolvePetCreatureInstanceExtraJson(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int petSerial,
            string candidateExtraJson)
        {
            var candidate = NormalizePetCreatureExtraJson(candidateExtraJson);
            var stored = LoadPetCreatureExtraJson(connection, transaction, characterId, petSerial);
            if (HasPetCreatureProtocolTail(stored))
                return stored;

            if (HasPetCreatureProtocolTail(candidate))
            {
                UpsertPetCreatureExtraJson(connection, transaction, characterId, petSerial, candidate);
                return candidate;
            }

            return candidate;
        }

        private static string MergePetCreatureInstanceExtraJsonForRead(
            string storedExtraJson,
            string candidateExtraJson)
        {
            var stored = NormalizePetCreatureExtraJson(storedExtraJson);
            return HasPetCreatureProtocolTail(stored)
                ? stored
                : NormalizePetCreatureExtraJson(candidateExtraJson);
        }

        private static string LoadPetCreatureExtraJson(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int petSerial)
        {
            if (petSerial <= 0)
                return CreateDefaultPetExtraJson();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT extra_json
FROM character_creatures
WHERE character_id = @cid
  AND creature_key = @serial
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@serial", petSerial);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? CreateDefaultPetExtraJson()
                    : NormalizePetCreatureExtraJson(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
        }

        private static void UpsertPetCreatureExtraJson(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int petSerial,
            string extraJson)
        {
            if (petSerial <= 0)
                return;

            var normalized = NormalizePetCreatureExtraJson(extraJson);
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = @"
UPDATE character_creatures
SET extra_json = @extra
WHERE character_id = @cid
  AND creature_key = @serial;";
                update.Parameters.AddWithValue("@extra", normalized);
                update.Parameters.AddWithValue("@cid", characterId);
                update.Parameters.AddWithValue("@serial", petSerial);
                if (update.ExecuteNonQuery() > 0)
                    return;
            }

            EnsureCreatureListEntry(
                connection,
                transaction,
                characterId,
                petSerial,
                new CreatureDefaults(1, Array.Empty<byte>()));

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = @"
UPDATE character_creatures
SET extra_json = @extra
WHERE character_id = @cid
  AND creature_key = @serial;";
                update.Parameters.AddWithValue("@extra", normalized);
                update.Parameters.AddWithValue("@cid", characterId);
                update.Parameters.AddWithValue("@serial", petSerial);
                update.ExecuteNonQuery();
            }
        }

        internal static string SetPetCreatureEnchantExtraJson(
            string extraJson,
            int enchantCardItemId,
            byte enchantUpgradeCount)
        {
            var view = PetCreatureExtraView.Parse(extraJson);
            view.SetEnchant(enchantCardItemId, enchantUpgradeCount);
            return view.ToJsonString();
        }

        internal static void PersistPetCreatureExtraJson(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int petSerial,
            string extraJson)
        {
            UpsertPetCreatureExtraJson(connection, transaction, characterId, petSerial, extraJson);
            SyncEquippedPetCreatureExtraRaw(connection, transaction, characterId, petSerial, extraJson);
        }

        internal static void RepairEquippedPetCreatureExtraRaw(
            string databasePath,
            string schemaFilePath,
            int characterId)
        {
            if (characterId <= 0)
                return;

            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    RepairEquippedPetCreatureExtraRaw(connection, transaction, characterId);
                    transaction.Commit();
                }
            }
        }

        internal static void RepairEquippedPetCreatureExtraRaw(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            byte[] raw = null;
            var itemId = 0;
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = @"
SELECT item_id, raw_entry
FROM character_equipped_entries
WHERE character_id = @cid
  AND slot = @slot
LIMIT 1;";
                select.Parameters.AddWithValue("@cid", characterId);
                select.Parameters.AddWithValue("@slot", PetCreatureEquipSlot);
                using (var reader = select.ExecuteReader())
                {
                    if (!reader.Read())
                        return;

                    itemId = reader.GetInt32(0);
                    raw = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);
                }
            }

            if (!IsCreatureItem(itemId))
                return;

            var petSerial = ResolvePetCreatureSerialFromEquippedRaw(raw);
            if (petSerial <= 0)
                return;

            var extraJson = LoadPetCreatureExtraJson(connection, transaction, characterId, petSerial);
            SyncEquippedPetCreatureExtraRaw(connection, transaction, characterId, petSerial, extraJson);
        }

        private static string BuildInitializedPetCreatureSealExtraJson(byte remainUseCount)
        {
            var view = PetCreatureExtraView.Parse(CreateDefaultPetExtraJson());
            view.InitializeSealRemainUseCount(remainUseCount);
            return view.ToJsonString();
        }

        private static bool TryResolvePetCreatureSealRemainUseCount(string extraJson, out byte remainUseCount)
        {
            return PetCreatureExtraView.Parse(extraJson).TryGetSealRemainUseCount(out remainUseCount);
        }

        private static void ApplyPetCreatureExtraToCommonPrefix(byte[] commonPrefixData0E, string petExtraJson)
        {
            PetCreatureExtraView.Parse(petExtraJson).ApplyEnchantToCommonPrefix(commonPrefixData0E);
        }

        private static void ApplyPetCreatureExtraToCommonTail(byte[] commonTailData2F, string petExtraJson)
        {
            PetCreatureExtraView.Parse(petExtraJson).ApplySealFieldsToCommonTail(commonTailData2F);
        }

        private static string NormalizePetCreatureExtraJson(string extraJson)
        {
            return PetCreatureExtraView.Parse(extraJson).ToJsonString();
        }

        private static bool HasPetCreatureProtocolTail(string extraJson)
        {
            return PetCreatureExtraView.Parse(extraJson).HasProtocolTail();
        }

        private static void SyncEquippedPetCreatureExtraRaw(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int petSerial,
            string extraJson)
        {
            if (petSerial <= 0)
                return;

            if (!TryResolvePetCreatureEnchant(extraJson, out var enchantCardItemId, out var enchantUpgradeCount))
                return;

            byte[] raw = null;
            var itemId = 0;
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = @"
SELECT item_id, raw_entry
FROM character_equipped_entries
WHERE character_id = @cid
  AND slot = @slot
LIMIT 1;";
                select.Parameters.AddWithValue("@cid", characterId);
                select.Parameters.AddWithValue("@slot", PetCreatureEquipSlot);
                using (var reader = select.ExecuteReader())
                {
                    if (!reader.Read())
                        return;

                    itemId = reader.GetInt32(0);
                    raw = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);
                }
            }

            if (!IsCreatureItem(itemId) || ResolvePetCreatureSerialFromEquippedRaw(raw) != petSerial)
                return;

            byte[] updatedRaw;
            try
            {
                var item = InvenItem.Parse(raw);
                item.EnchantIndex = enchantCardItemId;
                item.EnchantUpgradeCount = enchantUpgradeCount;
                updatedRaw = item.ToBytes();
            }
            catch
            {
                var fields = MakeEquipListCodec.ParseDisplayFields(raw);
                fields.Enchant = enchantCardItemId;
                fields.EnchantUpgradeCount = enchantUpgradeCount;
                updatedRaw = MakeEquipListCodec.BuildEntryFromDisplayFields(PetCreatureEquipSlot, itemId, fields);
            }

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = @"
UPDATE character_equipped_entries
SET raw_entry = @raw
WHERE character_id = @cid
  AND slot = @slot
  AND item_id = @itemId;";
                update.Parameters.AddWithValue("@raw", updatedRaw);
                update.Parameters.AddWithValue("@cid", characterId);
                update.Parameters.AddWithValue("@slot", PetCreatureEquipSlot);
                update.Parameters.AddWithValue("@itemId", itemId);
                update.ExecuteNonQuery();
            }
        }

        private static bool TryResolvePetCreatureEnchant(
            string extraJson,
            out uint enchantCardItemId,
            out byte enchantUpgradeCount)
        {
            return PetCreatureExtraView.Parse(extraJson).TryGetEnchant(out enchantCardItemId, out enchantUpgradeCount);
        }

        private static int ResolvePetCreatureSerialFromEquippedRaw(byte[] raw)
        {
            if (raw == null)
                return 0;

            try
            {
                var fields = MakeEquipListCodec.ParseDisplayFields(raw);
                var serial = unchecked((int)fields.InstanceValue);
                if (serial > 0)
                    return serial;
            }
            catch
            {
            }

            return raw.Length >= 9 ? BitConverter.ToInt32(raw, 5) : 0;
        }

    }
}
