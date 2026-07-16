using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal static class PetCreatureSatietyService
    {
        internal static PetCreatureSatietyUpdate LoadEquippedCreatureSatiety(
            string databasePath,
            string schemaFilePath,
            int characterId)
        {
            if (characterId <= 0)
                return PetCreatureSatietyUpdate.Noop(characterId);

            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var creatureKey = ResolveEquippedCreatureKey(connection, transaction, characterId);
                    if (creatureKey <= 0)
                        return PetCreatureSatietyUpdate.Noop(characterId);

                    var satiety = LoadCreatureSatiety(connection, transaction, characterId, creatureKey);
                    if (!satiety.HasValue)
                        return PetCreatureSatietyUpdate.Noop(characterId);

                    var foodConsumeRatePercent = ResolveEquippedCreatureFoodConsumeRatePercent(
                        connection,
                        transaction,
                        characterId);

                    transaction.Commit();
                    return new PetCreatureSatietyUpdate(
                        characterId,
                        creatureKey,
                        satiety.Value,
                        satiety.Value,
                        0,
                        0,
                        false,
                        foodConsumeRatePercent);
                }
            }
        }

        internal static byte LoadEquippedCreatureAliveFlag(
            string databasePath,
            string schemaFilePath,
            int characterId)
        {
            var current = LoadEquippedCreatureSatiety(databasePath, schemaFilePath, characterId);
            if (current.CreatureKey <= 0)
                return 0;

            return current.Before <= 0 ? (byte)0 : (byte)1;
        }

        internal static PetCreatureSatietyUpdate ApplyDungeonElapsed(
            string databasePath,
            string schemaFilePath,
            int characterId,
            DateTime startUtc,
            DateTime endUtc)
        {
            if (characterId <= 0 || startUtc == DateTime.MinValue)
                return PetCreatureSatietyUpdate.Noop(characterId);

            var elapsedSeconds = Math.Max(0, (endUtc - startUtc).TotalSeconds);
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var creatureKey = ResolveEquippedCreatureKey(connection, transaction, characterId);
                    if (creatureKey <= 0)
                        return PetCreatureSatietyUpdate.Noop(characterId, elapsedSeconds);

                    var before = LoadCreatureSatiety(connection, transaction, characterId, creatureKey);
                    if (!before.HasValue)
                        return PetCreatureSatietyUpdate.Noop(characterId, elapsedSeconds);

                    var foodConsumeRatePercent = ResolveEquippedCreatureFoodConsumeRatePercent(
                        connection,
                        transaction,
                        characterId);
                    var after = CalculateDungeonSatietyAfter(
                        before.Value,
                        elapsedSeconds,
                        foodConsumeRatePercent,
                        clampAliveMinimum: true);

                    if (after != before.Value)
                        UpdateCreatureSatiety(connection, transaction, characterId, creatureKey, after);

                    transaction.Commit();
                    return new PetCreatureSatietyUpdate(
                        characterId,
                        creatureKey,
                        before.Value,
                        after,
                        elapsedSeconds,
                        after - before.Value,
                        after != before.Value,
                        foodConsumeRatePercent);
                }
            }
        }

        internal static PetCreatureSatietyUpdate ApplyDungeonDeathIfExpired(
            string databasePath,
            string schemaFilePath,
            int characterId,
            DateTime startUtc,
            DateTime endUtc)
        {
            if (characterId <= 0 || startUtc == DateTime.MinValue)
                return PetCreatureSatietyUpdate.Noop(characterId);

            var elapsedSeconds = Math.Max(0, (endUtc - startUtc).TotalSeconds);
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var creatureKey = ResolveEquippedCreatureKey(connection, transaction, characterId);
                    if (creatureKey <= 0)
                        return PetCreatureSatietyUpdate.Noop(characterId, elapsedSeconds);

                    var before = LoadCreatureSatiety(connection, transaction, characterId, creatureKey);
                    if (!before.HasValue)
                        return PetCreatureSatietyUpdate.Noop(characterId, elapsedSeconds);

                    var foodConsumeRatePercent = ResolveEquippedCreatureFoodConsumeRatePercent(
                        connection,
                        transaction,
                        characterId);
                    var stomach = CalculateDungeonStomachValue(
                        before.Value,
                        elapsedSeconds,
                        foodConsumeRatePercent);
                    var shouldDie = stomach <= 1.0;
                    var after = shouldDie
                        ? 0
                        : CalculateVisibleSatiety(stomach, clampAliveMinimum: true);

                    if (shouldDie && before.Value != 0)
                        UpdateCreatureSatiety(connection, transaction, characterId, creatureKey, 0);

                    transaction.Commit();
                    return new PetCreatureSatietyUpdate(
                        characterId,
                        creatureKey,
                        before.Value,
                        after,
                        elapsedSeconds,
                        shouldDie ? -before.Value : after - before.Value,
                        shouldDie && before.Value != 0,
                        foodConsumeRatePercent);
                }
            }
        }

        internal static PetCreatureSatietyUpdate ApplyTownElapsed(
            string databasePath,
            string schemaFilePath,
            int characterId,
            DateTime startUtc,
            DateTime endUtc)
        {
            if (characterId <= 0 || startUtc == DateTime.MinValue)
                return PetCreatureSatietyUpdate.Noop(characterId);

            var elapsedSeconds = Math.Max(0, (endUtc - startUtc).TotalSeconds);
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var creatureKey = ResolveEquippedCreatureKey(connection, transaction, characterId);
                    if (creatureKey <= 0)
                        return PetCreatureSatietyUpdate.Noop(characterId, elapsedSeconds);

                    var before = LoadCreatureSatiety(connection, transaction, characterId, creatureKey);
                    if (!before.HasValue)
                        return PetCreatureSatietyUpdate.Noop(characterId, elapsedSeconds);

                    var foodConsumeRatePercent = ResolveEquippedCreatureFoodConsumeRatePercent(
                        connection,
                        transaction,
                        characterId);
                    var after = CalculateTownSatietyAfter(before.Value, elapsedSeconds);
                    if (after != before.Value)
                        UpdateCreatureSatiety(connection, transaction, characterId, creatureKey, after);

                    transaction.Commit();
                    return new PetCreatureSatietyUpdate(
                        characterId,
                        creatureKey,
                        before.Value,
                        after,
                        elapsedSeconds,
                        after - before.Value,
                        after != before.Value,
                        foodConsumeRatePercent);
                }
            }
        }

        internal static PetCreatureRevivalUpdate ReviveEquippedCreatureIfDead(
            string databasePath,
            string schemaFilePath,
            int characterId)
        {
            if (characterId <= 0)
                return PetCreatureRevivalUpdate.Noop(characterId);

            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var creatureKey = ResolveEquippedCreatureKey(connection, transaction, characterId);
                    if (creatureKey <= 0)
                        return PetCreatureRevivalUpdate.Noop(characterId);

                    var before = LoadCreatureSatiety(connection, transaction, characterId, creatureKey);
                    if (!before.HasValue)
                        return PetCreatureRevivalUpdate.Noop(characterId);

                    var after = before.Value;
                    var revived = before.Value <= 0;
                    if (revived)
                    {
                        after = 1;
                        UpdateCreatureSatiety(connection, transaction, characterId, creatureKey, after);
                    }

                    transaction.Commit();
                    return new PetCreatureRevivalUpdate(
                        characterId,
                        creatureKey,
                        before.Value,
                        after,
                        revived);
                }
            }
        }

        internal static int ResolveEquippedCreatureKey(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            if (connection == null || characterId <= 0)
                return 0;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT raw_entry
FROM character_equipped_entries
WHERE character_id = @cid
  AND slot = @slot
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@slot", (int)PetInventoryLayout.CreatureEquipSlot);

                var raw = command.ExecuteScalar() as byte[];
                if (raw == null)
                    return 0;

                try
                {
                    var item = InvenItem.Parse(raw);
                    var key = unchecked((int)item.Value);
                    return CreatureKeyExists(connection, transaction, characterId, key) ? key : 0;
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[PetCreatureSatiety] active creature raw parse failed cid={characterId}: {ex.Message}");
                    return 0;
                }
            }
        }

        private static int? LoadCreatureSatiety(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int creatureKey)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT field04
FROM character_creatures
WHERE character_id = @cid
  AND creature_key = @key
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@key", creatureKey);

                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? (int?)null
                    : Math.Max(0, Math.Min(100, Convert.ToInt32(value)));
            }
        }

        private static bool CreatureKeyExists(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int creatureKey)
        {
            if (creatureKey <= 0)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT 1
FROM character_creatures
WHERE character_id = @cid
  AND creature_key = @key
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@key", creatureKey);
                return command.ExecuteScalar() != null;
            }
        }

        private static void UpdateCreatureSatiety(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int creatureKey,
            int satiety)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_creatures
SET field04 = @satiety
WHERE character_id = @cid
  AND creature_key = @key;";
                command.Parameters.AddWithValue("@satiety", Math.Max(0, Math.Min(100, satiety)));
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@key", creatureKey);
                command.ExecuteNonQuery();
            }
        }

        private static int ResolveEquippedCreatureFoodConsumeRatePercent(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var itemIds = new List<int>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_id
FROM character_equipped_entries
WHERE character_id = @cid
  AND slot IN (@redArtifactSlot, @blueArtifactSlot, @greenArtifactSlot);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@redArtifactSlot", (int)PetInventoryLayout.ArtifactRedEquipSlot);
                command.Parameters.AddWithValue("@blueArtifactSlot", (int)PetInventoryLayout.ArtifactBlueEquipSlot);
                command.Parameters.AddWithValue("@greenArtifactSlot", (int)PetInventoryLayout.ArtifactGreenEquipSlot);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var itemId = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                        if (itemId > 0 && !itemIds.Contains(itemId))
                            itemIds.Add(itemId);
                    }
                }
            }

            var total = 0;
            foreach (var itemId in itemIds)
            {
                try
                {
                    if (ItemMetadataResolver.TryLoadEquipmentFile(itemId, out var equipment)
                        && equipment != null)
                    {
                        total += equipment.CreatureFoodConsumeRate;
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[PetCreatureSatiety] food consume rate fallback item=0x{itemId:X8}: {ex.Message}");
                }
            }

            return total;
        }

        private static int CalculateDungeonSatietyAfter(
            int before,
            double elapsedSeconds,
            int foodConsumeRatePercent,
            bool clampAliveMinimum)
        {
            if (before <= 0 || elapsedSeconds <= 0)
                return Math.Max(0, Math.Min(100, before));

            var stomach = CalculateDungeonStomachValue(before, elapsedSeconds, foodConsumeRatePercent);
            return CalculateVisibleSatiety(stomach, clampAliveMinimum);
        }

        private static double CalculateDungeonStomachValue(
            int before,
            double elapsedSeconds,
            int foodConsumeRatePercent)
        {
            if (before <= 0 || elapsedSeconds <= 0)
                return Math.Max(0, before);

            return before - elapsedSeconds / 60.0 * CalculateFoodConsumeMultiplier(foodConsumeRatePercent);
        }

        private static int CalculateVisibleSatiety(double stomach, bool clampAliveMinimum)
        {
            if (stomach <= 0)
                return 0;
            if (clampAliveMinimum && stomach < 1.0)
                return 1;
            return Math.Max(0, Math.Min(100, (int)stomach));
        }

        internal static double CalculateFoodConsumeMultiplier(int foodConsumeRatePercent)
        {
            var multiplier = 1.0 + foodConsumeRatePercent / 100.0;
            return multiplier <= 0 ? 0.01 : multiplier;
        }

        private static int CalculateTownSatietyAfter(int before, double elapsedSeconds)
        {
            before = Math.Max(0, Math.Min(100, before));
            if (elapsedSeconds <= 0 || before >= 100)
                return before;

            var stomach = before + elapsedSeconds / 360.0;
            if (stomach >= 100)
                return 100;
            if (stomach <= 0)
                return 0;

            return (int)stomach;
        }
    }

    internal readonly struct PetCreatureSatietyUpdate
    {
        internal PetCreatureSatietyUpdate(
            int characterId,
            int creatureKey,
            int before,
            int after,
            double elapsedSeconds,
            int satietyDelta,
            bool changed,
            int foodConsumeRatePercent = 0)
        {
            CharacterId = characterId;
            CreatureKey = creatureKey;
            Before = before;
            After = after;
            ElapsedSeconds = elapsedSeconds;
            SatietyDelta = satietyDelta;
            Changed = changed;
            FoodConsumeRatePercent = foodConsumeRatePercent;
        }

        public int CharacterId { get; }
        public int CreatureKey { get; }
        public int Before { get; }
        public int After { get; }
        public double ElapsedSeconds { get; }
        public int SatietyDelta { get; }
        public int ConsumedSatiety => SatietyDelta < 0 ? -SatietyDelta : 0;
        public int RecoveredSatiety => SatietyDelta > 0 ? SatietyDelta : 0;
        public bool Changed { get; }
        public int FoodConsumeRatePercent { get; }
        public double FoodConsumeMultiplier => PetCreatureSatietyService.CalculateFoodConsumeMultiplier(FoodConsumeRatePercent);

        internal static PetCreatureSatietyUpdate Noop(int characterId, double elapsedSeconds = 0)
            => new PetCreatureSatietyUpdate(characterId, 0, 0, 0, elapsedSeconds, 0, false);
    }

    internal readonly struct PetCreatureRevivalUpdate
    {
        internal PetCreatureRevivalUpdate(int characterId, int creatureKey, int before, int after, bool revived)
        {
            CharacterId = characterId;
            CreatureKey = creatureKey;
            Before = before;
            After = after;
            Revived = revived;
        }

        public int CharacterId { get; }
        public int CreatureKey { get; }
        public int Before { get; }
        public int After { get; }
        public bool Revived { get; }

        internal static PetCreatureRevivalUpdate Noop(int characterId)
            => new PetCreatureRevivalUpdate(characterId, 0, 0, 0, false);
    }
}
