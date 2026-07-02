using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryMigrationRunner
    {
        public void RunMigrations(SqliteConnection connection)
        {
            RunMigrationsInternal(connection);
        }

        internal void RunMigrationsInternal(SqliteConnection connection)
        {
            DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(connection, "characters", new[]
            {
                ("direction", "INTEGER NOT NULL DEFAULT 5"),
                ("area_state", "INTEGER NOT NULL DEFAULT 3"),
                ("appearance_blob", "BLOB"),
                ("delete_flag", "INTEGER NOT NULL DEFAULT 0"),
            });
            DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(connection, "character_equipped_entries", new[]
            {
                ("expire_time", "INTEGER NOT NULL DEFAULT 0"),
                ("equipment_lock_id", "INTEGER NOT NULL DEFAULT 0"),
            });
            DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(connection, "character_items", new[]
            {
                ("equipment_lock_id", "INTEGER NOT NULL DEFAULT 0"),
            });
            DfoServer.Sqlite.SqliteSchemaMigrator.MigrateCharacterItemLocks(connection);
            // 点券/代币券/欢乐代币券账号化: 旧库补列(账号级钱包)
            DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(connection, "accounts", new[]
            {
                ("cera", "INTEGER NOT NULL DEFAULT 0"),
                ("token_cera", "INTEGER NOT NULL DEFAULT 0"),
                ("happy_token_cera", "INTEGER NOT NULL DEFAULT 0"),
            });
            DfoServer.Sqlite.SqliteSchemaMigrator.MigrateCharacterItemsUniqueConstraint(connection);
        }
    }
}
