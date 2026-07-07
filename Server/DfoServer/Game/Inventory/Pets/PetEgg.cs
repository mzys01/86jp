using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryHatchCreatureEgg(int characterId, InventoryListType listType, short slotIndex, int expectedItemTemplateId, out CreatureHatchResult result)
        {
            result = null;

            var dbListType = MapToDbListType(listType);
            if (dbListType != InventoryListType.Pet)
                return false;

            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var source = _db.LoadItemRecord(connection, transaction, characterId, dbListType, slotIndex);
                    if (source == null || source.ItemKind != "pet")
                        return false;

                    if (expectedItemTemplateId > 0 && source.ItemTemplateId != expectedItemTemplateId)
                        return false;

                    if (!CreatureEggResolver.TryResolveHatchedCreatureItemId(source.ItemTemplateId, out var hatchedItemTemplateId))
                        return false;

                    var petSerial = source.PetSerialOrHandle > 0
                        ? source.PetSerialOrHandle
                        : _db.NextPetSerialOrHandle(connection, transaction, characterId);

                    _db.InsertCharacterItem(
                        connection,
                        transaction,
                        characterId,
                        InventoryListType.Pet,
                        source.SlotIndex,
                        hatchedItemTemplateId,
                        "pet",
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        petSerial,
                        "{}");

                    UpsertCreatureListEntry(connection, transaction, characterId, petSerial);

                    _auditLogger.WriteAuditLog(
                        connection,
                        transaction,
                        characterId,
                        "hatch_creature",
                        source,
                        InventoryListType.Pet,
                        source.SlotIndex,
                        0);

                    transaction.Commit();

                    result = new CreatureHatchResult
                    {
                        ListType = InventoryListType.Pet,
                        SlotIndex = source.SlotIndex,
                        EggItemTemplateId = source.ItemTemplateId,
                        HatchedItemTemplateId = hatchedItemTemplateId,
                        PetSerialOrHandle = petSerial,
                    };
                    return true;
                }
            }
        }

        private static void UpsertCreatureListEntry(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int petSerial)
        {
            using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = @"
SELECT COUNT(1)
FROM character_creatures
WHERE character_id = @characterId
  AND creature_key = @petSerial;";
                exists.Parameters.AddWithValue("@characterId", characterId);
                exists.Parameters.AddWithValue("@petSerial", petSerial);
                if (System.Convert.ToInt32(exists.ExecuteScalar()) > 0)
                    return;
            }

            int sortOrder;
            using (var order = connection.CreateCommand())
            {
                order.Transaction = transaction;
                order.CommandText = @"
SELECT COALESCE(MAX(sort_order), -1) + 1
FROM character_creatures
WHERE character_id = @characterId;";
                order.Parameters.AddWithValue("@characterId", characterId);
                sortOrder = System.Convert.ToInt32(order.ExecuteScalar());
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = @"
INSERT INTO character_creatures (
    character_id, sort_order, creature_key, field04, mode_flag, progress_value,
    mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag)
VALUES (
    @characterId, @sortOrder, @petSerial, 100, 0, 0,
    0, 0, 1, NULL, 0);";
                insert.Parameters.AddWithValue("@characterId", characterId);
                insert.Parameters.AddWithValue("@sortOrder", sortOrder);
                insert.Parameters.AddWithValue("@petSerial", petSerial);
                insert.ExecuteNonQuery();
            }
        }
    }
}
