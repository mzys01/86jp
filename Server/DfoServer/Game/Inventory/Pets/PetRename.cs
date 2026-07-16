using DfoServer.Game.Names;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        private const int PetCreatureNameMaxBytes = 13;

        public bool TryRenameEquippedPetCreature(
            int characterId,
            int accountId,
            PetCreatureRenameRequest request,
            out PetCreatureRenameResult result)
        {
            result = null;
            if (!IsValidPetCreatureRenameRequest(request, out var failure))
            {
                FileLogger.Log($"  [PetRename] failed: invalid request character={characterId} reason={failure}");
                return false;
            }

            var nameBytes = CopyPetCreatureNameBytes(request.NameBytes);
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var equippedCreature = LoadPetCreatureEquippedEntry(connection, transaction, characterId);
                    var creatureSerial = equippedCreature.HasValue ? equippedCreature.Value.Serial : 0;
                    var petItemTemplateId = equippedCreature.HasValue
                        ? equippedCreature.Value.ItemId
                        : LoadEquippedCreatureItemId(connection, transaction, characterId);
                    if (creatureSerial <= 0 || petItemTemplateId <= 0 || !IsCreatureItem(petItemTemplateId))
                    {
                        FileLogger.Log($"  [PetRename] failed: no valid equipped pet character={characterId} serial=0x{creatureSerial:X8} item=0x{petItemTemplateId:X8}");
                        return false;
                    }

                    var source = _db.LoadItemRecord(
                        connection,
                        transaction,
                        characterId,
                        MapToDbListType(request.SourceListType),
                        request.SourceSlotIndex);
                    if (source == null || !IsPetConsumableRecord(source))
                    {
                        FileLogger.Log($"  [PetRename] failed: source missing or not pet consumable list={request.SourceListType} slot={request.SourceSlotIndex}");
                        return false;
                    }

                    UpdateCreatureName(connection, transaction, characterId, creatureSerial, nameBytes);
                    var remaining = ConsumePetRenameSourceItem(connection, transaction, source);
                    transaction.Commit();

                    result = new PetCreatureRenameResult
                    {
                        SourceListType = request.SourceListType,
                        SourceSlotIndex = request.SourceSlotIndex,
                        PetItemTemplateId = petItemTemplateId,
                        CreatureSerial = creatureSerial,
                        NameBytes = nameBytes,
                        SourceItemConsumed = true,
                        SourceRemainingCount = remaining,
                    };
                    FileLogger.Log($"  [PetRename] renamed pet serial=0x{creatureSerial:X8} item=0x{petItemTemplateId:X8} source=({request.SourceListType},{request.SourceSlotIndex}) remain={remaining}");
                    return true;
                }
            }
        }

        private static bool IsValidPetCreatureRenameRequest(
            PetCreatureRenameRequest request,
            out NameInputValidationFailure failure)
        {
            failure = NameInputValidationFailure.None;
            if (request == null)
            {
                failure = NameInputValidationFailure.Null;
                return false;
            }

            if (request.SourceListType != InventoryListType.Pet)
                return false;

            if (request.SourceSlotIndex < PetConsumableSlotStart || request.SourceSlotIndex > PetConsumableSlotEnd)
                return false;

            return NameInputValidator.TryValidateRawName(
                request.NameBytes,
                minBytes: 0,
                maxBytes: PetCreatureNameMaxBytes,
                out _,
                out failure);
        }

        private static byte[] CopyPetCreatureNameBytes(byte[] nameBytes)
        {
            if (nameBytes == null || nameBytes.Length == 0)
                return Array.Empty<byte>();

            var copy = new byte[Math.Min(nameBytes.Length, PetCreatureNameMaxBytes)];
            Buffer.BlockCopy(nameBytes, 0, copy, 0, copy.Length);
            return copy;
        }

        private static int LoadEquippedCreatureItemId(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_id
FROM character_equipped_entries
WHERE character_id = @characterId
  AND slot = @slot
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@slot", (int)PetCreatureEquipSlot);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
            }
        }

        private static void UpdateCreatureName(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int creatureSerial,
            byte[] nameBytes)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_creatures
SET creature_text = @name
WHERE character_id = @characterId
  AND creature_key = @creatureSerial;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@creatureSerial", creatureSerial);
                command.Parameters.AddWithValue("@name", nameBytes != null && nameBytes.Length > 0
                    ? (object)nameBytes
                    : DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        private int ConsumePetRenameSourceItem(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ItemRecord source)
        {
            var currentCount = GetStackedRecordCount(source);
            if (currentCount > 1)
            {
                var remaining = currentCount - 1;
                _db.UpdatePetStackCount(connection, transaction, source.ItemUid, remaining);
                return remaining;
            }

            _db.DeleteItem(connection, transaction, source.ItemUid);
            return 0;
        }
    }
}
