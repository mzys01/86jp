using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public static class CurrencyService
    {
        // ── Cube Fragment (晶块) ──────────────────────────────
        // 6 种小晶块是账号共享、固定 slot 的物品。
        // item_id → (accounts 列名, 固定 slot)
        private static readonly Dictionary<int, (string ColumnName, int Slot)> CubeFragmentMap = new Dictionary<int, (string, int)>
        {
            { 3033, ("cube_black", 354) },
            { 3034, ("cube_white", 355) },
            { 3035, ("cube_red",   356) },
            { 3036, ("cube_blue",  357) },
            { 3037, ("cube_clear", 358) },
            { 3262, ("cube_gold",  359) },
        };

        // 晶块固定 slot 范围 (FindEmptySlot 保护用)
        public const int CubeFragmentSlotStart = 354;
        public const int CubeFragmentSlotEnd = 359;

        public static bool IsCubeFragment(int itemId) => CubeFragmentMap.ContainsKey(itemId);

        public static int GetCubeFragmentSlot(int itemId)
        {
            if (CubeFragmentMap.TryGetValue(itemId, out var entry))
                return entry.Slot;
            return -1;
        }

        public static int GetCubeFragmentItemIdFromSlot(int slot)
        {
            foreach (var kv in CubeFragmentMap)
            {
                if (kv.Value.Slot == slot)
                    return kv.Key;
            }
            return -1;
        }

        public static bool IsCubeFragmentSlot(int slot)
        {
            return slot >= CubeFragmentSlotStart && slot <= CubeFragmentSlotEnd;
        }

        /// <summary>
        /// 读取账号的 6 种晶块数量, 返回 (itemId, slot, count) 列表。
        /// </summary>
        public static List<(int ItemId, int Slot, int Count)> LoadCubeFragments(SqliteConnection conn, SqliteTransaction tx, int accountId)
        {
            var result = new List<(int, int, int)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT cube_black, cube_white, cube_red, cube_blue, cube_clear, cube_gold FROM accounts WHERE account_id = @aid;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        result.Add((3033, 354, reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0))));
                        result.Add((3034, 355, reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1))));
                        result.Add((3035, 356, reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2))));
                        result.Add((3036, 357, reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3))));
                        result.Add((3037, 358, reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4))));
                        result.Add((3262, 359, reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5))));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 累加指定晶块到账号。
        /// </summary>
        public static void AddCubeFragment(SqliteConnection conn, SqliteTransaction tx, int accountId, int itemId, int count)
        {
            if (!CubeFragmentMap.TryGetValue(itemId, out var entry))
                throw new ArgumentException($"itemId {itemId} is not a cube fragment");

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"UPDATE accounts SET {entry.ColumnName} = {entry.ColumnName} + @count WHERE account_id = @aid;";
                cmd.Parameters.AddWithValue("@count", count);
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.ExecuteNonQuery();
            }
        }



        /// <summary>
        /// 启动时迁移: 把 character_items slot 354-359 的旧晶块数量归集到 accounts 表, 然后删除旧行。
        /// 幂等: 只在 accounts 对应列为 0 且 character_items 有数据时才迁移。
        /// </summary>
        public static void MigrateCubeFragmentsFromCharacterItems(SqliteConnection conn)
        {
            // 检查是否有待迁移的数据(任何角色在 slot 354-359 有晶块)
            bool hasOldData;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT COUNT(*) FROM character_items
WHERE list_type = 0 AND slot_index >= 354 AND slot_index <= 359
  AND item_template_id IN (3033, 3034, 3035, 3036, 3037, 3262);";
                hasOldData = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            if (!hasOldData)
                return;

            // 检查是否已经迁移过(accounts 表已有非零晶块)
            bool alreadyMigrated;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT COUNT(*) FROM accounts
WHERE cube_black != 0 OR cube_white != 0 OR cube_red != 0
   OR cube_blue != 0 OR cube_clear != 0 OR cube_gold != 0;";
                alreadyMigrated = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            if (alreadyMigrated)
            {
                // 已迁移但旧行残留, 清理旧行
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
DELETE FROM character_items
WHERE list_type = 0 AND slot_index >= 354 AND slot_index <= 359
  AND item_template_id IN (3033, 3034, 3035, 3036, 3037, 3262);";
                    cmd.ExecuteNonQuery();
                }
                return;
            }

            // 对每个 account, 从其角色的 character_items 中读取晶块数据
            // (单账号项目: 取角色中各晶块的 MAX stack_count 做为账号值)
            foreach (var kv in CubeFragmentMap)
            {
                var itemId = kv.Key;
                var colName = kv.Value.ColumnName;
                var slot = kv.Value.Slot;

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $@"
UPDATE accounts
SET {colName} = COALESCE((
    SELECT MAX(ci.stack_count)
    FROM character_items ci
    JOIN characters ch ON ch.character_id = ci.character_id
    WHERE ch.account_id = accounts.account_id
      AND ci.list_type = 0 AND ci.slot_index = @slot
      AND ci.item_template_id = @itemId
), 0)
WHERE {colName} = 0;";
                    cmd.Parameters.AddWithValue("@slot", slot);
                    cmd.Parameters.AddWithValue("@itemId", itemId);
                    cmd.ExecuteNonQuery();
                }
            }

            // 删除旧的 character_items 行
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
DELETE FROM character_items
WHERE list_type = 0 AND slot_index >= 354 AND slot_index <= 359
  AND item_template_id IN (3033, 3034, 3035, 3036, 3037, 3262);";
                cmd.ExecuteNonQuery();
            }
        }

        public static WalletSnapshot LoadWallet(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var w = new WalletSnapshot();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT stack_count FROM character_items WHERE character_id = @cid AND list_type = 0 AND slot_index = 0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    w.Gold = Convert.ToInt32(result);
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT a.cera, a.token_cera, a.happy_token_cera, a.lucky_star
FROM accounts a
JOIN characters c ON c.account_id = a.account_id
WHERE c.character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        w.Cera = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                        w.TokenCera = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                        w.HappyTokenCera = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                        w.LuckyStar = reader.IsDBNull(3) ? (ushort)0 : NormalizeLuckyStar(Convert.ToInt32(reader.GetValue(3)));
                    }
                }
            }
            // 读取账号级晶块
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT a.cube_black, a.cube_white, a.cube_red, a.cube_blue, a.cube_clear, a.cube_gold
FROM accounts a
JOIN characters c ON c.account_id = a.account_id
WHERE c.character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        w.CubeBlack = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                        w.CubeWhite = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                        w.CubeRed = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                        w.CubeBlue = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3));
                        w.CubeClear = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
                        w.CubeGold = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5));
                    }
                }
            }
            return w;
        }

        public static ushort LoadLuckyStar(string connectionString, int accountId)
        {
            if (accountId <= 0)
                return 0;

            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                return LoadLuckyStar(conn, null, accountId);
            }
        }

        public static ushort LoadLuckyStar(SqliteConnection connection, SqliteTransaction transaction, int accountId)
        {
            if (connection == null || accountId <= 0)
                return 0;

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT lucky_star FROM accounts WHERE account_id = @aid;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                var result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return 0;

                return NormalizeLuckyStar(Convert.ToInt32(result));
            }
        }

        public static void UpdateLuckyStar(SqliteConnection connection, SqliteTransaction transaction, int accountId, ushort luckyStar)
        {
            if (connection == null || transaction == null || accountId <= 0)
                return;

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE accounts
SET lucky_star = @luckyStar
WHERE account_id = @aid;";
                cmd.Parameters.AddWithValue("@luckyStar", (int)luckyStar);
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.ExecuteNonQuery();
            }
        }

        private static ushort NormalizeLuckyStar(int value)
        {
            if (value <= 0)
                return 0;
            if (value > 999)
                return 999;
            return (ushort)value;
        }

        public static void UpdateGold(SqliteConnection connection, SqliteTransaction transaction, int characterId, int newGold)
        {
            UpdateCurrencySlot(connection, transaction, characterId, 0, newGold);
        }

        // 点券账号化: 写 accounts.cera, 并把 characters.coin 作兼容镜像同步,
        // 防止旧的角色级 coin 在下次迁移时重新灌入账号钱包。
        public static void UpdateCera(SqliteConnection connection, SqliteTransaction transaction, int characterId, int newCera)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE accounts
SET cera = @val
WHERE account_id = (SELECT account_id FROM characters WHERE character_id = @cid);";
                cmd.Parameters.AddWithValue("@val", newCera);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE characters
SET coin = @val
WHERE account_id = (SELECT account_id FROM characters WHERE character_id = @cid);";
                cmd.Parameters.AddWithValue("@val", newCera);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        // 代币券(token cera): 账号级 accounts.token_cera。
        public static void UpdateTokenCera(SqliteConnection connection, SqliteTransaction transaction, int characterId, int newValue)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE accounts
SET token_cera = @val
WHERE account_id = (SELECT account_id FROM characters WHERE character_id = @cid);";
                cmd.Parameters.AddWithValue("@val", newValue);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        // 欢乐代币券(happy token cera): 账号级 accounts.happy_token_cera。
        public static void UpdateHappyTokenCera(SqliteConnection connection, SqliteTransaction transaction, int characterId, int newValue)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE accounts
SET happy_token_cera = @val
WHERE account_id = (SELECT account_id FROM characters WHERE character_id = @cid);";
                cmd.Parameters.AddWithValue("@val", newValue);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        private static void UpdateCurrencySlot(SqliteConnection connection, SqliteTransaction transaction, int characterId, int slot, int value)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE character_items
SET stack_count = @val,
    instance_value = @val
WHERE character_id = @cid AND list_type = 0 AND slot_index = @slot;";
                cmd.Parameters.AddWithValue("@val", value);
                cmd.Parameters.AddWithValue("@slot", slot);
                cmd.Parameters.AddWithValue("@cid", characterId);
                var updated = cmd.ExecuteNonQuery();
                if (updated > 0)
                    return;
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"INSERT OR REPLACE INTO character_items
(owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16)
VALUES ('character', @cid, @cid, 0, @slot, 0, 'special', @val, @val, 0, 0, 0, 0, 0);";
                cmd.Parameters.AddWithValue("@val", value);
                cmd.Parameters.AddWithValue("@slot", slot);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        public static int LoadCera(SqliteConnection connection, int characterId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT a.cera
FROM accounts a
JOIN characters c ON c.account_id = a.account_id
WHERE c.character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    return Convert.ToInt32(result);
                return 0;
            }
        }

        // 把历史的点券(原存于 packet_templates noti=53 或角色级 coin)归集到账号级 accounts.cera,
        // 然后把账号 cera 回写到该账号下所有角色的 coin 镜像列。
        public static void MigrateCeraFromPacketTemplates(SqliteConnection connection)
        {
            var hasCharacterCera = false;
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM characters WHERE coin != 0";
                hasCharacterCera = Convert.ToInt32(check.ExecuteScalar()) > 0;
            }
            if (!hasCharacterCera)
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT character_id, body FROM packet_templates WHERE noti_type = 53";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var cid = reader.GetInt32(0);
                            var body = reader[1] as byte[];
                            if (body != null && body.Length >= 5)
                            {
                                int cera = BitConverter.ToInt32(body, 1);
                                using (var upd = connection.CreateCommand())
                                {
                                    upd.CommandText = "UPDATE characters SET coin = @coin WHERE character_id = @cid AND coin = 0;";
                                    upd.Parameters.AddWithValue("@coin", cera);
                                    upd.Parameters.AddWithValue("@cid", cid);
                                    upd.ExecuteNonQuery();
                                }
                            }
                        }
                    }
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE accounts
SET cera = (
    SELECT MAX(c.coin)
    FROM characters c
    WHERE c.account_id = accounts.account_id
)
WHERE cera = 0
  AND EXISTS (
    SELECT 1
    FROM characters c
    WHERE c.account_id = accounts.account_id
      AND c.coin > 0
  );";
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE characters
SET coin = (
    SELECT a.cera
    FROM accounts a
    WHERE a.account_id = characters.account_id
)
WHERE EXISTS (
    SELECT 1
    FROM accounts a
    WHERE a.account_id = characters.account_id
);";
                cmd.ExecuteNonQuery();
            }
        }
    }

    public sealed class WalletSnapshot
    {
        public int Gold { get; set; }
        public int Cera { get; set; }

        public int TokenCera { get; set; }

        public int HappyTokenCera { get; set; }

        public ushort LuckyStar { get; set; }

        // 账号级晶块
        public int CubeBlack { get; set; }
        public int CubeWhite { get; set; }
        public int CubeRed { get; set; }
        public int CubeBlue { get; set; }
        public int CubeClear { get; set; }
        public int CubeGold { get; set; }
    }
}
