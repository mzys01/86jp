using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.Game.Premium
{
    public static class PremiumContractService
    {
        public const ushort DevilContractServiceType = 1;

        private const int PremiumServiceDataLength = 74;
        private const int PremiumServiceEntryBase = 5;
        private const int PremiumServiceEntryStride = 9;
        private const int ExpertContractServiceIndex = 6;

        private static readonly Dictionary<int, ContractDefinition> ContractItems =
            new Dictionary<int, ContractDefinition>
            {
                { 31, new ContractDefinition(31, 3, -1) },
                { 33, new ContractDefinition(33, 7, -1) },
                { 44, new ContractDefinition(44, 3, ExpertContractServiceIndex) },
                { 45, new ContractDefinition(45, 7, ExpertContractServiceIndex) },
            };

        public static void ApplyAccountPremiums(
            string databasePath,
            string schemaFilePath,
            int accountId,
            SelectCharacterInitializationSnapshot snapshot)
        {
            if (snapshot == null || accountId <= 0)
                return;

            var activePremiums = LoadAccountPremiums(databasePath, schemaFilePath, accountId);
            ApplyAckPremiums(snapshot.AckPremiums, activePremiums);
            ApplyPremiumServiceData(snapshot, activePremiums);
        }

        public static bool IsExpertContractItem(int itemTemplateId)
        {
            return ContractItems.TryGetValue(itemTemplateId, out var definition)
                && definition.ServiceIndex == ExpertContractServiceIndex;
        }

        private static Dictionary<byte, PremiumState> LoadAccountPremiums(string databasePath, string schemaFilePath, int accountId)
        {
            var result = new Dictionary<byte, PremiumState>();
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                LoadPremiumsFromAckBlobs(connection, accountId, result);
                LoadPremiumsFromContractItems(connection, accountId, result);
            }

            return result;
        }

        private static void LoadPremiumsFromAckBlobs(
            SqliteConnection connection,
            int accountId,
            Dictionary<byte, PremiumState> result)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT f.ack_premium_blob
FROM character_init_flags f
JOIN characters c ON c.character_id = f.character_id
WHERE c.account_id = @aid
  AND f.ack_premium_blob IS NOT NULL;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var blob = reader.IsDBNull(0) ? null : (byte[])reader[0];
                        MergeAckPremiumBlob(result, blob);
                    }
                }
            }
        }

        private static void LoadPremiumsFromContractItems(
            SqliteConnection connection,
            int accountId,
            Dictionary<byte, PremiumState> result)
        {
            var rows = new List<Tuple<int, int, string, string>>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT ci.item_template_id, MAX(1, ci.stack_count), ci.created_at, ci.updated_at
FROM character_items ci
JOIN characters c ON c.character_id = ci.character_id
WHERE c.account_id = @aid
  AND ci.owner_scope = 'character'
  AND ci.item_template_id IN (31, 33, 44, 45)
  AND ci.stack_count > 0;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add(Tuple.Create(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
                    }
                }
            }

            foreach (var row in rows)
            {
                if (!ContractItems.TryGetValue(row.Item1, out var definition))
                    continue;
                if (!TryParseSqliteTimestamp(row.Item3, out var createdAt))
                    continue;
                if (TryParseSqliteTimestamp(row.Item4, out var updatedAt) && updatedAt > createdAt)
                    createdAt = updatedAt;

                var expire = (int)Math.Min(
                    int.MaxValue,
                    createdAt.ToUnixTimeSeconds() + (long)definition.DurationDays * Math.Max(1, row.Item2) * 86400L);
                MergePremium(result, definition.PremiumType, expire, definition.ServiceIndex);
            }
        }

        private static void MergeAckPremiumBlob(Dictionary<byte, PremiumState> result, byte[] blob)
        {
            if (blob == null || blob.Length < 1)
                return;

            var count = blob[0];
            for (var i = 0; i < count; i++)
            {
                var off = 1 + i * 9;
                if (off + 9 > blob.Length)
                    break;

                var premiumType = blob[off];
                var endTime = new byte[8];
                Buffer.BlockCopy(blob, off + 1, endTime, 0, endTime.Length);
                var serviceIndex = ContractItems.TryGetValue(premiumType, out var definition)
                    ? definition.ServiceIndex
                    : -1;
                MergeRawPremium(result, premiumType, endTime, serviceIndex);
            }
        }

        private static void ApplyAckPremiums(
            List<AckPremiumEntrySnapshot> ackPremiums,
            Dictionary<byte, PremiumState> activePremiums)
        {
            if (ackPremiums == null || activePremiums == null)
                return;

            foreach (var state in activePremiums.Values)
            {
                if (state.RawEndTime == null && state.ExpireTime <= CurrentUnixTime())
                    continue;

                var existing = ackPremiums.Find(x => x != null && x.PremiumType == state.PremiumType);
                if (existing == null)
                {
                    ackPremiums.Add(new AckPremiumEntrySnapshot
                    {
                        PremiumType = state.PremiumType,
                        EndTime = state.RawEndTime == null
                            ? BuildAckExpireTime(state.ExpireTime)
                            : CloneEndTime(state.RawEndTime),
                    });
                    continue;
                }

                if (state.RawEndTime != null)
                {
                    existing.EndTime = CloneEndTime(state.RawEndTime);
                    continue;
                }

                if (ReadAckExpireTime(existing.EndTime, 0) < state.ExpireTime)
                    existing.EndTime = BuildAckExpireTime(state.ExpireTime);
            }
        }

        private static void ApplyPremiumServiceData(
            SelectCharacterInitializationSnapshot snapshot,
            Dictionary<byte, PremiumState> activePremiums)
        {
            if (activePremiums == null)
                return;

            foreach (var state in activePremiums.Values)
            {
                if (state.ServiceIndex < 0 || state.ExpireTime <= CurrentUnixTime())
                    continue;

                if (snapshot.PremiumServiceData == null || snapshot.PremiumServiceData.Length < PremiumServiceDataLength)
                    snapshot.PremiumServiceData = BuildExpiredPremiumServiceData();
                snapshot.PremiumServiceType = DevilContractServiceType;

                var off = GetServiceEntryOffset(state.ServiceIndex);
                if (off < 0 || off + 9 > snapshot.PremiumServiceData.Length)
                    continue;
                if (ReadAckExpireTime(snapshot.PremiumServiceData, off + 1) >= state.ExpireTime)
                    continue;

                snapshot.PremiumServiceData[off] = 0;
                WriteInt32(snapshot.PremiumServiceData, off + 1, state.ExpireTime);
                WriteInt32(snapshot.PremiumServiceData, off + 5, 0);
            }
        }

        private static byte[] BuildExpiredPremiumServiceData()
        {
            var data = new byte[PremiumServiceDataLength];
            var expired = Math.Max(0, CurrentUnixTime() - 86400);
            for (var off = PremiumServiceEntryBase; off + 9 <= data.Length; off += PremiumServiceEntryStride)
            {
                data[off] = 0;
                WriteInt32(data, off + 1, expired);
                WriteInt32(data, off + 5, 0);
            }

            return data;
        }

        private static void MergePremium(
            Dictionary<byte, PremiumState> result,
            byte premiumType,
            int expireTime,
            int serviceIndex)
        {
            if (expireTime <= CurrentUnixTime())
                return;

            if (!result.TryGetValue(premiumType, out var current))
            {
                result[premiumType] = new PremiumState
                {
                    PremiumType = premiumType,
                    ExpireTime = expireTime,
                    ServiceIndex = serviceIndex,
                };
                return;
            }

            if (current.ExpireTime < expireTime)
                current.ExpireTime = expireTime;

            if (current.ServiceIndex < 0 && serviceIndex >= 0)
                current.ServiceIndex = serviceIndex;
        }

        private static void MergeRawPremium(
            Dictionary<byte, PremiumState> result,
            byte premiumType,
            byte[] endTime,
            int serviceIndex)
        {
            if (endTime == null || endTime.Length < 8)
                return;

            var expireTime = ReadAckExpireTime(endTime, 0);
            if (!result.TryGetValue(premiumType, out var current))
            {
                result[premiumType] = new PremiumState
                {
                    PremiumType = premiumType,
                    ExpireTime = expireTime,
                    ServiceIndex = serviceIndex,
                    RawEndTime = CloneEndTime(endTime),
                };
                return;
            }

            if (current.RawEndTime == null || ReadAckExpireTime(current.RawEndTime, 0) < expireTime)
                current.RawEndTime = CloneEndTime(endTime);
            if (current.ExpireTime < expireTime)
                current.ExpireTime = expireTime;
            if (current.ServiceIndex < 0 && serviceIndex >= 0)
                current.ServiceIndex = serviceIndex;
        }

        private static byte[] BuildAckExpireTime(int expireTime)
        {
            var buffer = new byte[8];
            var bytes = BitConverter.GetBytes((long)expireTime);
            Buffer.BlockCopy(bytes, 0, buffer, 0, buffer.Length);
            return buffer;
        }

        private static byte[] CloneEndTime(byte[] endTime)
        {
            var copy = new byte[8];
            Buffer.BlockCopy(endTime, 0, copy, 0, Math.Min(copy.Length, endTime.Length));
            return copy;
        }

        private static int ReadAckExpireTime(byte[] buffer, int offset)
        {
            if (buffer == null || offset < 0 || offset + 4 > buffer.Length)
                return 0;
            return BitConverter.ToInt32(buffer, offset);
        }

        private static int GetServiceEntryOffset(int serviceIndex)
        {
            return serviceIndex < 0 ? -1 : PremiumServiceEntryBase + serviceIndex * PremiumServiceEntryStride;
        }

        private static void WriteInt32(byte[] data, int offset, int value)
        {
            if (data == null || offset < 0 || offset + 4 > data.Length)
                return;
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, data, offset, 4);
        }

        private static bool TryParseSqliteTimestamp(string value, out DateTimeOffset timestamp)
        {
            timestamp = DateTimeOffset.MinValue;
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
        }

        private static int CurrentUnixTime()
        {
            return (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private sealed class ContractDefinition
        {
            public ContractDefinition(byte premiumType, int durationDays, int serviceIndex)
            {
                PremiumType = premiumType;
                DurationDays = durationDays;
                ServiceIndex = serviceIndex;
            }

            public byte PremiumType { get; }

            public int DurationDays { get; }

            public int ServiceIndex { get; }
        }

        private sealed class PremiumState
        {
            public byte PremiumType { get; set; }

            public int ExpireTime { get; set; }

            public int ServiceIndex { get; set; }

            public byte[] RawEndTime { get; set; }
        }
    }
}
