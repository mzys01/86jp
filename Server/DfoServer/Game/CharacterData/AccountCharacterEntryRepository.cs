using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    public sealed class AccountCharacterEntry
    {
        public int EntryIndex { get; set; }
        public ushort SlotIndex { get; set; }
        public string Name { get; set; }
        public byte[] BodyAfterName { get; set; }
    }

    public sealed class AccountCharacterEntryRepository
    {
        private readonly string _connectionString;

        public AccountCharacterEntryRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public List<AccountCharacterEntry> LoadAll()
        {
            var list = new List<AccountCharacterEntry>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT entry_index, slot_index, name, body_after_name FROM account_character_entries ORDER BY entry_index", conn))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new AccountCharacterEntry
                        {
                            EntryIndex = r.GetInt32(0),
                            SlotIndex = (ushort)r.GetInt32(1),
                            Name = r.GetString(2),
                            BodyAfterName = r.IsDBNull(3) ? new byte[0] : (byte[])r[3],
                        });
                    }
                }
            }
            return list;
        }

        public void SaveAll(List<AccountCharacterEntry> entries)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                Sqlite.SqliteSchemaMigrator.EnsureColumns(conn, "account_character_entries", new[]
                {
                    ("entry_index", "INTEGER NOT NULL DEFAULT 0"),
                    ("slot_index", "INTEGER NOT NULL DEFAULT 0"),
                    ("name", "TEXT NOT NULL DEFAULT ''"),
                    ("name_bytes", "BLOB"),
                    ("body_after_name", "BLOB NOT NULL DEFAULT X''"),
                });
                using (var tx = conn.BeginTransaction())
                {
                    using (var del = new SqliteCommand("DELETE FROM account_character_entries", conn, tx))
                        del.ExecuteNonQuery();
                    foreach (var e in entries)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO account_character_entries (entry_index, slot_index, name, body_after_name) VALUES (@ei, @si, @n, @b)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@ei", e.EntryIndex);
                            cmd.Parameters.AddWithValue("@si", (int)e.SlotIndex);
                            cmd.Parameters.AddWithValue("@n", e.Name);
                            cmd.Parameters.AddWithValue("@b", e.BodyAfterName);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }
    }

    public sealed class GetUserInfoExtraPacketRepository
    {
        private readonly string _connectionString;

        public GetUserInfoExtraPacketRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public List<(byte command, ushort type, byte[] body)> LoadAll()
        {
            var list = new List<(byte, ushort, byte[])>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT command, noti_type, body FROM getuserinfo_extra_packets ORDER BY seq", conn))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(((byte)r.GetInt32(0), (ushort)r.GetInt32(1), (byte[])r[2]));
                }
            }
            return list;
        }

        public void SaveAll(List<(byte command, ushort type, byte[] body)> packets)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var del = new SqliteCommand("DELETE FROM getuserinfo_extra_packets", conn, tx))
                        del.ExecuteNonQuery();
                    for (int i = 0; i < packets.Count; i++)
                    {
                        var p = packets[i];
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO getuserinfo_extra_packets (seq, command, noti_type, body) VALUES (@s, @c, @t, @b)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@s", i);
                            cmd.Parameters.AddWithValue("@c", (int)p.command);
                            cmd.Parameters.AddWithValue("@t", (int)p.type);
                            cmd.Parameters.AddWithValue("@b", p.body);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }
    }
}
