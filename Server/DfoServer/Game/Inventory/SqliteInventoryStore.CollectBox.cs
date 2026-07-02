using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryRemoveItemByTemplateId(int characterId, int itemTemplateId, out short slotIndex, out InventoryMutationResult result)
        {
            slotIndex = -1;
            result = null;
            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT slot_index FROM character_items WHERE character_id = @cid AND list_type = 0 AND item_template_id = @tid LIMIT 1",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@tid", itemTemplateId);
                    var found = cmd.ExecuteScalar();
                    if (found == null)
                        return false;
                    slotIndex = Convert.ToInt16(found);
                }
            }
            return TryDeleteItem(InventoryListType.Main, slotIndex, 1, out result);
        }

        public bool TryPickupItem(int characterId, int accountId, int itemTemplateId, int stackCount, out short assignedSlot, out int newStackCount)
        {
            newStackCount = stackCount;
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var result = TryPickupItemCore(connection, transaction,
                        characterId, accountId,
                        itemTemplateId, stackCount, out assignedSlot);
                    if (result)
                    {
                        using (var cmd = new SqliteCommand(
                            "SELECT stack_count FROM character_items WHERE character_id=@cid AND list_type=0 AND slot_index=@slot LIMIT 1",
                            connection, transaction))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@slot", assignedSlot);
                            var val = cmd.ExecuteScalar();
                            if (val != null) newStackCount = Convert.ToInt32(val);
                        }
                        transaction.Commit();
                    }
                    return result;
                }
            }
        }
    }
}
