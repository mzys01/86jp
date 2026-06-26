using DfoServer.Game.SelectCharacter;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    internal sealed class GlobalStateRepository
    {
        private readonly string _connectionString;

        internal GlobalStateRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        internal byte[] LoadGlobalRawPacket(int notiType)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT packet_body FROM global_raw_packets WHERE noti_type = @nt", conn))
                {
                    cmd.Parameters.AddWithValue("@nt", notiType);
                    var result = cmd.ExecuteScalar();
                    return result == null || result == DBNull.Value ? null : (byte[])result;
                }
            }
        }

        internal byte[] LoadServerEventPhaseBitmap()
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT event_phase_bitmap FROM global_server_event_phase WHERE id = 1", conn))
                {
                    var result = cmd.ExecuteScalar();
                    return result == null || result == DBNull.Value ? null : (byte[])result;
                }
            }
        }

        internal void SeedRawPacketsFromTemplates(int characterId, List<SelectCharacterPacketTemplate> templates)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();

                using (var chk = new SqliteCommand("SELECT COUNT(*) FROM character_init_bodies WHERE character_id = @cid", conn))
                {
                    chk.Parameters.AddWithValue("@cid", characterId);
                    if (Convert.ToInt32(chk.ExecuteScalar()) > 0)
                        return;
                }

                using (var tx = conn.BeginTransaction())
                {
                    foreach (var t in templates)
                    {
                        if (t.PacketBytes == null || t.PacketBytes.Length == 0)
                            continue;

                        var headerLen = 15;
                        if (t.PacketBytes.Length <= headerLen)
                            continue;

                        var body = new byte[t.PacketBytes.Length - headerLen];
                        Buffer.BlockCopy(t.PacketBytes, headerLen, body, 0, body.Length);

                        if (t.Command == 0x00 && t.Type == 0x0187)
                        {
                            if (body.Length < 4)
                                continue;
                            var bitmap = new byte[body.Length - 4];
                            Buffer.BlockCopy(body, 4, bitmap, 0, bitmap.Length);
                            using (var cmd = new SqliteCommand("INSERT OR IGNORE INTO global_server_event_phase (id, event_phase_bitmap) VALUES (1, @b)", conn, tx))
                            {
                                cmd.Parameters.AddWithValue("@b", bitmap);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        else if (t.Command == 0x01 && t.Type == 0x0312)
                        {
                            using (var cmd = new SqliteCommand("INSERT OR IGNORE INTO global_raw_packets (noti_type, packet_body) VALUES (@nt, @body)", conn, tx))
                            {
                                cmd.Parameters.AddWithValue("@nt", 0x10312);
                                cmd.Parameters.AddWithValue("@body", body);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    const int hdrLen = 15;
                    var initBodyTypes = new System.Collections.Generic.HashSet<int> { 0x0035, 0x0077, 0x0111, 0x019F, 0x015F, 0x0381, 0x0357, 0x03D8 };
                    foreach (var t in templates)
                    {
                        if (t.Command != 0x00 || !initBodyTypes.Contains(t.Type))
                            continue;
                        if (t.PacketBytes == null || t.PacketBytes.Length <= hdrLen)
                            continue;
                        var b = new byte[t.PacketBytes.Length - hdrLen];
                        Buffer.BlockCopy(t.PacketBytes, hdrLen, b, 0, b.Length);
                        using (var cmd = new SqliteCommand(
                            "INSERT OR IGNORE INTO character_init_bodies(character_id, noti_type, occurrence_index, body) VALUES(@cid, @nt, @oi, @b)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@nt", t.Type);
                            cmd.Parameters.AddWithValue("@oi", t.OccurrenceIndex);
                            cmd.Parameters.AddWithValue("@b", b);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }
    }
}
