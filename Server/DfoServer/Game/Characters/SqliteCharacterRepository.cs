using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using DfoServer.Sqlite;

namespace DfoServer.Game.Characters
{
    public sealed class SqliteCharacterRepository : ICharacterRepository
    {
        private readonly string _connectionString;

        public SqliteCharacterRepository(string databasePath, string schemaFilePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("databasePath is empty", nameof(databasePath));
            if (string.IsNullOrWhiteSpace(schemaFilePath))
                throw new ArgumentException("schemaFilePath is empty", nameof(schemaFilePath));

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                SqliteSchemaMigrator.EnsureColumns(conn, "characters", new[]
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
                });
            }
        }

        public CharacterRecord GetById(int characterId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SelectColumns + " WHERE character_id = @id;";
                cmd.Parameters.AddWithValue("@id", characterId);
                using (var reader = cmd.ExecuteReader())
                    return reader.Read() ? Map(reader) : null;
            }
        }

        public IReadOnlyList<CharacterRecord> ListByAccount(int accountId)
        {
            var list = new List<CharacterRecord>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SelectColumns + " WHERE account_id = @aid AND delete_flag = 0 ORDER BY character_id;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        list.Add(Map(reader));
                }
            }
            return list;
        }

        public int Create(CharacterRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.Name == null || record.Name.Length == 0) throw new ArgumentException("character name is empty", nameof(record));

            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO characters
    (character_id, account_id, name, job, grow_type, level, gold, coin,
     town_id, area_id, pos_x, pos_y, direction, area_state, appearance_blob, delete_flag)
VALUES
    (@cid, @aid, @name, @job, @grow, @lvl, @gold, @coin,
     @town, @area, @px, @py, @dir, @astate, @blob, 0);
SELECT character_id FROM characters WHERE rowid = last_insert_rowid();";

                if (record.CharacterId > 0)
                    cmd.Parameters.AddWithValue("@cid", record.CharacterId);
                else
                    cmd.Parameters.AddWithValue("@cid", DBNull.Value);

                cmd.Parameters.AddWithValue("@aid", record.AccountId);
                cmd.Parameters.AddWithValue("@name", record.Name);
                cmd.Parameters.AddWithValue("@job", record.Job);
                cmd.Parameters.AddWithValue("@grow", record.GrowType);
                cmd.Parameters.AddWithValue("@lvl", record.Level);
                cmd.Parameters.AddWithValue("@gold", record.Gold);
                cmd.Parameters.AddWithValue("@coin", record.Coin);
                cmd.Parameters.AddWithValue("@town", record.TownId);
                cmd.Parameters.AddWithValue("@area", record.AreaId);
                cmd.Parameters.AddWithValue("@px", record.PosX);
                cmd.Parameters.AddWithValue("@py", record.PosY);
                cmd.Parameters.AddWithValue("@dir", record.Direction);
                cmd.Parameters.AddWithValue("@astate", record.AreaState);
                cmd.Parameters.AddWithValue("@blob", (object)CharacterAppearanceCodec.Encode(record.Appearance) ?? DBNull.Value);

                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        public void UpdatePosition(int characterId, byte townId, byte areaId, short posX, short posY, byte direction, byte areaState)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE characters
                                    SET town_id = @town, area_id = @area, pos_x = @px, pos_y = @py,
                                        direction = @dir, area_state = @astate,
                                        updated_at = CURRENT_TIMESTAMP
                                    WHERE character_id = @id;";
                cmd.Parameters.AddWithValue("@town", townId);
                cmd.Parameters.AddWithValue("@area", areaId);
                cmd.Parameters.AddWithValue("@px", posX);
                cmd.Parameters.AddWithValue("@py", posY);
                cmd.Parameters.AddWithValue("@dir", direction);
                cmd.Parameters.AddWithValue("@astate", areaState);
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        public void UpdateSeedFields(int characterId, byte[] name, byte job, byte growType, byte level,
            byte pvpGrade, byte pvpRatingGrade, byte userState,
            CharacterAppearanceEntry[] appearance, DateTime? createdAt = null)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE characters
                                    SET name = @name, job = @job, grow_type = @grow, level = @lvl,
                                        pvp_grade = @pvpG, pvp_rating_grade = @pvpR, user_state = @ustate,
                                        appearance_blob = @blob, created_at = @cat, updated_at = CURRENT_TIMESTAMP
                                    WHERE character_id = @id;";
                cmd.Parameters.AddWithValue("@name", (object)name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@job", (int)job);
                cmd.Parameters.AddWithValue("@grow", (int)growType);
                cmd.Parameters.AddWithValue("@lvl", (int)level);
                cmd.Parameters.AddWithValue("@pvpG", (int)pvpGrade);
                cmd.Parameters.AddWithValue("@pvpR", (int)pvpRatingGrade);
                cmd.Parameters.AddWithValue("@ustate", (int)userState);
                cmd.Parameters.AddWithValue("@blob", (object)CharacterAppearanceCodec.Encode(appearance) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@cat", (createdAt ?? DateTime.UtcNow).ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        public void UpdateLevelAndExp(int characterId, byte level, uint exp)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE characters SET level = @lvl, exp = @exp, updated_at = CURRENT_TIMESTAMP WHERE character_id = @id;";
                cmd.Parameters.AddWithValue("@lvl", (int)level);
                cmd.Parameters.AddWithValue("@exp", (long)exp);
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        public void UpdateAppearance(int characterId, CharacterAppearanceEntry[] appearance)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE characters
                                    SET appearance_blob = @blob, updated_at = CURRENT_TIMESTAMP
                                    WHERE character_id = @id;";
                cmd.Parameters.AddWithValue("@blob", (object)CharacterAppearanceCodec.Encode(appearance) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        public void SoftDelete(int characterId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE characters SET delete_flag = 1, updated_at = CURRENT_TIMESTAMP
                                    WHERE character_id = @id;";
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        public CharacterRecord GetByName(string name)
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(name ?? "");
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SelectColumns + " WHERE (name = @name OR name = @nameBytes) AND delete_flag = 0;";
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@nameBytes", nameBytes);
                using (var reader = cmd.ExecuteReader())
                    return reader.Read() ? Map(reader) : null;
            }
        }

        public int CountByAccount(int accountId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM characters WHERE account_id = @aid AND delete_flag = 0;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        private const string SelectColumns = @"
SELECT character_id, account_id, CAST(name AS BLOB), job, grow_type, level, gold, coin,
       town_id, area_id, pos_x, pos_y, direction, area_state, appearance_blob,
       delete_flag, created_at, updated_at, exp, ex_equip_slot_stat,
       pvp_grade, pvp_rating_grade, user_state, bonus_sp, bonus_tp
FROM characters";

        private static CharacterRecord Map(IDataRecord r)
        {
            var appearBlob = r.IsDBNull(14) ? null : (byte[])r.GetValue(14);
            return new CharacterRecord
            {
                CharacterId = r.GetInt32(0),
                AccountId = r.GetInt32(1),
                Name = r.IsDBNull(2) ? null : ReadNameBlob(r, 2),
                Job = (byte)r.GetInt32(3),
                GrowType = (byte)r.GetInt32(4),
                Level = (byte)r.GetInt32(5),
                Gold = r.GetInt64(6),
                Coin = r.GetInt64(7),
                TownId = (byte)r.GetInt32(8),
                AreaId = (byte)r.GetInt32(9),
                PosX = (short)r.GetInt32(10),
                PosY = (short)r.GetInt32(11),
                Direction = (byte)r.GetInt32(12),
                AreaState = (byte)r.GetInt32(13),
                Appearance = CharacterAppearanceCodec.Decode(appearBlob),
                Deleted = r.GetInt32(15) != 0,
                CreatedAt = ParseDate(r.GetString(16)),
                UpdatedAt = ParseDate(r.GetString(17)),
                Exp = r.FieldCount > 18 && !r.IsDBNull(18) ? (uint)r.GetInt64(18) : 0u,
                ExEquipSlotStat = r.FieldCount > 19 && !r.IsDBNull(19) ? (byte)r.GetInt32(19) : (byte)0,
                PvpGrade = r.FieldCount > 20 && !r.IsDBNull(20) ? (byte)r.GetInt32(20) : (byte)0,
                PvpRatingGrade = r.FieldCount > 21 && !r.IsDBNull(21) ? (byte)r.GetInt32(21) : (byte)0,
                UserState = r.FieldCount > 22 && !r.IsDBNull(22) ? (byte)r.GetInt32(22) : (byte)0,
                BonusSp = r.FieldCount > 23 && !r.IsDBNull(23) ? r.GetInt32(23) : 0,
                BonusTp = r.FieldCount > 24 && !r.IsDBNull(24) ? r.GetInt32(24) : 0,
            };
        }

        private static byte[] ReadNameBlob(IDataRecord r, int ordinal)
        {
            var val = r.GetValue(ordinal);
            if (val is byte[] b) return b;
            if (val is string s) return System.Text.Encoding.GetEncoding(936).GetBytes(s);
            return null;
        }

        private static DateTime ParseDate(string text)
        {
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt;
            return DateTime.MinValue;
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }
    }
}
