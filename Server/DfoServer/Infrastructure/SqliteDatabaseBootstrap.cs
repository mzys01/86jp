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

                // 点券/代币券/欢乐代币券账号化迁移。
                // 必须放在这里: 当前启动流程只走 Initialize(执行 schema), RunMigrations() 无人调用,
                // 而 schema 的 CREATE TABLE IF NOT EXISTS 对已存在的旧 accounts 表是空操作 → 三列补不上。
                // 1) 旧库补列; 2) 把历史角色级 coin 归集为账号级 cera 并回镜像到同账号所有角色。
                DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(conn, "accounts", new[]
                {
                    ("cera", "INTEGER NOT NULL DEFAULT 0"),
                    ("token_cera", "INTEGER NOT NULL DEFAULT 0"),
                    ("happy_token_cera", "INTEGER NOT NULL DEFAULT 0"),
                });
                DfoServer.Game.Inventory.CurrencyService.MigrateCeraFromPacketTemplates(conn);
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
