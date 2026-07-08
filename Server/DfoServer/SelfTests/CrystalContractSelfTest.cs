using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class CrystalContractSelfTest
    {
        private const int AccountId = 8299;
        private const int CharacterId = 8299001;
        private const int SeedCharacterId = 8299002;

        public static int Run()
        {
            Console.WriteLine("=== CRYSTAL_CONTRACT selftest ===");
            int pass = 0;
            int fail = 0;

            void Check(string name, bool ok)
            {
                if (ok)
                {
                    pass++;
                    Console.WriteLine($"  [PASS] {name}");
                }
                else
                {
                    fail++;
                    Console.WriteLine($"  [FAIL] {name}");
                }
            }

            var tempDb = Path.Combine(Path.GetTempPath(), "crystal_contract_selftest.db");
            DeleteTempDatabase(tempDb);

            try
            {
                SeedCharacter(tempDb, CharacterId);
                var savedBody = new byte[] { 0x03, 0x01 };
                SaveInitBody(tempDb, CharacterId, 0x0300, savedBody);

                var charRepo = new Game.Characters.SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath);
                var dataSource = new SqliteSelectCharacterDataSource(tempDb, ServerPaths.SchemaFilePath, charRepo);
                var snapshot = dataSource.Load(CharacterId, AccountId);
                Check("select data source loads saved cube type", snapshot.InitializationSnapshot.CubeType == savedBody[0]);
                Check("select data source loads saved cube grade", snapshot.InitializationSnapshot.CubeGrade == savedBody[1]);

                var builder = new CubeInfoBodyBuilder();
                Check("0x0300 builder succeeds", builder.TryBuild(snapshot, 0, out var builtBody));
                Check("0x0300 builder returns saved cube body", ByteEquals(builtBody, savedBody));

                var selectBody = new byte[] { 0x00, 0x03 };
                Check(
                    "0x0218 non-default cube body parses as crystal selection",
                    InventoryHandler.TryBuildCrystalContractBodyFromUpdateRequest(0x0218, selectBody, out var crystalBody)
                        && ByteEquals(crystalBody, selectBody));
                Check(
                    "0x0218 default cube body parses as crystal selection",
                    InventoryHandler.TryBuildCrystalContractBodyFromUpdateRequest(0x0218, new byte[] { 0x00, 0x00 }, out var defaultCrystalBody)
                        && ByteEquals(defaultCrystalBody, new byte[] { 0x00, 0x00 }));
                var emptyBody = new byte[] { 0x00, 0xFF };
                Check(
                    "0x0218 empty cube body parses as crystal selection",
                    InventoryHandler.TryBuildCrystalContractBodyFromUpdateRequest(0x0218, emptyBody, out var emptyCrystalBody)
                        && ByteEquals(emptyCrystalBody, emptyBody));
                Check(
                    "0x0218 little-endian slot body is not a crystal selection",
                    !InventoryHandler.TryBuildCrystalContractBodyFromUpdateRequest(0x0218, new byte[] { 0x05, 0x00 }, out _));
                Check(
                    "crystal selection saves 0x0300 init body",
                    dataSource.TrySaveCrystalContractSelection(CharacterId, crystalBody)
                        && ByteEquals(LoadInitBody(tempDb, CharacterId, 0x0300), selectBody));
                Check(
                    "empty crystal selection overwrites previous 0x0300 init body",
                    dataSource.TrySaveCrystalContractSelection(CharacterId, emptyCrystalBody)
                        && ByteEquals(LoadInitBody(tempDb, CharacterId, 0x0300), emptyBody));

                snapshot = dataSource.Load(CharacterId, AccountId);
                Check("select data source loads empty cube type", snapshot.InitializationSnapshot.CubeType == emptyBody[0]);
                Check("select data source loads empty cube grade", snapshot.InitializationSnapshot.CubeGrade == emptyBody[1]);
                Check("0x0300 builder returns empty cube body",
                    builder.TryBuild(snapshot, 0, out builtBody)
                        && ByteEquals(builtBody, emptyBody));

                SeedCharacter(tempDb, SeedCharacterId);
                var seededBody = new byte[] { 0x05, 0x00 };
                var stateRepo = new SqliteCharacterStateRepository(tempDb, ServerPaths.SchemaFilePath);
                stateRepo.SeedRawPacketsFromTemplates(SeedCharacterId, new List<SelectCharacterPacketTemplate>
                {
                    new SelectCharacterPacketTemplate
                    {
                        Command = 0x00,
                        Type = 0x0300,
                        Kind = SelectCharacterPacketTemplateKind.Raw,
                        PacketBytes = GamePacketEnvelopeBuilder.Build(0x00, 0x0300, seededBody),
                    },
                });

                var persistedSeedBody = LoadInitBody(tempDb, SeedCharacterId, 0x0300);
                Check("raw packet seeding persists 0x0300 body", ByteEquals(persistedSeedBody, seededBody));
            }
            finally
            {
                DeleteTempDatabase(tempDb);
            }

            Console.WriteLine($"=== result: {pass} PASS, {fail} FAIL ===");
            return fail == 0 ? 0 : 1;
        }

        private static void SeedCharacter(string databasePath, int characterId)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'crystal-contract-selftest', '');

INSERT OR IGNORE INTO characters
    (character_id, account_id, name, job, grow_type, level, town_id, area_id, pos_x, pos_y)
VALUES
    (@cid, @aid, @name, 0, 0, 1, 1, 1, 100, 100);

INSERT OR IGNORE INTO character_init_flags (character_id)
VALUES (@cid);";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@name", "CrystalContractSelfTest" + characterId.ToString());
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void SaveInitBody(string databasePath, int characterId, int notiType, byte[] body)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO character_init_bodies(character_id, noti_type, occurrence_index, body)
VALUES(@cid, @nt, 0, @body)
ON CONFLICT(character_id, noti_type, occurrence_index)
DO UPDATE SET body=@body;";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@nt", notiType);
                    cmd.Parameters.AddWithValue("@body", body);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static byte[] LoadInitBody(string databasePath, int characterId, int notiType)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT body FROM character_init_bodies WHERE character_id=@cid AND noti_type=@nt AND occurrence_index=0;";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@nt", notiType);
                    return cmd.ExecuteScalar() as byte[];
                }
            }
        }

        private static bool ByteEquals(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i])
                    return false;
            return true;
        }

        private static void DeleteTempDatabase(string path)
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
