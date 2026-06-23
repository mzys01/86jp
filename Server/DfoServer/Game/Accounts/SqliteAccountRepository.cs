using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace DfoServer.Game.Accounts
{
    public sealed class SqliteAccountRepository : IAccountRepository
    {
        private readonly string _connectionString;

        public SqliteAccountRepository(string databasePath, string schemaFilePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("databasePath is empty", nameof(databasePath));
            if (string.IsNullOrWhiteSpace(schemaFilePath))
                throw new ArgumentException("schemaFilePath is empty", nameof(schemaFilePath));

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public AccountRecord GetById(int accountId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT account_id, m_id, password_hash, last_login_ip, last_login_at, created_at
                                    FROM accounts WHERE account_id = @id;";
                cmd.Parameters.AddWithValue("@id", accountId);
                using (var reader = cmd.ExecuteReader())
                    return reader.Read() ? Map(reader) : null;
            }
        }

        public AccountRecord GetByMid(string mId)
        {
            if (string.IsNullOrEmpty(mId)) return null;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT account_id, m_id, password_hash, last_login_ip, last_login_at, created_at
                                    FROM accounts WHERE m_id = @mid;";
                cmd.Parameters.AddWithValue("@mid", mId);
                using (var reader = cmd.ExecuteReader())
                    return reader.Read() ? Map(reader) : null;
            }
        }

        public int Create(string mId, string passwordHash)
        {
            if (string.IsNullOrEmpty(mId)) throw new ArgumentException("mId is empty", nameof(mId));

            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO accounts (m_id, password_hash) VALUES (@mid, @pwd);
                                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@mid", mId);
                cmd.Parameters.AddWithValue("@pwd", passwordHash ?? string.Empty);
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        public void UpdateLastLogin(int accountId, string ip, DateTime when)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE accounts SET last_login_ip = @ip, last_login_at = @at
                                    WHERE account_id = @id;";
                cmd.Parameters.AddWithValue("@ip", ip ?? string.Empty);
                cmd.Parameters.AddWithValue("@at", when.ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@id", accountId);
                cmd.ExecuteNonQuery();
            }
        }

        private static AccountRecord Map(IDataRecord r)
        {
            return new AccountRecord
            {
                AccountId = r.GetInt32(0),
                MId = r.GetString(1),
                PasswordHash = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                LastLoginIp = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                LastLoginAt = r.IsDBNull(4) ? (DateTime?)null : ParseDate(r.GetString(4)),
                CreatedAt = ParseDate(r.GetString(5)),
            };
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
