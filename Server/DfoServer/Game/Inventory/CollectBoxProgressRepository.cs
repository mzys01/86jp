using System.Collections.Generic;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public sealed class CollectBoxSlotEntry
    {
        public int SlotIndex { get; set; }
        public int ItemId { get; set; }
    }

    // 收集箱槛位存档。宝珠本质是背包道具(character_items)，
    // 这里只维护"哪个 itemId 被摆在哪个收集箱槛位"的状态表。
    // 放入/取出时由 CollectionBoxHandler 联动背包扣减/归还后再调用本类。
    public sealed class CollectBoxProgressRepository
    {
        private readonly string _connectionString;

        public CollectBoxProgressRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public IReadOnlyList<CollectBoxSlotEntry> LoadSlots(int characterId, int boxIndex)
        {
            var list = new List<CollectBoxSlotEntry>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT slot_index, item_id FROM character_collectbox_slots WHERE character_id=@cid AND box_index=@box ORDER BY slot_index",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@box", boxIndex);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            list.Add(new CollectBoxSlotEntry { SlotIndex = r.GetInt32(0), ItemId = r.GetInt32(1) });
                }
            }
            return list;
        }

        public bool HasItem(int characterId, int boxIndex, int itemId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM character_collectbox_slots WHERE character_id=@cid AND box_index=@box AND item_id=@item",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@box", boxIndex);
                    cmd.Parameters.AddWithValue("@item", itemId);
                    return System.Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
        }

        public void PutSlot(int characterId, int boxIndex, int slotIndex, int itemId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    @"INSERT INTO character_collectbox_slots (character_id, box_index, slot_index, item_id)
                      VALUES (@cid, @box, @slot, @item)
                      ON CONFLICT(character_id, box_index, slot_index) DO UPDATE SET item_id=@item",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@box", boxIndex);
                    cmd.Parameters.AddWithValue("@slot", slotIndex);
                    cmd.Parameters.AddWithValue("@item", itemId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public bool RemoveItem(int characterId, int boxIndex, int itemId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "DELETE FROM character_collectbox_slots WHERE character_id=@cid AND box_index=@box AND item_id=@item",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@box", boxIndex);
                    cmd.Parameters.AddWithValue("@item", itemId);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        // 906 取出请求只带 itemId，需反查该宝珠当前存放位置。
        public bool TryFindSlotByItem(int characterId, int itemId, out int boxIndex, out int slotIndex)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT box_index, slot_index FROM character_collectbox_slots WHERE character_id=@cid AND item_id=@item LIMIT 1",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@item", itemId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) { boxIndex = 0; slotIndex = 0; return false; }
                        boxIndex = r.GetInt32(0);
                        slotIndex = r.GetInt32(1);
                        return true;
                    }
                }
            }
        }
    }
}
