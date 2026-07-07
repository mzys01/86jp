using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.ReviveCoin;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    // 复活币功能自测(每日功能打样的功能侧):
    // slot1 固定槽机制(领取-扣光-重建) + ReviveCoinService 用例
    // (救济规则/领取发币同事务/每日一次/死亡消耗)。机制侧见 DailyResetSelfTest。
    public static class ReviveCoinSelfTest
    {
        private const int AccountId = 930017;
        private const int CharacterId = 930117;
        private const int Coin = ReviveCoinService.ItemId;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== REVIVE_COIN selftest ===");

            var tempDb = Path.Combine(Path.GetTempPath(), "revive_coin_selftest.db");
            DeleteTempDatabase(tempDb);
            var connStr = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
            Seed(connStr);

            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);
            var assetService = new SqliteAssetService(tempDb, ServerPaths.SchemaFilePath, store);
            var dailyReset = new DailyResetService(tempDb, ServerPaths.SchemaFilePath);
            var reviveCoin = new ReviveCoinService(store, assetService, dailyReset);

            // ── slot1 固定槽机制: 发放-扣光-重建(store 专线) ──
            short slot;
            Check("发放复活币成功", store.TryPickupItem(CharacterId, AccountId, Coin, 3, out slot));
            Check("复活币落在 slot1", slot == ReviveCoinService.WalletSlot);
            Check("计数=3", store.CountItem(CharacterId, Coin) == 3);
            using (var scope = assetService.OpenScope(CharacterId, AccountId))
            {
                short rSlot;
                int remaining;
                Check("扣除3枚成功", assetService.TryRemoveItem(scope, Coin, 3, out rSlot, out remaining) && remaining == 0);
                scope.Commit();
            }
            Check("扣光后计数=0", store.CountItem(CharacterId, Coin) == 0);
            Check("重建发放成功", store.TryPickupItem(CharacterId, AccountId, Coin, 1, out slot));
            Check("重建仍落 slot1", slot == ReviveCoinService.WalletSlot);

            // ── 用例: 救济规则(身上有币不发, 且不烧当日标记) ──
            short grantSlot;
            Check("有币时领取被拒", !reviveCoin.TryGrantDaily(CharacterId, AccountId, out grantSlot));
            Check("被拒不消耗当日标记", !dailyReset.IsClaimed(CharacterId, ReviveCoinService.DailyClaimKey));

            // ── 用例: 死亡消耗 ──
            short useSlot;
            int useRemaining;
            Check("消耗1枚成功", reviveCoin.TryConsume(CharacterId, AccountId, out useSlot, out useRemaining));
            Check("消耗后剩0", useRemaining == 0 && store.CountItem(CharacterId, Coin) == 0);

            // ── 用例: 每日领取(无币且未领 → 发1枚; 标记与发币同事务) ──
            Check("无币未领时领取成功", reviveCoin.TryGrantDaily(CharacterId, AccountId, out grantSlot));
            Check("领取落 slot1", grantSlot == ReviveCoinService.WalletSlot);
            Check("领取后计数=1", store.CountItem(CharacterId, Coin) == 1);
            Check("领取后当日已领", dailyReset.IsClaimed(CharacterId, ReviveCoinService.DailyClaimKey));
            Check("当日二次领取被拒", !reviveCoin.TryGrantDaily(CharacterId, AccountId, out grantSlot));

            // ── 用例: 扣光后当日仍不可再领(每日一次不因消耗复活) ──
            Check("再消耗成功", reviveCoin.TryConsume(CharacterId, AccountId, out useSlot, out useRemaining));
            Check("无币时消耗被拒", !reviveCoin.TryConsume(CharacterId, AccountId, out useSlot, out useRemaining));
            Check("扣光后当日仍不可再领", !reviveCoin.TryGrantDaily(CharacterId, AccountId, out grantSlot));

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static void Seed(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash) VALUES (@aid, 'revive-coin-selftest', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name) VALUES (@cid, @aid, 'revive-coin-selftest');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            try
            {
                if (File.Exists(databasePath)) File.Delete(databasePath);
                if (File.Exists(databasePath + "-wal")) File.Delete(databasePath + "-wal");
                if (File.Exists(databasePath + "-shm")) File.Delete(databasePath + "-shm");
            }
            catch
            {
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++;
            else _fail++;
        }

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }
    }
}
