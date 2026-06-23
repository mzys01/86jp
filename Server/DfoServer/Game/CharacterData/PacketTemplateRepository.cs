using DfoServer.Infrastructure;
using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    public sealed class PacketTemplateRepository
    {
        private readonly string _connectionString;

        public PacketTemplateRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public byte[] LoadBody(int characterId, byte command, ushort notiType, int occurrenceIndex)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT body FROM packet_templates WHERE character_id=@cid AND command=@cmd AND noti_type=@nt AND occurrence_index=@oi",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@cmd", (int)command);
                    cmd.Parameters.AddWithValue("@nt", (int)notiType);
                    cmd.Parameters.AddWithValue("@oi", occurrenceIndex);
                    var result = cmd.ExecuteScalar();
                    return result != null && result != DBNull.Value ? (byte[])result : null;
                }
            }
        }

        public void Save(int characterId, byte command, ushort notiType, int occurrenceIndex, byte[] body)
        {
            if (body == null) return;
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    @"INSERT OR REPLACE INTO packet_templates (character_id, command, noti_type, occurrence_index, body, body_length)
                      VALUES (@cid, @cmd, @nt, @oi, @body, @len)", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@cmd", (int)command);
                    cmd.Parameters.AddWithValue("@nt", (int)notiType);
                    cmd.Parameters.AddWithValue("@oi", occurrenceIndex);
                    cmd.Parameters.AddWithValue("@body", body);
                    cmd.Parameters.AddWithValue("@len", body.Length);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
