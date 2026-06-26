using DfoServer.Game.SelectCharacter;
using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    internal sealed class CharacterItemLockRepository
    {
        private readonly string _connectionString;

        internal CharacterItemLockRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        internal ItemLockListSnapshot LoadItemLocks(int characterId)
        {
            var snapshot = new ItemLockListSnapshot();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT type_or_list, item_key_or_slot, state, extra_value FROM character_item_locks WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var entry = new ItemLockEntrySnapshot
                            {
                                TypeOrList = (byte)reader.GetInt32(0),
                                ItemKeyOrSlot = (ushort)reader.GetInt32(1),
                                State = (byte)reader.GetInt32(2),
                            };
                            if (!reader.IsDBNull(3))
                            {
                                entry.ExtraValue = reader.GetInt32(3);
                                entry.HasExtraValue = true;
                            }
                            snapshot.Entries.Add(entry);
                        }
                    }
                }
            }
            return snapshot;
        }

        internal void SaveItemLocks(int characterId, ItemLockListSnapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_item_locks WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    for (int i = 0; i < snapshot.Entries.Count; i++)
                    {
                        var e = snapshot.Entries[i];
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_item_locks (character_id, sort_order, type_or_list, item_key_or_slot, state, extra_value) VALUES (@cid, @ord, @t, @k, @s, @ev)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@t", (int)e.TypeOrList);
                            cmd.Parameters.AddWithValue("@k", (int)e.ItemKeyOrSlot);
                            cmd.Parameters.AddWithValue("@s", (int)e.State);
                            cmd.Parameters.AddWithValue("@ev", e.HasExtraValue ? (object)e.ExtraValue : DBNull.Value);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }
    }
}
