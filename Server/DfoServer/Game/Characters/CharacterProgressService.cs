using DfoServer.Game.CharacterData;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Characters
{
    public static class CharacterProgressService
    {
        public static bool PersistLevelAndExp(int characterId, byte level, uint exp)
        {
            return PersistLevelAndExp(
                characterId,
                level,
                exp,
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath);
        }

        public static bool PersistLevelAndExp(
            int characterId,
            byte level,
            uint exp,
            string databasePath,
            string schemaFilePath)
        {
            var characterRepository = new SqliteCharacterRepository(databasePath, schemaFilePath);
            characterRepository.UpdateLevelAndExp(characterId, level, exp);

            var record = characterRepository.GetById(characterId);
            if (record == null)
                return false;

            CharacterStatComputer.DecodeGrowType(record.GrowType, out int firstGrow, out int secondGrow);
            var combatStats = CharacterStatComputer.BuildAdditionalInfo(record.Job, level, firstGrow, secondGrow);
            return new SqliteSubtype1Repository(databasePath, schemaFilePath)
                .UpdateCombatStats(characterId, combatStats) > 0;
        }

        public static bool PersistLevelAndExp(
            string connectionString,
            int characterId,
            byte level,
            uint exp)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("connectionString is empty", nameof(connectionString));

            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                return PersistLevelAndExp(conn, characterId, level, exp);
            }
        }

        private static bool PersistLevelAndExp(
            SqliteConnection conn,
            int characterId,
            byte level,
            uint exp)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE characters
SET level = @lvl, exp = @exp, updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@lvl", (int)level);
                cmd.Parameters.AddWithValue("@exp", (long)exp);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }

            byte job;
            byte growType;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT job, grow_type FROM characters WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    job = (byte)reader.GetInt32(0);
                    growType = (byte)reader.GetInt32(1);
                }
            }

            CharacterStatComputer.DecodeGrowType(growType, out int firstGrow, out int secondGrow);
            var combatStats = CharacterStatComputer.BuildAdditionalInfo(job, level, firstGrow, secondGrow);
            return SqliteSubtype1Repository.UpdateCombatStatsOnConnection(conn, characterId, combatStats) > 0;
        }
    }
}
