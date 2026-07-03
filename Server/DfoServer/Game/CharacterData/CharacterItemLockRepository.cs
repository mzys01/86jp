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
                    @"
SELECT inventory_list_type, slot, state, remaining_seconds
FROM (
    SELECT ci.list_type AS inventory_list_type,
           ci.slot_index AS slot,
           l.state AS state,
           l.remaining_seconds AS remaining_seconds,
           l.equipment_lock_id AS equipment_lock_id
    FROM character_item_locks l
    JOIN character_items ci
      ON ci.character_id = l.character_id
     AND ci.equipment_lock_id = l.equipment_lock_id
    WHERE l.character_id = @cid
      AND ci.owner_scope = 'character'
      AND ci.equipment_lock_id > 0
    UNION ALL
    SELECT 3 AS inventory_list_type,
           e.slot AS slot,
           l.state AS state,
           l.remaining_seconds AS remaining_seconds,
           l.equipment_lock_id AS equipment_lock_id
    FROM character_item_locks l
    JOIN character_equipped_entries e
      ON e.character_id = l.character_id
     AND e.equipment_lock_id = l.equipment_lock_id
    WHERE l.character_id = @cid
      AND e.equipment_lock_id > 0
    UNION ALL
    SELECT l.inventory_list_type AS inventory_list_type,
           l.slot AS slot,
           l.state AS state,
           l.remaining_seconds AS remaining_seconds,
           l.equipment_lock_id AS equipment_lock_id
    FROM character_item_locks l
    WHERE l.character_id = @cid
      AND l.equipment_lock_id > 0
      AND l.inventory_list_type >= 19
      AND l.inventory_list_type <= 23
)
ORDER BY equipment_lock_id;", conn))
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
                            @"INSERT INTO character_item_locks (
                                character_id, equipment_lock_id, inventory_list_type, slot, state, remaining_seconds)
                              VALUES (@cid, @lockId, @t, @k, @s, @ev)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@lockId", i + 1);
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
