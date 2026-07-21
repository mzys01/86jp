using DfoServer.Infrastructure;
using DfoServer.Game.Session;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Reflection;

namespace DfoServer.SelfTests
{
    public static class TowerOfDespairProgressSelfTest
    {
        private const int AccountId = 940020;
        private const int CharacterId = 940120;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "tower-of-despair-progress-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(connectionString);

                var assembly = typeof(TowerOfDespairProgressSelfTest).Assembly;
                var repositoryType = assembly.GetType(
                    "DfoServer.Game.Dungeon.TowerOfDespairProgressRepository");
                var serviceType = assembly.GetType(
                    "DfoServer.Game.Dungeon.TowerOfDespairProgressService");

                Check("progress repository exists", repositoryType != null, ref failures);
                Check("progress service exists", serviceType != null, ref failures);
                if (repositoryType == null || serviceType == null)
                    return Finish(failures);

                var repository = Activator.CreateInstance(
                    repositoryType,
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var service = Activator.CreateInstance(serviceType, repository);

                Check("fresh character starts on floor 1",
                    ResolveEntryDungeon(serviceType, service, CharacterId, 11008) == 11008,
                    ref failures);
                Check("non tower dungeon is unchanged",
                    ResolveEntryDungeon(serviceType, service, CharacterId, 144) == 144,
                    ref failures);

                RecordClear(serviceType, service, CharacterId, 11008);
                Check("clearing floor 1 redirects the base request to floor 2",
                    ResolveEntryDungeon(serviceType, service, CharacterId, 11008) == 11009,
                    ref failures);

                var reopenedRepository = Activator.CreateInstance(
                    repositoryType,
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var reopenedService = Activator.CreateInstance(serviceType, reopenedRepository);
                Check("floor progress survives repository recreation",
                    ResolveEntryDungeon(serviceType, reopenedService, CharacterId, 11008) == 11009,
                    ref failures);

                CheckEnterSelectDungeonFloorLayout(assembly, ref failures);

                RecordClear(serviceType, reopenedService, CharacterId, 11008);
                Check("replaying an older clear does not skip a floor",
                    ResolveEntryDungeon(serviceType, reopenedService, CharacterId, 11008) == 11009,
                    ref failures);

                RecordClear(serviceType, reopenedService, CharacterId, 11107);
                Check("floor progress is capped at floor 100",
                    ResolveEntryDungeon(serviceType, reopenedService, CharacterId, 11008) == 11107,
                    ref failures);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "PRAGMA user_version;";
                        Check("tower progress schema migration is version 29",
                            Convert.ToInt32(command.ExecuteScalar()) == 29,
                            ref failures);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] tower progress selftest exception: " + ex);
                failures++;
            }
            finally
            {
                DeleteDatabase(databasePath);
            }

            return Finish(failures);
        }

        private static int ResolveEntryDungeon(
            Type serviceType,
            object service,
            int characterId,
            int requestedDungeonId)
        {
            var method = serviceType.GetMethod(
                "ResolveEntryDungeonId",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException(serviceType.FullName, "ResolveEntryDungeonId");

            return Convert.ToInt32(method.Invoke(
                service,
                new object[] { characterId, requestedDungeonId }));
        }

        private static void RecordClear(
            Type serviceType,
            object service,
            int characterId,
            int clearedDungeonId)
        {
            var method = serviceType.GetMethod(
                "RecordClear",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException(serviceType.FullName, "RecordClear");

            method.Invoke(service, new object[] { characterId, clearedDungeonId });
        }

        private static void CheckEnterSelectDungeonFloorLayout(
            Assembly assembly,
            ref int failures)
        {
            var builderType = assembly.GetType(
                "DfoServer.Network.Builders.EnterSelectDungeonStateBuilder");
            var method = builderType?.GetMethod(
                "BuildEnterSelectDungeon",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(PlayerContext), typeof(int) },
                modifiers: null);

            Check("enter-select-dungeon builder accepts the current despair floor",
                method != null,
                ref failures);
            if (method == null)
                return;

            var player = new PlayerContext { UserId = 1002 };
            var body = (byte[])method.Invoke(null, new object[] { player, 8 });
            Check("enter-select-dungeon body keeps the proven 19-byte layout",
                body != null && body.Length == 19,
                ref failures);
            Check("enter-select-dungeon body writes the user id at offset 7",
                body != null
                    && body.Length >= 9
                    && BitConverter.ToUInt16(body, 7) == player.UserId,
                ref failures);
            Check("enter-select-dungeon body writes the despair floor at offset 14",
                body != null
                    && body.Length >= 16
                    && BitConverter.ToUInt16(body, 14) == 8,
                ref failures);
        }

        private static void SeedCharacter(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts(account_id, m_id, password_hash)
VALUES(@accountId, 'tower-of-despair-selftest', '');
INSERT OR IGNORE INTO characters(character_id, account_id, name, level)
VALUES(@characterId, @accountId, 'tower-of-despair-selftest', 86);";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteDatabase(string databasePath)
        {
            TryDelete(databasePath);
            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");
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

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private static int Finish(int failures)
        {
            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }
    }
}
