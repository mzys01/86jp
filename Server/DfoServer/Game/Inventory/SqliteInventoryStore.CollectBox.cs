using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        // 按模板ID在主背包找到一个槛位并扣除1个，供收集箱放入宝珠使用。
        // 调用方需用返回的 result 发 0x0012(DELETE_ITEM ACK) 同步客户端背包UI。
        public bool TryRemoveItemByTemplateId(int itemTemplateId, out short slotIndex, out InventoryMutationResult result)
        {
            slotIndex = -1;
            result = null;
            using (var conn = new SqliteConnection(_context.ConnectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT slot_index FROM character_items WHERE character_id = @cid AND list_type = 0 AND item_template_id = @tid LIMIT 1",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
                    cmd.Parameters.AddWithValue("@tid", itemTemplateId);
                    var found = cmd.ExecuteScalar();
                    if (found == null)
                        return false;
                    slotIndex = Convert.ToInt16(found);
                }
            }
            return TryDeleteItem(InventoryListType.Main, slotIndex, 1, out result);
        }

        // TryPickupItem 重载，额外返回 newStackCount 供收集箱取出宝珠时构造 NOTI 14 用。
        public bool TryPickupItem(int itemTemplateId, int stackCount, out short assignedSlot, out int newStackCount)
        {
            newStackCount = stackCount;
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var result = TryPickupItemCore(connection, transaction,
                    _context.CharacterId, _context.AccountId,
                    itemTemplateId, stackCount, out assignedSlot);
                if (result)
                {
                    // 读取放入后的实际数量（堆叠道具可能合并到已有格子）
                    using (var cmd = new SqliteCommand(
                        "SELECT stack_count FROM character_items WHERE character_id=@cid AND list_type=0 AND slot_index=@slot LIMIT 1",
                        connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@cid", _context.CharacterId);
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
