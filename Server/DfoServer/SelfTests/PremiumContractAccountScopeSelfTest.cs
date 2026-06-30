using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class PremiumContractAccountScopeSelfTest
    {
        private const int AccountId = 1;
        private const int ReceiverCharacterId = 990201;
        private const int OwnerCharacterId = 990202;
        private const int ExpertContract3DayItemId = 44;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== PREMIUM_CONTRACT_ACCOUNT_SCOPE selftest ===");

            AccountOwnedContractProjectsToOtherCharacters();
            AccountOwnedAckPremiumBlobProjectsToOtherCharacters();
            AccountOwnedClientNativeAckPremiumBlobProjectsToOtherCharacters();
            UpdatedContractStackProjectsAsActiveContract();
            EmptyPacketSequenceStillFallsBackToDefaultInitSequence();
            CeraInitPacketUsesAccountWallet();

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void AccountOwnedAckPremiumBlobProjectsToOtherCharacters()
        {
            var tempDb = Path.Combine(Path.GetTempPath(), "premium_contract_ack_scope_selftest.db");
            DeleteTempDatabase(tempDb);

            EnsureAccountAndCharacters(tempDb);
            SeedAckPremiumOnOwnerCharacter(tempDb, 27);

            var dataSource = new SqliteSelectCharacterDataSource(
                tempDb,
                ServerPaths.SchemaFilePath,
                new SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath));

            var snapshot = dataSource.Load(ReceiverCharacterId, AccountId);
            Check(
                "other character receives account-level existing ACK premium",
                HasPremium(snapshot.InitializationSnapshot, 27));
        }

        private static void AccountOwnedClientNativeAckPremiumBlobProjectsToOtherCharacters()
        {
            var tempDb = Path.Combine(Path.GetTempPath(), "premium_contract_raw_ack_scope_selftest.db");
            DeleteTempDatabase(tempDb);

            EnsureAccountAndCharacters(tempDb);
            var clientNativeEndTime = new byte[] { 0xD2, 0xE2, 0x2F, 0x62, 0x00, 0x00, 0x00, 0x00 };
            SeedRawAckPremiumOnOwnerCharacter(tempDb, 27, clientNativeEndTime);

            var dataSource = new SqliteSelectCharacterDataSource(
                tempDb,
                ServerPaths.SchemaFilePath,
                new SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath));

            var snapshot = dataSource.Load(ReceiverCharacterId, AccountId);
            Check(
                "other character receives account-level client-native ACK premium bytes",
                HasPremiumWithExactEndTime(snapshot.InitializationSnapshot, 27, clientNativeEndTime));
        }

        private static void EmptyPacketSequenceStillFallsBackToDefaultInitSequence()
        {
            var tempDb = Path.Combine(Path.GetTempPath(), "premium_contract_empty_sequence_selftest.db");
            DeleteTempDatabase(tempDb);

            EnsureAccountAndCharacters(tempDb);
            SeedExpertContractOnOwnerCharacter(tempDb);

            var dataSource = new SqliteSelectCharacterDataSource(
                tempDb,
                ServerPaths.SchemaFilePath,
                new SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath));

            var snapshot = dataSource.Load(ReceiverCharacterId, AccountId);
            Check(
                "empty packet sequence remains empty so default init sequence is used",
                snapshot.PacketTemplates.Count == 0);
        }

        private static void CeraInitPacketUsesAccountWallet()
        {
            var tempDb = Path.Combine(Path.GetTempPath(), "premium_contract_cera_wallet_selftest.db");
            DeleteTempDatabase(tempDb);

            EnsureAccountAndCharacters(tempDb);
            SeedAccountWallet(tempDb, 987200, 1000000, 938390);

            var dataSource = new SqliteSelectCharacterDataSource(
                tempDb,
                ServerPaths.SchemaFilePath,
                new SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath));

            var snapshot = dataSource.Load(ReceiverCharacterId, AccountId);
            var ok = new CeraBodyBuilder().TryBuild(snapshot, 0, out var body);

            Check(
                "0x0035 init packet carries cera/token/happy wallet",
                ok
                && body != null
                && body.Length == 13
                && body[0] == 1
                && BitConverter.ToInt32(body, 1) == 987200
                && BitConverter.ToInt32(body, 5) == 1000000
                && BitConverter.ToInt32(body, 9) == 938390);
        }

        private static void UpdatedContractStackProjectsAsActiveContract()
        {
            var tempDb = Path.Combine(Path.GetTempPath(), "premium_contract_updated_stack_selftest.db");
            DeleteTempDatabase(tempDb);

            EnsureAccountAndCharacters(tempDb);
            SeedUpdatedExpertContractStackOnOwnerCharacter(tempDb);

            var dataSource = new SqliteSelectCharacterDataSource(
                tempDb,
                ServerPaths.SchemaFilePath,
                new SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath));

            var snapshot = dataSource.Load(ReceiverCharacterId, AccountId);
            Check(
                "recently updated expert-contract stack is active even when created_at is old",
                HasFutureServiceSlot(snapshot.InitializationSnapshot, 6));
        }

        private static void AccountOwnedContractProjectsToOtherCharacters()
        {
            var tempDb = Path.Combine(Path.GetTempPath(), "premium_contract_account_scope_selftest.db");
            DeleteTempDatabase(tempDb);

            EnsureAccountAndCharacters(tempDb);
            SeedExpertContractOnOwnerCharacter(tempDb);
            SeedReceiverPacketSequenceWithoutPremium(tempDb);

            var dataSource = new SqliteSelectCharacterDataSource(
                tempDb,
                ServerPaths.SchemaFilePath,
                new SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath));

            var snapshot = dataSource.Load(ReceiverCharacterId, AccountId);
            Check(
                "other character receives account expert-contract ACK premium",
                HasPremium(snapshot.InitializationSnapshot, ExpertContract3DayItemId));
            Check(
                "other character receives active expert-contract service slot",
                HasFutureServiceSlot(snapshot.InitializationSnapshot, 6));
            Check(
                "other character packet sequence includes premium service packet",
                HasPremiumServicePacket(snapshot.PacketTemplates));
        }

        private static bool HasPremium(SelectCharacterInitializationSnapshot snapshot, byte premiumType)
        {
            if (snapshot == null)
                return false;

            foreach (var premium in snapshot.AckPremiums)
            {
                if (premium != null && premium.PremiumType == premiumType)
                    return true;
            }

            return false;
        }

        private static bool HasPremiumWithExactEndTime(
            SelectCharacterInitializationSnapshot snapshot,
            byte premiumType,
            byte[] endTime)
        {
            if (snapshot == null || endTime == null)
                return false;

            foreach (var premium in snapshot.AckPremiums)
            {
                if (premium == null || premium.PremiumType != premiumType || premium.EndTime == null)
                    continue;
                if (premium.EndTime.Length != endTime.Length)
                    continue;

                var same = true;
                for (var i = 0; i < endTime.Length; i++)
                {
                    if (premium.EndTime[i] == endTime[i])
                        continue;
                    same = false;
                    break;
                }

                if (same)
                    return true;
            }

            return false;
        }

        private static bool HasFutureServiceSlot(SelectCharacterInitializationSnapshot snapshot, int serviceIndex)
        {
            if (snapshot == null || snapshot.PremiumServiceType != 1 || snapshot.PremiumServiceData == null)
                return false;

            var off = 5 + serviceIndex * 9 + 1;
            if (off < 0 || off + 4 > snapshot.PremiumServiceData.Length)
                return false;

            var expire = BitConverter.ToInt32(snapshot.PremiumServiceData, off);
            return expire > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static bool HasPremiumServicePacket(List<SelectCharacterPacketTemplate> templates)
        {
            if (templates == null)
                return false;

            foreach (var template in templates)
            {
                if (template != null && template.Command == 0x01 && template.Type == 0x0312)
                    return true;
            }

            return false;
        }

        private static void EnsureAccountAndCharacters(string databasePath)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'premium-contract-account-scope', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name, level, job, grow_type)
VALUES (@receiverCid, @aid, 'receiver', 86, 0, 35);

INSERT OR IGNORE INTO characters (character_id, account_id, name, level, job, grow_type)
VALUES (@ownerCid, @aid, 'owner', 86, 0, 35);

INSERT OR IGNORE INTO character_init_flags (character_id)
VALUES (@receiverCid);

INSERT OR IGNORE INTO character_init_flags (character_id)
VALUES (@ownerCid);";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@receiverCid", ReceiverCharacterId);
                    cmd.Parameters.AddWithValue("@ownerCid", OwnerCharacterId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void SeedAccountWallet(string databasePath, int cera, int tokenCera, int happyTokenCera)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE accounts
SET cera = @cera,
    token_cera = @tokenCera,
    happy_token_cera = @happyTokenCera
WHERE account_id = @aid;

UPDATE characters
SET coin = @cera
WHERE account_id = @aid;";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@cera", cera);
                    cmd.Parameters.AddWithValue("@tokenCera", tokenCera);
                    cmd.Parameters.AddWithValue("@happyTokenCera", happyTokenCera);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void SeedReceiverPacketSequenceWithoutPremium(string databasePath)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO packet_sequence (
    character_id, seq_index, command, noti_type, kind, item_list_type, occurrence_index)
VALUES
    (@receiverCid, 0, 1, 4, 0, -1, 0),
    (@receiverCid, 1, 0, 768, 0, -1, 0),
    (@receiverCid, 2, 0, 984, 0, -1, 0);";
                    cmd.Parameters.AddWithValue("@receiverCid", ReceiverCharacterId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void SeedExpertContractOnOwnerCharacter(string databasePath)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id,
    item_kind, stack_count, instance_value, durability, seal_flag, option_value,
    expire_time, marker_16, pet_serial_or_handle, extra_json, created_at, updated_at)
VALUES (
    'character', @ownerCid, @ownerCid, 0, 65, @itemId,
    'stackable', 1, 1, 0, 0, 0,
    0, 0, 0, '{}', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);";
                    cmd.Parameters.AddWithValue("@ownerCid", OwnerCharacterId);
                    cmd.Parameters.AddWithValue("@itemId", ExpertContract3DayItemId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void SeedUpdatedExpertContractStackOnOwnerCharacter(string databasePath)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id,
    item_kind, stack_count, instance_value, durability, seal_flag, option_value,
    expire_time, marker_16, pet_serial_or_handle, extra_json, created_at, updated_at)
VALUES (
    'character', @ownerCid, @ownerCid, 0, 66, @itemId,
    'stackable', 2, 2, 0, 0, 0,
    0, 0, 0, '{}', datetime('now', '-10 days'), CURRENT_TIMESTAMP);";
                    cmd.Parameters.AddWithValue("@ownerCid", OwnerCharacterId);
                    cmd.Parameters.AddWithValue("@itemId", ExpertContract3DayItemId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void SeedAckPremiumOnOwnerCharacter(string databasePath, byte premiumType)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    var blob = new byte[10];
                    blob[0] = 1;
                    blob[1] = premiumType;
                    var expire = BitConverter.GetBytes((long)DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds());
                    Buffer.BlockCopy(expire, 0, blob, 2, expire.Length);

                    cmd.CommandText = @"
UPDATE character_init_flags
SET ack_premium_blob = @blob
WHERE character_id = @ownerCid;";
                    cmd.Parameters.AddWithValue("@ownerCid", OwnerCharacterId);
                    cmd.Parameters.AddWithValue("@blob", blob);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void SeedRawAckPremiumOnOwnerCharacter(string databasePath, byte premiumType, byte[] endTime)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    var blob = new byte[10];
                    blob[0] = 1;
                    blob[1] = premiumType;
                    Buffer.BlockCopy(endTime, 0, blob, 2, Math.Min(8, endTime.Length));

                    cmd.CommandText = @"
UPDATE character_init_flags
SET ack_premium_blob = @blob
WHERE character_id = @ownerCid;";
                    cmd.Parameters.AddWithValue("@ownerCid", OwnerCharacterId);
                    cmd.Parameters.AddWithValue("@blob", blob);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteTempDatabase(string path)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    var file = path + suffix;
                    if (File.Exists(file))
                        File.Delete(file);
                }
                catch
                {
                }
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
    }
}
