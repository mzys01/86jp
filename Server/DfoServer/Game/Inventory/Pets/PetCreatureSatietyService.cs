using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public static class PetCreatureSatietyService
    {
        private const int PetCreatureEquipSlot = 24;

        public static PetCreatureSatietyUpdate LoadEquippedCreatureSatiety(
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

                    var foodConsumeRatePercent = ResolveEquippedCreatureFoodConsumeRatePercent(connection, transaction, characterId);

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

        public static byte LoadEquippedCreatureAliveFlag(
            string databasePath,
            string schemaFilePath,
            int characterId)
        {
            var current = LoadEquippedCreatureSatiety(databasePath, schemaFilePath, characterId);
            if (current.CreatureKey <= 0)
                return 0;
            return current.Before <= 0 ? (byte)0 : (byte)1;
        }

        public static PetCreatureSatietyUpdate ApplyDungeonElapsed(
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

                    var foodConsumeRatePercent = ResolveEquippedCreatureFoodConsumeRatePercent(connection, transaction, characterId);
                    var after = CalculateDungeonSatietyAfter(
                        before.Value,
                        elapsedSeconds,
                        foodConsumeRatePercent,
                        clampAliveMinimum: true);
                    if (after != before.Value)
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
UPDATE character_creatures
SET field04 = @after
WHERE character_id = @cid
  AND creature_key = @key;";
                            command.Parameters.AddWithValue("@after", after);
                            command.Parameters.AddWithValue("@cid", characterId);
                            command.Parameters.AddWithValue("@key", creatureKey);
                            command.ExecuteNonQuery();
                        }
                    }

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

        public static PetCreatureSatietyUpdate ApplyTownElapsed(
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

                    var foodConsumeRatePercent = ResolveEquippedCreatureFoodConsumeRatePercent(connection, transaction, characterId);
                    var after = CalculateTownSatietyAfter(before.Value, elapsedSeconds);
                    if (after != before.Value)
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
UPDATE character_creatures
SET field04 = @after
WHERE character_id = @cid
  AND creature_key = @key;";
                            command.Parameters.AddWithValue("@after", after);
                            command.Parameters.AddWithValue("@cid", characterId);
                            command.Parameters.AddWithValue("@key", creatureKey);
                            command.ExecuteNonQuery();
                        }
                    }

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

        public static PetCreatureSatietyUpdate ApplyDungeonDeathIfExpired(
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

                    var foodConsumeRatePercent = ResolveEquippedCreatureFoodConsumeRatePercent(connection, transaction, characterId);
                    var stomach = CalculateDungeonStomachValue(before.Value, elapsedSeconds, foodConsumeRatePercent);
                    var shouldDie = stomach <= 1.0;
                    var after = shouldDie
                        ? 0
                        : CalculateVisibleSatiety(stomach, clampAliveMinimum: true);
                    if (shouldDie && before.Value != 0)
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
UPDATE character_creatures
SET field04 = 0
WHERE character_id = @cid
  AND creature_key = @key;";
                            command.Parameters.AddWithValue("@cid", characterId);
                            command.Parameters.AddWithValue("@key", creatureKey);
                            command.ExecuteNonQuery();
                        }
                    }

                    transaction.Commit();
                    return new PetCreatureSatietyUpdate(
                        characterId,
                        creatureKey,
                        before.Value,
                        shouldDie ? 0 : after,
                        elapsedSeconds,
                        shouldDie ? -before.Value : 0,
                        shouldDie && before.Value != 0,
                        foodConsumeRatePercent);
                }
            }
        }

        public static PetCreatureRevivalUpdate ReviveEquippedCreatureIfDead(
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
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
UPDATE character_creatures
SET field04 = 1
WHERE character_id = @cid
  AND creature_key = @key;";
                            command.Parameters.AddWithValue("@cid", characterId);
                            command.Parameters.AddWithValue("@key", creatureKey);
                            command.ExecuteNonQuery();
                        }
                    }

                    transaction.Commit();
                    return new PetCreatureRevivalUpdate(characterId, creatureKey, before.Value, after, revived);
                }
            }
        }

        private static int CalculateDungeonSatietyAfter(
            int before,
            double elapsedSeconds,
            int foodConsumeRatePercent,
            bool clampAliveMinimum)
        {
            if (before <= 0 || elapsedSeconds <= 0)
                return Math.Max(0, before);

            var stomach = CalculateDungeonStomachValue(before, elapsedSeconds, foodConsumeRatePercent);
            return CalculateVisibleSatiety(stomach, clampAliveMinimum);
        }

        private static double CalculateDungeonStomachValue(int before, double elapsedSeconds, int foodConsumeRatePercent)
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
            return (int)stomach;
        }

        public static double CalculateFoodConsumeMultiplier(int foodConsumeRatePercent)
        {
            var multiplier = 1.0 + foodConsumeRatePercent / 100.0;
            return multiplier <= 0 ? 0.01 : multiplier;
        }

        private static int CalculateTownSatietyAfter(int before, double elapsedSeconds)
        {
            if (elapsedSeconds <= 0)
                return Math.Max(0, Math.Min(100, before));
            if (before >= 100)
                return 100;

            var stomach = before + elapsedSeconds / 360.0;
            if (stomach >= 100)
                return 100;
            if (stomach <= 0)
                return 0;

            return (int)stomach;
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
                return value == null || value == DBNull.Value ? (int?)null : Convert.ToInt32(value);
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
  AND slot IN (25, 26, 27);";
                command.Parameters.AddWithValue("@cid", characterId);

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

        internal static int ResolveEquippedCreatureKey(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var candidates = new List<int>();
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
                command.Parameters.AddWithValue("@slot", PetCreatureEquipSlot);

                var raw = command.ExecuteScalar() as byte[];
                AddCreatureKeyCandidate(candidates, raw, 5, littleEndian: true);
                AddCreatureKeyCandidate(candidates, raw, 5, littleEndian: false);
            }

            foreach (var candidate in candidates)
            {
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
                    command.Parameters.AddWithValue("@key", candidate);
                    if (command.ExecuteScalar() != null)
                        return candidate;
                }
            }

            return 0;
        }

        private static void AddCreatureKeyCandidate(List<int> candidates, byte[] buffer, int offset, bool littleEndian)
        {
            if (buffer == null || buffer.Length < offset + 4)
                return;

            int value;
            if (littleEndian)
            {
                value = buffer[offset]
                    | (buffer[offset + 1] << 8)
                    | (buffer[offset + 2] << 16)
                    | (buffer[offset + 3] << 24);
            }
            else
            {
                value = (buffer[offset] << 24)
                    | (buffer[offset + 1] << 16)
                    | (buffer[offset + 2] << 8)
                    | buffer[offset + 3];
            }

            if (value > 0 && value < 1000000 && !candidates.Contains(value))
                candidates.Add(value);
        }
    }

    public readonly struct PetCreatureSatietyUpdate
    {
        public PetCreatureSatietyUpdate(
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

        public static PetCreatureSatietyUpdate Noop(int characterId, double elapsedSeconds = 0)
            => new PetCreatureSatietyUpdate(characterId, 0, 0, 0, elapsedSeconds, 0, false);
    }

    public readonly struct PetCreatureRevivalUpdate
    {
        public PetCreatureRevivalUpdate(int characterId, int creatureKey, int before, int after, bool revived)
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

        public static PetCreatureRevivalUpdate Noop(int characterId)
            => new PetCreatureRevivalUpdate(characterId, 0, 0, 0, false);
    }
}
