using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    // 货币唯一入口自测: Grant=原子增量 / TrySpend=条件扣减。
    // 核心断言: 不足额扣费必须返回 false 且余额不变(旧 Add* 负数会静默 clamp 到 0)。
    public static class CurrencySelfTest
    {
        private const int AccountId = 920016;
        private const int CharacterId = 920116;
        private const int FreshCharacterId = 920117; // 无金币行的新角色
        private const int StartingGold = 5000;
        private const int StartingCera = 300;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== CURRENCY selftest ===");

            var tempDb = Path.Combine(Path.GetTempPath(), "currency_selftest.db");
            DeleteTempDatabase(tempDb);
            var connStr = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
            Seed(connStr);

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                // ── 金币 (character_items slot 0) ──
                using (var tx = conn.BeginTransaction())
                {
                    CurrencyService.GrantGold(conn, tx, CharacterId, 1000);
                    tx.Commit();
                }
                Check("GrantGold increments", LoadGold(conn, CharacterId) == StartingGold + 1000);

                using (var tx = conn.BeginTransaction())
                {
                    Check("TrySpendGold success", CurrencyService.TrySpendGold(conn, tx, CharacterId, 6000));
                    tx.Commit();
                }
                Check("TrySpendGold decrements", LoadGold(conn, CharacterId) == 0);

                using (var tx = conn.BeginTransaction())
                {
                    CurrencyService.GrantGold(conn, tx, CharacterId, 100);
                    Check("TrySpendGold insufficient returns false", !CurrencyService.TrySpendGold(conn, tx, CharacterId, 101));
                    tx.Commit();
                }
                Check("insufficient spend leaves balance unchanged (no clamp-to-0)", LoadGold(conn, CharacterId) == 100);

                using (var tx = conn.BeginTransaction())
                {
                    Check("TrySpendGold exact balance", CurrencyService.TrySpendGold(conn, tx, CharacterId, 100));
                    Check("TrySpendGold zero amount is no-op success", CurrencyService.TrySpendGold(conn, tx, CharacterId, 0));
                    tx.Commit();
                }
                Check("exact spend reaches zero", LoadGold(conn, CharacterId) == 0);

                // 新角色无金币行: Grant 自建行
                using (var tx = conn.BeginTransaction())
                {
                    CurrencyService.GrantGold(conn, tx, FreshCharacterId, 777);
                    tx.Commit();
                }
                Check("GrantGold creates slot-0 row for fresh character", LoadGold(conn, FreshCharacterId) == 777);
                Check("fresh character spend from missing row fails", SpendGoldOnce(conn, 999999, 1) == false);

                // ── 点券 (accounts.cera, characterId 经 characters 表寻址) ──
                using (var tx = conn.BeginTransaction())
                {
                    CurrencyService.GrantCera(conn, tx, CharacterId, 200);
                    tx.Commit();
                }
                Check("GrantCera increments", LoadAccountColumn(conn, "cera") == StartingCera + 200);

                using (var tx = conn.BeginTransaction())
                {
                    Check("TrySpendCera insufficient returns false", !CurrencyService.TrySpendCera(conn, tx, CharacterId, StartingCera + 201));
                    tx.Commit();
                }
                Check("cera unchanged after refused spend", LoadAccountColumn(conn, "cera") == StartingCera + 200);

                using (var tx = conn.BeginTransaction())
                {
                    Check("TrySpendCera success", CurrencyService.TrySpendCera(conn, tx, CharacterId, StartingCera + 200));
                    tx.Commit();
                }
                Check("cera reaches zero", LoadAccountColumn(conn, "cera") == 0);

                // ── 幸运星 (accounts.lucky_star, accountId 直接寻址, 上限999) ──
                using (var tx = conn.BeginTransaction())
                {
                    CurrencyService.GrantLuckyStar(conn, tx, AccountId, 500);
                    CurrencyService.GrantLuckyStar(conn, tx, AccountId, 600);
                    tx.Commit();
                }
                Check("GrantLuckyStar caps at 999", LoadAccountColumn(conn, "lucky_star") == 999);

                using (var tx = conn.BeginTransaction())
                {
                    Check("TrySpendLuckyStar success", CurrencyService.TrySpendLuckyStar(conn, tx, AccountId, 999));
                    Check("TrySpendLuckyStar on empty returns false", !CurrencyService.TrySpendLuckyStar(conn, tx, AccountId, 1));
                    tx.Commit();
                }

                // ── 负数入参必须炸, 不能静默 ──
                using (var tx = conn.BeginTransaction())
                {
                    Check("GrantGold negative throws", Throws(() => CurrencyService.GrantGold(conn, tx, CharacterId, -1)));
                    Check("TrySpendGold negative throws", Throws(() => CurrencyService.TrySpendGold(conn, tx, CharacterId, -1)));
                }
            }

            // ── IAssetService 路径 + DbScope 回滚语义 ──
            var assetService = new SqliteAssetService(tempDb, ServerPaths.SchemaFilePath);
            using (var scope = assetService.OpenScope(CharacterId, AccountId))
            {
                assetService.GrantGold(scope, 250);
                scope.Commit();
            }
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                Check("asset service GrantGold committed", LoadGold(conn, CharacterId) == 250);
            }

            using (var scope = assetService.OpenScope(CharacterId, AccountId))
            {
                Check("asset service TrySpendGold in scope", assetService.TrySpendGold(scope, 200));
                // 故意不 Commit → 扣减必须随事务回滚
            }
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                Check("uncommitted scope rolls back spend", LoadGold(conn, CharacterId) == 250);
            }

            var grantRewardMethod = typeof(Game.Dungeon.CardRewardService).GetMethod(
                "GrantGoldReward",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            WalletSnapshot rewardWallet = null;
            if (grantRewardMethod != null)
            {
                rewardWallet = grantRewardMethod.Invoke(
                    null,
                    new object[] { assetService, FreshCharacterId, AccountId, 75 }) as WalletSnapshot;
            }
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                Check("card gold reward loads wallet before commit and persists the grant",
                    rewardWallet != null
                    && rewardWallet.Gold == 852
                    && LoadGold(conn, FreshCharacterId) == 852);
            }

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static bool SpendGoldOnce(SqliteConnection conn, int characterId, int amount)
        {
            using (var tx = conn.BeginTransaction())
            {
                var ok = CurrencyService.TrySpendGold(conn, tx, characterId, amount);
                tx.Commit();
                return ok;
            }
        }

        private static int LoadGold(SqliteConnection conn, int characterId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT stack_count FROM character_items WHERE character_id=@cid AND list_type=0 AND slot_index=0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var v = cmd.ExecuteScalar();
                return v != null && v != DBNull.Value ? Convert.ToInt32(v) : -1;
            }
        }

        private static int LoadAccountColumn(SqliteConnection conn, string column)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT {column} FROM accounts WHERE account_id=@aid;";
                cmd.Parameters.AddWithValue("@aid", AccountId);
                var v = cmd.ExecuteScalar();
                return v != null && v != DBNull.Value ? Convert.ToInt32(v) : -1;
            }
        }

        private static bool Throws(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (ArgumentOutOfRangeException)
            {
                return true;
            }
        }

        private static void Seed(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash, cera)
VALUES (@aid, 'currency-selftest', '', @cera);

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@cid, @aid, 'currency-selftest');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@freshCid, @aid, 'currency-selftest-fresh');

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @cid, @cid, 0, 0, 0, 'special',
    @gold, @gold, 0, 0, 0, 0, 0,
    0, '{}');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.Parameters.AddWithValue("@freshCid", FreshCharacterId);
                    cmd.Parameters.AddWithValue("@gold", StartingGold);
                    cmd.Parameters.AddWithValue("@cera", StartingCera);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            try
            {
                if (File.Exists(databasePath))
                    File.Delete(databasePath);

                var wal = databasePath + "-wal";
                if (File.Exists(wal))
                    File.Delete(wal);

                var shm = databasePath + "-shm";
                if (File.Exists(shm))
                    File.Delete(shm);
            }
            catch
            {
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok)
                _pass++;
            else
                _fail++;
        }

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }
    }
}
