using System;
using DfoServer.Game.Currency;
using Microsoft.Data.Sqlite;

namespace DfoServer.Sqlite
{
    // 版本化迁移: PRAGMA user_version 门控, 每条迁移每库只跑一次。
    //
    // 规则:
    //   1) 新增迁移 = 在 Steps 末尾追加下一个版本号, 禁止修改/删除已发布条目。
    //   2) item_schema.sql 始终保持"新库的完整最终形态"; 迁移只负责把旧库升上来。
    //      加列时两边都要写(schema + 迁移), 否则旧库缺列。
    //   3) 迁移体保持幂等(加列先查存在/重建先查建表SQL)作双保险, 但版本门控保证正常路径只执行一次。
    //   4) 破坏性变更(删列/改约束)用表重建或 DROP COLUMN, SQL 批内嵌 BEGIN/COMMIT 保证原子性。
    internal static class SqliteMigrations
    {
        private static readonly (int Version, string Name, Action<SqliteConnection> Apply)[] Steps =
        {
            (1, "accounts 账号级货币列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "accounts", new[]
            {
                ("cera", "INTEGER NOT NULL DEFAULT 0"),
                ("token_cera", "INTEGER NOT NULL DEFAULT 0"),
                ("happy_token_cera", "INTEGER NOT NULL DEFAULT 0"),
                ("lucky_star", "INTEGER NOT NULL DEFAULT 0"),
            })),

            // 原 SqliteCharacterRepository 构造函数内散装补列(含原 InventoryMigrationRunner 独有4列)
            (2, "characters town/外观/进度列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "characters", new[]
            {
                ("direction", "INTEGER NOT NULL DEFAULT 5"),
                ("area_state", "INTEGER NOT NULL DEFAULT 3"),
                ("name_bytes", "BLOB"),
                ("appearance_blob", "BLOB"),
                ("delete_flag", "INTEGER NOT NULL DEFAULT 0"),
                ("exp", "INTEGER NOT NULL DEFAULT 0"),
                ("ex_equip_slot_stat", "INTEGER NOT NULL DEFAULT 0"),
                ("pvp_grade", "INTEGER NOT NULL DEFAULT 0"),
                ("pvp_rating_grade", "INTEGER NOT NULL DEFAULT 0"),
                ("user_state", "INTEGER NOT NULL DEFAULT 0"),
                ("bonus_sp", "INTEGER NOT NULL DEFAULT 0"),
                ("bonus_tp", "INTEGER NOT NULL DEFAULT 0"),
                ("clone_title_item_id", "INTEGER NOT NULL DEFAULT 0"),
            })),

            (3, "character_equipped_entries 期限/锁列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "character_equipped_entries", new[]
            {
                ("expire_time", "INTEGER NOT NULL DEFAULT 0"),
                ("equipment_lock_id", "INTEGER NOT NULL DEFAULT 0"),
            })),

            (4, "character_items equipment_lock_id 列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "character_items", new[]
            {
                ("equipment_lock_id", "INTEGER NOT NULL DEFAULT 0"),
            })),

            (5, "character_item_locks 表重建", SqliteSchemaMigrator.MigrateCharacterItemLocks),

            (6, "character_items 唯一键重建(含item_kind)", SqliteSchemaMigrator.MigrateCharacterItemsUniqueConstraint),

            (7, "character_init_flags 角色选项blob列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "character_init_flags", new[]
            {
                ("character_option_blob", "BLOB"),
            })),

            (8, "accounts 晶块6列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "accounts", new[]
            {
                ("cube_black", "INTEGER NOT NULL DEFAULT 0"),
                ("cube_white", "INTEGER NOT NULL DEFAULT 0"),
                ("cube_red", "INTEGER NOT NULL DEFAULT 0"),
                ("cube_blue", "INTEGER NOT NULL DEFAULT 0"),
                ("cube_clear", "INTEGER NOT NULL DEFAULT 0"),
                ("cube_gold", "INTEGER NOT NULL DEFAULT 0"),
            })),

            (9, "晶块从 character_items 归集账号", CurrencyService.MigrateCubeFragmentsFromCharacterItems),

            // 原 AccountCharacterEntryRepository.SaveAll 内散装补列
            (10, "account_character_entries 选角条目列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "account_character_entries", new[]
            {
                ("entry_index", "INTEGER NOT NULL DEFAULT 0"),
                ("slot_index", "INTEGER NOT NULL DEFAULT 0"),
                ("name", "TEXT NOT NULL DEFAULT ''"),
                ("name_bytes", "BLOB"),
                ("body_after_name", "BLOB NOT NULL DEFAULT X''"),
            })),

            // 原 SqliteUserInfoBlobRepository.SaveGetUserInfoResponseBlob 内散装补列
            (11, "get_userinfo_template response_blob 列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "get_userinfo_template", new[]
            {
                ("response_blob", "BLOB"),
            })),

            // characters.gold/coin 是创建时写入后再无人读写的影子列(游戏内金币=character_items slot0,
            // 点券=accounts.cera), 留着只会误导调试。schema 已同步移除。
            (12, "characters 删除影子列 gold/coin", conn =>
                SqliteSchemaMigrator.DropColumnsIfExist(conn, "characters", "gold", "coin")),
        };

        public static void Apply(SqliteConnection connection)
        {
            long current = ReadUserVersion(connection);
            foreach (var (version, name, apply) in Steps)
            {
                if (version <= current)
                    continue;

                apply(connection);
                SetUserVersion(connection, version);
                FileLogger.Log($"[Db] migration v{version} applied: {name}");
            }
        }

        private static long ReadUserVersion(SqliteConnection connection)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version;";
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        private static void SetUserVersion(SqliteConnection connection, int version)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA user_version = {version};";
                cmd.ExecuteNonQuery();
            }
        }
    }
}
