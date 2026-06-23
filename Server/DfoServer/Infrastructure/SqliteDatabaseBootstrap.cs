using Microsoft.Data.Sqlite;
using System.IO;

namespace DfoServer.Infrastructure
{
    public static class SqliteDatabaseBootstrap
    {
        public static string Initialize(string databasePath, string schemaFilePath)
        {
            EnsureDatabaseFile(databasePath);

            var connectionString = BuildConnectionString(databasePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = File.ReadAllText(schemaFilePath);
                    cmd.ExecuteNonQuery();
                }
            }
            return connectionString;
        }

        public static string BuildConnectionString(string databasePath)
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                ForeignKeys = true
            }.ConnectionString;
        }

        private static void EnsureDatabaseFile(string databasePath)
        {
            if (File.Exists(databasePath))
                return;

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }
    }
}
