using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Sqlite
{
    
    
    
    internal static class SqliteSchemaMigrator
    {
        public static void EnsureColumns(SqliteConnection connection, string tableName, IEnumerable<(string Name, string Definition)> requiredColumns)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentException("tableName is empty", nameof(tableName));

            var existing = ReadColumnNames(connection, tableName);
            if (existing.Count == 0)
                return; 

            foreach (var (name, definition) in requiredColumns)
            {
                if (existing.Contains(name))
                    continue;

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {name} {definition};";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static void MigrateCharacterItemsUniqueConstraint(SqliteConnection connection)
        {
            if (connection == null) return;

            var createSql = ReadTableCreateSql(connection, "character_items");
            if (createSql == null) return;
            if (createSql.Contains("slot_index, item_kind")) return;

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS character_items_new (
    item_uid INTEGER PRIMARY KEY AUTOINCREMENT,
    owner_scope TEXT NOT NULL CHECK (owner_scope IN ('character', 'account')),
    owner_id INTEGER NOT NULL,
    character_id INTEGER,
    list_type INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_template_id INTEGER NOT NULL,
    item_kind TEXT NOT NULL DEFAULT 'unknown' CHECK (item_kind IN ('unknown', 'stackable', 'equipment', 'avatar', 'pet', 'special')),
    stack_count INTEGER NOT NULL DEFAULT 0,
    instance_value INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_flag INTEGER NOT NULL DEFAULT 0,
    option_value INTEGER NOT NULL DEFAULT 0,
    expire_time INTEGER NOT NULL DEFAULT 0,
    marker_16 INTEGER NOT NULL DEFAULT 0,
    pet_serial_or_handle INTEGER NOT NULL DEFAULT 0,
    extra_json TEXT NOT NULL DEFAULT '{}',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(owner_scope, owner_id, list_type, slot_index, item_kind),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE SET NULL
);
INSERT INTO character_items_new SELECT * FROM character_items;
DROP TABLE character_items;
ALTER TABLE character_items_new RENAME TO character_items;
CREATE INDEX IF NOT EXISTS idx_character_items_owner_container
    ON character_items(owner_scope, owner_id, list_type, slot_index);
CREATE INDEX IF NOT EXISTS idx_character_items_template
    ON character_items(item_template_id);
CREATE INDEX IF NOT EXISTS idx_character_items_character
    ON character_items(character_id, list_type, slot_index);";
                cmd.ExecuteNonQuery();
            }
        }

        private static string ReadTableCreateSql(SqliteConnection connection, string tableName)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@name;";
                cmd.Parameters.AddWithValue("@name", tableName);
                var result = cmd.ExecuteScalar();
                return result as string;
            }
        }

        private static HashSet<string> ReadColumnNames(SqliteConnection connection, string tableName)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({tableName});";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        set.Add(reader.GetString(1));
                }
            }
            return set;
        }
    }
}
