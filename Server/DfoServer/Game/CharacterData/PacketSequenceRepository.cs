using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    public sealed class PacketSequenceRepository
    {
        private readonly string _connectionString;

        public PacketSequenceRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public List<SelectCharacterPacketTemplate> Load(int characterId)
        {
            var list = new List<SelectCharacterPacketTemplate>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT command, noti_type, kind, item_list_type, occurrence_index FROM packet_sequence WHERE character_id=@cid ORDER BY seq_index",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            list.Add(new SelectCharacterPacketTemplate
                            {
                                Command = (byte)r.GetInt32(0),
                                Type = (ushort)r.GetInt32(1),
                                Kind = (SelectCharacterPacketTemplateKind)r.GetInt32(2),
                                ItemListType = r.GetInt32(3) >= 0 ? (InventoryListType)r.GetInt32(3) : InventoryListType.Main,
                                OccurrenceIndex = r.GetInt32(4),
                            });
                        }
                    }
                }
            }
            return list;
        }

        public void Save(int characterId, List<SelectCharacterPacketTemplate> templates)
        {
            if (templates == null || templates.Count == 0) return;
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var del = new SqliteCommand("DELETE FROM packet_sequence WHERE character_id=@cid", conn, tx))
                    {
                        del.Parameters.AddWithValue("@cid", characterId);
                        del.ExecuteNonQuery();
                    }
                    for (int i = 0; i < templates.Count; i++)
                    {
                        var t = templates[i];
                        using (var cmd = new SqliteCommand(
                            @"INSERT INTO packet_sequence (character_id, seq_index, command, noti_type, kind, item_list_type, occurrence_index)
                              VALUES (@cid, @idx, @cmd, @nt, @kind, @ilt, @oi)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@idx", i);
                            cmd.Parameters.AddWithValue("@cmd", (int)t.Command);
                            cmd.Parameters.AddWithValue("@nt", (int)t.Type);
                            cmd.Parameters.AddWithValue("@kind", (int)t.Kind);
                            cmd.Parameters.AddWithValue("@ilt", t.Kind == SelectCharacterPacketTemplateKind.ItemList ? (int)t.ItemListType : -1);
                            cmd.Parameters.AddWithValue("@oi", t.OccurrenceIndex);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        public bool HasSequence(int characterId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM packet_sequence WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    return System.Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
        }
    }
}
