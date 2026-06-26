using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public static class CurrencyService
    {
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
                cmd.CommandText = @"INSERT INTO character_items
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
    }
}
