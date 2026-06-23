using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    public sealed class SqliteCharacterProgressRepository
    {
        private readonly string _connectionString;

        public SqliteCharacterProgressRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        

        public SkillInfoSnapshot LoadSkills(int characterId)
        {
            var snapshot = new SkillInfoSnapshot();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();

                using (var cmd = new SqliteCommand(
                    "SELECT page_index, page_header, slot, skill_id, level, extra_values FROM character_skills WHERE character_id = @cid ORDER BY page_index, slot", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        var pages = new Dictionary<int, SkillInfoPageSnapshot>();
                        while (reader.Read())
                        {
                            var pageIdx = reader.GetInt32(0);
                            if (!pages.TryGetValue(pageIdx, out var page))
                            {
                                page = new SkillInfoPageSnapshot { HeaderValue = (ushort)reader.GetInt32(1) };
                                pages[pageIdx] = page;
                            }
                            int slot = reader.GetInt32(2);
                            if (slot < 0) continue;
                            var entry = new SkillInfoEntrySnapshot
                            {
                                Slot = (byte)slot,
                                SkillId = (ushort)reader.GetInt32(3),
                                Level = (byte)reader.GetInt32(4),
                            };
                            var extraBlob = reader.IsDBNull(5) ? null : (byte[])reader[5];
                            if (extraBlob != null)
                                foreach (var b in extraBlob)
                                    entry.ExtraValues.Add(b);
                            page.Entries.Add(entry);
                        }
                        for (int i = 0; i < 2; i++)
                        {
                            snapshot.Pages.Add(pages.ContainsKey(i) ? pages[i] : new SkillInfoPageSnapshot());
                        }
                    }
                }

                using (var cmd = new SqliteCommand(
                    "SELECT tail0, tail1 FROM character_skill_tail WHERE character_id = @cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            snapshot.Tail0 = (ushort)reader.GetInt32(0);
                            snapshot.Tail1 = (ushort)reader.GetInt32(1);
                        }
                    }
                }
            }
            return snapshot;
        }

        public void SaveSkills(int characterId, SkillInfoSnapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_skills WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new SqliteCommand("DELETE FROM character_skill_tail WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    for (int pageIdx = 0; pageIdx < snapshot.Pages.Count; pageIdx++)
                    {
                        var page = snapshot.Pages[pageIdx];
                        if (page.Entries.Count == 0)
                        {
                            using (var cmd = new SqliteCommand(
                                "INSERT INTO character_skills (character_id, page_index, page_header, slot, skill_id, level) VALUES (@cid, @page, @header, -1, 0, 0)", conn, tx))
                            {
                                cmd.Parameters.AddWithValue("@cid", characterId);
                                cmd.Parameters.AddWithValue("@page", pageIdx);
                                cmd.Parameters.AddWithValue("@header", (int)page.HeaderValue);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        foreach (var entry in page.Entries)
                        {
                            using (var cmd = new SqliteCommand(
                                "INSERT INTO character_skills (character_id, page_index, page_header, slot, skill_id, level, extra_values) VALUES (@cid, @page, @header, @slot, @sid, @lvl, @extra)", conn, tx))
                            {
                                cmd.Parameters.AddWithValue("@cid", characterId);
                                cmd.Parameters.AddWithValue("@page", pageIdx);
                                cmd.Parameters.AddWithValue("@header", (int)page.HeaderValue);
                                cmd.Parameters.AddWithValue("@slot", (int)entry.Slot);
                                cmd.Parameters.AddWithValue("@sid", (int)entry.SkillId);
                                cmd.Parameters.AddWithValue("@lvl", (int)entry.Level);
                                cmd.Parameters.AddWithValue("@extra", entry.ExtraValues.Count > 0 ? (object)entry.ExtraValues.ToArray() : DBNull.Value);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    using (var cmd = new SqliteCommand(
                        "INSERT INTO character_skill_tail (character_id, tail0, tail1) VALUES (@cid, @t0, @t1)", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@t0", (int)snapshot.Tail0);
                        cmd.Parameters.AddWithValue("@t1", (int)snapshot.Tail1);
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }
            }
        }

        public bool HasSkills(int characterId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM character_skills WHERE character_id = @cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
        }

        
        
        
        
        public void SwapSkillSlot(int characterId, int page, int slot1, int slot2)
        {
            if (slot1 == slot2) return;
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    MoveSkillSlot(conn, tx, characterId, page, slot1, -1);    
                    MoveSkillSlot(conn, tx, characterId, page, slot2, slot1); 
                    MoveSkillSlot(conn, tx, characterId, page, -1, slot2);    
                    tx.Commit();
                }
            }
        }

        private static void MoveSkillSlot(SqliteConnection conn, SqliteTransaction tx, int cid, int page, int fromSlot, int toSlot)
        {
            using (var cmd = new SqliteCommand(
                "UPDATE character_skills SET slot = @to WHERE character_id = @cid AND page_index = @page AND slot = @from", conn, tx))
            {
                cmd.Parameters.AddWithValue("@to", toSlot);
                cmd.Parameters.AddWithValue("@cid", cid);
                cmd.Parameters.AddWithValue("@page", page);
                cmd.Parameters.AddWithValue("@from", fromSlot);
                cmd.ExecuteNonQuery();
            }
        }

        public void ResetSkills(int characterId, ushort totalSp)
        {
            var snapshot = LoadSkills(characterId);
            foreach (var page in snapshot.Pages)
                page.Entries.Clear();
            if (snapshot.Pages.Count > 0)
                snapshot.Pages[0].HeaderValue = totalSp;
            SaveSkills(characterId, snapshot);
        }

        

        public CreatureItemListSnapshot LoadCreatures(int characterId)
        {
            var snapshot = new CreatureItemListSnapshot();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT creature_key, field04, mode_flag, progress_value, mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag FROM character_creatures WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            snapshot.Entries.Add(new CreatureItemEntrySnapshot
                            {
                                CreatureKey = reader.GetInt32(0),
                                Field04 = (byte)reader.GetInt32(1),
                                ModeFlag = (byte)reader.GetInt32(2),
                                ProgressValue32 = reader.GetInt32(3),
                                Mode1Field0A = (byte)reader.GetInt32(4),
                                Mode1Field0B = (byte)reader.GetInt32(5),
                                FieldAfterValue32 = (byte)reader.GetInt32(6),
                                CreatureTextBytes = reader.IsDBNull(7) ? new byte[0] : (byte[])reader[7],
                                TailFlag = (byte)reader.GetInt32(8),
                            });
                        }
                    }
                }
            }
            return snapshot;
        }

        public void SaveCreatures(int characterId, CreatureItemListSnapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_creatures WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    for (int i = 0; i < snapshot.Entries.Count; i++)
                    {
                        var entry = snapshot.Entries[i];
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_creatures (character_id, sort_order, creature_key, field04, mode_flag, progress_value, mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag) VALUES (@cid, @ord, @key, @f04, @mf, @pv, @m0a, @m0b, @fav, @txt, @tf)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@key", entry.CreatureKey);
                            cmd.Parameters.AddWithValue("@f04", (int)entry.Field04);
                            cmd.Parameters.AddWithValue("@mf", (int)entry.ModeFlag);
                            cmd.Parameters.AddWithValue("@pv", entry.ProgressValue32);
                            cmd.Parameters.AddWithValue("@m0a", (int)entry.Mode1Field0A);
                            cmd.Parameters.AddWithValue("@m0b", (int)entry.Mode1Field0B);
                            cmd.Parameters.AddWithValue("@fav", (int)entry.FieldAfterValue32);
                            cmd.Parameters.AddWithValue("@txt", entry.CreatureTextBytes != null && entry.CreatureTextBytes.Length > 0 ? (object)entry.CreatureTextBytes : DBNull.Value);
                            cmd.Parameters.AddWithValue("@tf", (int)entry.TailFlag);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                }
            }
        }

        public bool HasCreatures(int characterId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM character_creatures WHERE character_id = @cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
        }

        

        public void SeedFromSnapshot(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            if (!HasSkills(characterId) && snapshot.SkillInfo != null && snapshot.SkillInfo.Pages.Count > 0)
                SaveSkills(characterId, snapshot.SkillInfo);

            if (!HasCreatures(characterId) && snapshot.CreatureItemList != null && snapshot.CreatureItemList.Entries.Count > 0)
                SaveCreatures(characterId, snapshot.CreatureItemList);
        }
    }
}
