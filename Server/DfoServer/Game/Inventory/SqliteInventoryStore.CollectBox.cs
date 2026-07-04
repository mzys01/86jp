using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        // 查槽位+删除在同一连接同一事务(旧版分两个连接, 中间有竞态窗口)。
        // alsoInSameTransaction: 与删除同事务执行的附加写入(如收集箱进度), 一起提交/回滚。
        public bool TryRemoveItemByTemplateId(int characterId, int accountId, int itemTemplateId, out short slotIndex, out InventoryMutationResult result,
            Action<SqliteConnection, SqliteTransaction> alsoInSameTransaction = null)
        {
            slotIndex = -1;
            result = null;
            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand(
                        "SELECT slot_index FROM character_items WHERE character_id = @cid AND list_type = 0 AND item_template_id = @tid LIMIT 1",
                        conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@tid", itemTemplateId);
                        var found = cmd.ExecuteScalar();
                        if (found == null)
                            return false;
                        slotIndex = Convert.ToInt16(found);
                    }

                    if (!TryDeleteItemCore(conn, tx, characterId, InventoryListType.Main, MapToDbListType(InventoryListType.Main), slotIndex, 1, out result))
                        return false;

                    alsoInSameTransaction?.Invoke(conn, tx);
                    tx.Commit();
                    return true;
                }
            }
        }

        public bool TryPickupItem(int characterId, int accountId, int itemTemplateId, int stackCount, out short assignedSlot, out int newStackCount,
            Action<SqliteConnection, SqliteTransaction> alsoInSameTransaction = null)
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
                        alsoInSameTransaction?.Invoke(connection, transaction);
                        transaction.Commit();
                    }
                    return result;
                }
            }
        }
    }
}
