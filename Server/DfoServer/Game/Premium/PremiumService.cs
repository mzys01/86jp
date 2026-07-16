using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Game.Premium
{
    public static class PremiumService
    {
        private const int PremiumServiceEntryExpireBase = 6;
        private const int PremiumServiceEntryUsedCountBase = 10;
        private const int PremiumServiceEntryStride = 9;

        public static bool IsContractItem(int itemTemplateId)
        {
            return PremiumCatalog.Load().TryGetValue(itemTemplateId, out var pt, out var dd)
                && pt > 0 && dd > 0;
        }

        public static async Task ActivateAndNotify(
            EnhancedClientSession session,
            IReadOnlyList<(int itemTemplateId, int count)> items,
            ISelectCharacterDataSource dataSource = null)
        {
            if (session == null)
                return;

            var accountId = session.Account?.AccountId ?? 0;
            if (accountId <= 0)
                return;

            var notifications = new List<(int premiumType, long remaining)>();
            if (items != null && items.Count > 0)
            {
                var connStr = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                    Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);

                using (var conn = new SqliteConnection(connStr))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        foreach (var (itemTemplateId, count) in items)
                        {
                            if (!PremiumCatalog.Load().TryGetValue(itemTemplateId, out var premiumType, out var durationDays))
                                continue;
                            if (premiumType <= 0 || durationDays <= 0)
                                continue;

                            var effectiveCount = Math.Max(1, count);
                            var totalDuration = (long)durationDays * 86400 * effectiveCount;
                            var newExpire = UpsertPremiumExpire(conn, tx, accountId, premiumType, now, totalDuration);
                            var remaining = newExpire - now;
                            notifications.Add((premiumType, remaining));
                            FileLogger.Log($"[PremiumService] Contract activated: account={accountId} type={premiumType} days={durationDays}x{effectiveCount} remaining={remaining} item=0x{itemTemplateId:X8}");
                        }

                        tx.Commit();
                    }
                }
            }

            foreach (var (premiumType, remaining) in notifications)
            {
                var body = BuildCeraSpecialItemNotification(premiumType, remaining);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0042, body));
            }

            await SendPremiumServiceRefresh(session, accountId, dataSource);
        }

        private static byte[] BuildCeraSpecialItemNotification(int premiumType, long remaining)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(2);
            writer.WriteByte((byte)premiumType);
            writer.WriteBytes(BitConverter.GetBytes(remaining));
            return writer.ToArray();
        }

        private static async Task SendPremiumServiceRefresh(
            EnhancedClientSession session,
            int accountId,
            ISelectCharacterDataSource dataSource)
        {
            try
            {
                var source = dataSource ?? new SqliteSelectCharacterDataSource(
                    Infrastructure.ServerPaths.DatabasePath,
                    Infrastructure.ServerPaths.SchemaFilePath,
                    null);

                int cid = session.Player?.CharacterId ?? 0;
                if (cid <= 0) return;

                var snapshot = source.Load(cid, accountId);
                var initSnap = snapshot?.InitializationSnapshot;
                if (initSnap?.PremiumServiceData == null)
                    return;

                var writer = new GamePacketWriter();
                writer.WriteByte(1);
                writer.WriteUInt16(initSnap.PremiumServiceType);
                writer.WriteBytes(initSnap.PremiumServiceData);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0312, writer.ToArray()));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[PremiumService] premium service refresh failed: {ex.Message}");
            }
        }

        public static async Task<(bool success, InventoryMutationResult result)> TryBuyDevilContractSlot(
            EnhancedClientSession session,
            int commodityNo,
            ISelectCharacterDataSource dataSource = null)
        {
            var accountId = session?.Account?.AccountId ?? 0;
            if (accountId <= 0)
                return (false, null);

            var catalog = DevilContractCatalog.Load();
            if (!catalog.TryGetSlot(commodityNo, out var slotIndex, out var durationDays, out var ceraPrice))
                return (false, null);

            var connStr = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var duration = (long)durationDays * 86400;
            var premiumType = DevilContractCatalog.SlotToPremiumType(slotIndex);

            int updatedCera, tokenCera, happyTokenCera;
            long remaining;
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
                if (cid <= 0) return (false, null);

                using (var tx = conn.BeginTransaction())
                {
                    var wallet = CurrencyService.LoadWallet(conn, tx, cid);
                    if (!CurrencyService.TrySpendCera(conn, tx, cid, ceraPrice))
                    {
                        FileLogger.Log($"[PremiumService] Devil slot {slotIndex} rejected: cera {wallet.Cera} < {ceraPrice}");
                        return (false, null);
                    }
                    updatedCera = wallet.Cera - ceraPrice;
                    tokenCera = wallet.TokenCera;
                    happyTokenCera = wallet.HappyTokenCera;

                    var newExpire = UpsertPremiumExpire(conn, tx, accountId, premiumType, now, duration);
                    remaining = newExpire - now;
                    tx.Commit();
                }

                FileLogger.Log($"[PremiumService] Devil slot {slotIndex} activated: account={accountId} days={durationDays} cera={updatedCera}");
            }

            if (session != null)
            {
                var body = BuildCeraSpecialItemNotification(premiumType, remaining);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0042, body));
                await SendPremiumServiceRefresh(session, accountId, dataSource);
            }

            var result = new InventoryMutationResult
            {
                ConsumedOnPurchase = true,
                UpdatedCoin = updatedCera,
                UpdatedTokenCera = tokenCera,
                UpdatedHappyTokenCera = happyTokenCera,
                RequestedCount = 1,
                AppliedCount = 1,
            };
            return (true, result);
        }

        public static byte[] BuildPremiumServiceData(
            string connStr,
            int accountId,
            IReadOnlyDictionary<int, int> usedCountBySlot = null)
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
                            var expireOffset = PremiumServiceEntryExpireBase + slot * PremiumServiceEntryStride;
                            Buffer.BlockCopy(BitConverter.GetBytes((int)Math.Min(expire, int.MaxValue)), 0, data, expireOffset, 4);

                            if (usedCountBySlot != null
                                && usedCountBySlot.TryGetValue(slot, out var usedCount))
                            {
                                WriteInt32(
                                    data,
                                    PremiumServiceEntryUsedCountBase + slot * PremiumServiceEntryStride,
                                    Math.Max(0, usedCount));
                            }
                        }
                    }
                }
            }
            return data;
        }

        // 账号是否有生效中的"自动修理"契约 — 自行解析连接串(供 handler 直接调用)。
        public static bool HasActiveAutoRepairForAccount(int accountId)
        {
            var connStr = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);
            return HasActiveAutoRepair(connStr, accountId);
        }

        // 账号是否有生效中的"自动修理"契约(魔王契约 slot 6, premium_type=586)。
        // 生效时装备修理免费。
        public static bool HasActiveAutoRepair(string connStr, int accountId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT 1 FROM account_premiums WHERE account_id=@aid AND premium_type=@type AND end_time>@now LIMIT 1;";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.Parameters.AddWithValue("@type", DevilContractCatalog.AutoRepairPremiumType);
                    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    return cmd.ExecuteScalar() != null;
                }
            }
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

        private static void WriteInt32(byte[] data, int offset, int value)
        {
            if (data == null || offset < 0 || offset + 4 > data.Length)
                return;

            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, data, offset, 4);
        }
    }
}
