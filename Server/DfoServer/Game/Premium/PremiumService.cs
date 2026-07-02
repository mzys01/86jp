using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Network;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Premium
{
    public static class PremiumService
    {
        public static (int premiumType, long remaining)? TryActivateContract(int accountId, int itemTemplateId)
        {
            if (!PremiumCatalog.Load().TryGetValue(itemTemplateId, out var premiumType, out var durationDays))
                return null;
            if (premiumType <= 0 || durationDays <= 0)
                return null;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var duration = (long)durationDays * 86400;
            var connStr = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);

            var newExpire = UpsertPremiumExpire(connStr, accountId, premiumType, now, duration);
            var remaining = newExpire - now;
            FileLogger.Log($"[PremiumService] Contract activated: account={accountId} type={premiumType} days={durationDays} remaining={remaining} item=0x{itemTemplateId:X8}");
            return (premiumType, remaining);
        }

        public static bool TryBuyDevilContractSlot(int accountId, int commodityNo, out InventoryMutationResult result)
        {
            result = null;
            var catalog = DevilContractCatalog.Load();
            if (!catalog.TryGetSlot(commodityNo, out var slotIndex, out var durationDays, out var ceraPrice))
                return false;

            var connStr = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var duration = (long)durationDays * 86400;
            var premiumType = DevilContractCatalog.SlotToPremiumType(slotIndex);

            int updatedCera, tokenCera, happyTokenCera;
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                int cid = 0;
                using (var q = conn.CreateCommand())
                {
                    q.CommandText = "SELECT character_id FROM characters WHERE account_id=@aid LIMIT 1;";
                    q.Parameters.AddWithValue("@aid", accountId);
                    var val = q.ExecuteScalar();
                    if (val != null && val != DBNull.Value)
                        cid = Convert.ToInt32(val);
                }
                if (cid <= 0) return false;

                var wallet = CurrencyService.LoadWallet(conn, null, cid);
                if (wallet.Cera < ceraPrice)
                {
                    FileLogger.Log($"[PremiumService] Devil slot {slotIndex} rejected: cera {wallet.Cera} < {ceraPrice}");
                    return false;
                }
                updatedCera = wallet.Cera - ceraPrice;
                tokenCera = wallet.TokenCera;
                happyTokenCera = wallet.HappyTokenCera;

                using (var tx = conn.BeginTransaction())
                {
                    CurrencyService.UpdateCera(conn, tx, cid, updatedCera);
                    UpsertPremiumExpire(conn, tx, accountId, premiumType, now, duration);
                    tx.Commit();
                }

                FileLogger.Log($"[PremiumService] Devil slot {slotIndex} activated: account={accountId} days={durationDays} cera={updatedCera}");
            }

            result = new InventoryMutationResult
            {
                ConsumedOnPurchase = true,
                UpdatedCoin = updatedCera,
                UpdatedTokenCera = tokenCera,
                UpdatedHappyTokenCera = happyTokenCera,
                RequestedCount = 1,
                AppliedCount = 1,
            };
            return true;
        }

        public static byte[] BuildPremiumServiceData(string connStr, int accountId)
        {
            var data = new byte[74];
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT premium_type, end_time FROM account_premiums WHERE account_id=@aid AND premium_type>=@lo AND premium_type<@hi;";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.Parameters.AddWithValue("@lo", DevilContractCatalog.SlotPremiumTypeBase);
                    cmd.Parameters.AddWithValue("@hi", DevilContractCatalog.SlotPremiumTypeBase + 8);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var slot = DevilContractCatalog.PremiumTypeToSlot(reader.GetInt32(0));
                            if (slot < 0 || slot >= 8) continue;
                            var expire = reader.GetInt64(1);
                            var off = 6 + slot * 9;
                            Buffer.BlockCopy(BitConverter.GetBytes((int)Math.Min(expire, int.MaxValue)), 0, data, off, 4);
                        }
                    }
                }
            }
            return data;
        }

        public static long LoadDevilContractMaxExpire(string connStr, int accountId)
        {
            long maxExpire = 0;
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT MAX(end_time) FROM account_premiums WHERE account_id=@aid AND premium_type>=@lo AND premium_type<@hi AND end_time>@now;";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.Parameters.AddWithValue("@lo", DevilContractCatalog.SlotPremiumTypeBase);
                    cmd.Parameters.AddWithValue("@hi", DevilContractCatalog.SlotPremiumTypeBase + 8);
                    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    var val = cmd.ExecuteScalar();
                    if (val != null && val != DBNull.Value)
                        maxExpire = Convert.ToInt64(val);
                }
            }
            return maxExpire;
        }

        public static bool HasActivePremium(string connStr, int accountId, params int[] premiumTypes)
        {
            if (accountId <= 0 || premiumTypes == null || premiumTypes.Length == 0)
                return false;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    var typeParams = new string[premiumTypes.Length];
                    for (var i = 0; i < premiumTypes.Length; i++)
                    {
                        var name = "@type" + i;
                        typeParams[i] = name;
                        cmd.Parameters.AddWithValue(name, premiumTypes[i]);
                    }

                    cmd.CommandText = $@"
SELECT 1
FROM account_premiums
WHERE account_id=@aid
  AND end_time>@now
  AND premium_type IN ({string.Join(",", typeParams)})
LIMIT 1;";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.Parameters.AddWithValue("@now", now);
                    return cmd.ExecuteScalar() != null;
                }
            }
        }

        private static long UpsertPremiumExpire(string connStr, int accountId, int premiumType, long now, long duration)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                return UpsertPremiumExpire(conn, null, accountId, premiumType, now, duration);
            }
        }

        private static long UpsertPremiumExpire(SqliteConnection conn, SqliteTransaction tx, int accountId, int premiumType, long now, long duration)
        {
            long oldExpire = 0;
            using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT end_time FROM account_premiums WHERE account_id=@aid AND premium_type=@type;";
                q.Parameters.AddWithValue("@aid", accountId);
                q.Parameters.AddWithValue("@type", premiumType);
                var val = q.ExecuteScalar();
                if (val != null && val != DBNull.Value)
                    oldExpire = Convert.ToInt64(val);
            }

            var newExpire = Math.Max(now, oldExpire) + duration;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO account_premiums (account_id, premium_type, end_time, updated_at)
VALUES (@aid, @type, @expire, CURRENT_TIMESTAMP)
ON CONFLICT(account_id, premium_type)
DO UPDATE SET end_time = @expire, updated_at = CURRENT_TIMESTAMP;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.Parameters.AddWithValue("@type", premiumType);
                cmd.Parameters.AddWithValue("@expire", newExpire);
                cmd.ExecuteNonQuery();
            }
            return newExpire;
        }
    }
}
