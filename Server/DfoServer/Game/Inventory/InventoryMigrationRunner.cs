using DfoServer.Game.Currency;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
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
            });
            // 点券/代币券/欢乐代币券账号化: 旧库补列(账号级钱包)
            DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(connection, "accounts", new[]
            {
                ("cera", "INTEGER NOT NULL DEFAULT 0"),
                ("token_cera", "INTEGER NOT NULL DEFAULT 0"),
                ("happy_token_cera", "INTEGER NOT NULL DEFAULT 0"),
            });
            DfoServer.Sqlite.SqliteSchemaMigrator.MigrateCharacterItemsUniqueConstraint(connection);
            CurrencyService.MigrateCeraFromPacketTemplates(connection);
            MigrateSubtype1BlobIfNeeded(connection);
            DfoServer.Game.CharacterData.SqliteSubtype0FieldsRepository.MigrateFromBlobIfNeeded(connection);
        }

        private void MigrateSubtype1BlobIfNeeded(SqliteConnection connection)
        {
            try
            {
                bool hasNewShape;
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(character_subtype1_fields);";
                    hasNewShape = false;
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            if (string.Equals(r.GetString(1), "name_tag_item_id", StringComparison.OrdinalIgnoreCase))
                                hasNewShape = true;
                }
                if (!hasNewShape)
                {
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = @"
ALTER TABLE character_subtype1_fields ADD COLUMN name_tag_item_id INTEGER NOT NULL DEFAULT 0;
ALTER TABLE character_subtype1_fields ADD COLUMN name_tag_expire_time INTEGER NOT NULL DEFAULT 0;
DELETE FROM character_subtype1_fields;";
                        cmd.ExecuteNonQuery();
                    }
                    FileLogger.Log("[MigrateSubtype1] 检测到旧列形(无 name_tag_item_id): 已加列并清空, 从 equip_list_blob 重迁移");
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM character_subtype1_fields;";
                    if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return;
                }
                var cids = new List<int>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT character_id FROM equipped_items WHERE equip_list_blob IS NOT NULL;";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read()) cids.Add(r.GetInt32(0));
                    }
                }
                foreach (var cid in cids)
                    DfoServer.Game.CharacterData.Subtype1BlobMigrator.Migrate(connection, cid);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[MigrateSubtype1] ERROR: {ex}");
            }
        }

    }
}
