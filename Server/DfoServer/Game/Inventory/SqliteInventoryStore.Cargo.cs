using DfoServer.Game.Currency;
using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    // 账号金库(account_cargo_state): selection_key=容量档位, value32=存入金币。
    // 原先散在 InventoryHandler.Trade.cs 的裸SQL, 下沉为 store 方法; handler 只留解析+ACK。
    public sealed partial class SqliteInventoryStore
    {
        private const int CargoInitialCapacity = 1;
        private static readonly int[] CargoCapacityTiers = { 1, 8, 16, 24, 32, 40, 48, 56, 64 };

        // 存款: 角色金币条件扣减 + 金库原子增量, 同一事务。
        // 任一步失败(余额不足/金库未开通)整体回滚返回 false。
        public bool TryDepositCargoGold(int characterId, int accountId, int amount, out int newCharGold, out int newCargoGold)
        {
            newCharGold = 0;
            newCargoGold = 0;
            if (amount <= 0)
                return false;

            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var wallet = CurrencyService.LoadWallet(conn, tx, characterId);
                    int cargoGold = LoadCargoStateField(conn, tx, accountId, "value32");

                    if (!CurrencyService.TrySpendGold(conn, tx, characterId, amount) ||
                        !TryAddCargoGold(conn, tx, accountId, amount))
                        return false;

                    newCharGold = wallet.Gold - amount;
                    newCargoGold = cargoGold + amount;
                    tx.Commit();
                    return true;
                }
            }
        }

        // 取出: 金库条件扣减 + 角色金币原子增量, 同一事务。
        public bool TryWithdrawCargoGold(int characterId, int accountId, int amount, out int newCharGold, out int newCargoGold)
        {
            newCharGold = 0;
            newCargoGold = 0;
            if (amount <= 0)
                return false;

            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var wallet = CurrencyService.LoadWallet(conn, tx, characterId);
                    int cargoGold = LoadCargoStateField(conn, tx, accountId, "value32");

                    if (!TrySpendCargoGold(conn, tx, accountId, amount))
                        return false;
                    CurrencyService.GrantGold(conn, tx, characterId, amount);

                    newCharGold = wallet.Gold + amount;
                    newCargoGold = cargoGold - amount;
                    tx.Commit();
                    return true;
                }
            }
        }

        // 开通金库(初始容量档位)。已开通返回 false。
        public bool TryCreateAccountCargo(int accountId)
        {
            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                int existing = LoadCargoStateField(conn, null, accountId, "selection_key");
                if (existing > 0)
                    return false;

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR REPLACE INTO account_cargo_state (account_id, selection_key, value32, updated_at)
VALUES (@aid, @cap, 0, CURRENT_TIMESTAMP);";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.Parameters.AddWithValue("@cap", CargoInitialCapacity);
                    cmd.ExecuteNonQuery();
                }
                return true;
            }
        }

        // 升级容量档位。errorCode: 0x15=未开通, 0x13=已满级。
        public bool TryUpgradeAccountCargo(int accountId, out byte errorCode)
        {
            errorCode = 0;
            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                int current = LoadCargoStateField(conn, null, accountId, "selection_key");
                if (current <= 0)
                {
                    errorCode = 0x15;
                    return false;
                }

                int nextTierIndex = Array.IndexOf(CargoCapacityTiers, current) + 1;
                if (nextTierIndex <= 0 || nextTierIndex >= CargoCapacityTiers.Length)
                {
                    errorCode = 0x13;
                    return false;
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE account_cargo_state SET selection_key=@cap, updated_at=CURRENT_TIMESTAMP WHERE account_id=@aid;";
                    cmd.Parameters.AddWithValue("@cap", CargoCapacityTiers[nextTierIndex]);
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.ExecuteNonQuery();
                }
                return true;
            }
        }

        private static int LoadCargoStateField(SqliteConnection conn, SqliteTransaction tx, int accountId, string column)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT {column} FROM account_cargo_state WHERE account_id=@aid;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
            }
        }

        // 金库入账: 原子增量; 金库行不存在(未开通)命中0行返回false
        private static bool TryAddCargoGold(SqliteConnection conn, SqliteTransaction tx, int accountId, int amount)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE account_cargo_state SET value32=value32+@amt, updated_at=CURRENT_TIMESTAMP WHERE account_id=@aid;";
                cmd.Parameters.AddWithValue("@amt", amount);
                cmd.Parameters.AddWithValue("@aid", accountId);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        // 金库取出: 条件扣减, 余额不足或未开通返回false
        private static bool TrySpendCargoGold(SqliteConnection conn, SqliteTransaction tx, int accountId, int amount)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE account_cargo_state SET value32=value32-@amt, updated_at=CURRENT_TIMESTAMP WHERE account_id=@aid AND value32>=@amt;";
                cmd.Parameters.AddWithValue("@amt", amount);
                cmd.Parameters.AddWithValue("@aid", accountId);
                return cmd.ExecuteNonQuery() > 0;
            }
        }
    }
}
