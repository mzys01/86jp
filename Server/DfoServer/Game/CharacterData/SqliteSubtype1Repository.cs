using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Game.CharacterData
{
    public sealed class SqliteSubtype1Repository
    {
        private readonly string _connectionString;

        public SqliteSubtype1Repository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public bool HasData(int characterId)
        {
            using (var conn = Open())
            using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM character_subtype1_fields WHERE character_id=@cid", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        public UserInfoAdditionSnapshot Load(int characterId)
        {
            var snap = new UserInfoAdditionSnapshot();

            using (var conn = Open())
            {
                
                using (var cmd = new SqliteCommand(@"SELECT
                    stat_hp_max, stat_mp_max, stat_physical_attack, stat_physical_defense,
                    stat_magical_attack, stat_magical_defense, stat_fire_resistance, stat_water_resistance,
                    stat_dark_resistance, stat_light_resistance, stat_inventory_limit,
                    stat_hp_regen_speed, stat_mp_regen_speed, stat_move_speed, stat_attack_speed,
                    stat_cast_speed, stat_hit_recovery, stat_jump_power, stat_weight, stat_level,
                    name_tag_item_id, name_tag_expire_time, skill_tree_index, equipped_creature_level, equip_list_trailing,
                    manage_level, flag_byte, guild_power_war, server_timestamp, quest_shop_count,
                    progress1, progress2
                FROM character_subtype1_fields WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        snap.StatHpMax = (uint)r.GetInt64(0);
                        snap.StatMpMax = (uint)r.GetInt64(1);
                        snap.StatPhysicalAttack = (short)r.GetInt32(2);
                        snap.StatPhysicalDefense = (short)r.GetInt32(3);
                        snap.StatMagicalAttack = (short)r.GetInt32(4);
                        snap.StatMagicalDefense = (short)r.GetInt32(5);
                        snap.StatFireResistance = (short)r.GetInt32(6);
                        snap.StatWaterResistance = (short)r.GetInt32(7);
                        snap.StatDarkResistance = (short)r.GetInt32(8);
                        snap.StatLightResistance = (short)r.GetInt32(9);
                        snap.StatInventoryLimit = (uint)r.GetInt64(10);
                        snap.StatHpRegenSpeed = (ushort)r.GetInt32(11);
                        snap.StatMpRegenSpeed = (ushort)r.GetInt32(12);
                        snap.StatMoveSpeed = (uint)r.GetInt64(13);
                        snap.StatAttackSpeed = (ushort)r.GetInt32(14);
                        snap.StatCastSpeed = (ushort)r.GetInt32(15);
                        snap.StatHitRecovery = (ushort)r.GetInt32(16);
                        snap.StatJumpPower = (ushort)r.GetInt32(17);
                        snap.StatWeight = (uint)r.GetInt64(18);
                        snap.StatLevel = (byte)r.GetInt32(19);
                        snap.NameTagItemId = (uint)r.GetInt64(20);
                        snap.NameTagExpireTime = (uint)r.GetInt64(21);
                        snap.SkillTreeIndex = (byte)r.GetInt32(22);
                        snap.EquippedCreatureLevel = (byte)r.GetInt32(23);
                        snap.EquipListTrailing = r.IsDBNull(24) ? 0u : (uint)r.GetInt64(24);
                        snap.ManageLevel = (byte)r.GetInt32(25);
                        snap.FlagByte = (byte)r.GetInt32(26);
                        snap.GuildPowerWar = (uint)r.GetInt64(27);
                        snap.ServerTimestamp = (uint)r.GetInt64(28);
                        snap.QuestShopCount = (ushort)r.GetInt32(29);
                        snap.Progress1 = (uint)r.GetInt64(30);
                        snap.Progress2 = (uint)r.GetInt64(31);
                    }
                }

                
                using (var cmd = new SqliteCommand("SELECT exp, ex_equip_slot_stat FROM characters WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            snap.CharacExp = (uint)r.GetInt64(0);
                            snap.ExEquipSlotStat = (byte)r.GetInt32(1);
                        }
                    }
                }

                
                
                using (var cmd = new SqliteCommand("SELECT slot, item_id, raw_entry FROM character_equipped_entries WHERE character_id=@cid ORDER BY slot", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            int slot = r.GetInt32(0);
                            int itemId = r.GetInt32(1);
                            var raw = (byte[])r.GetValue(2);

                            int diff = Game.Inventory.InvenItem.VerifyRoundTrip(raw, out var item);
                            if (diff >= 0)
                                throw new System.IO.InvalidDataException(
                                    $"[Subtype1Repo] char {characterId} slot {slot} item {itemId}: InvenItem roundtrip 首差 offset {diff} (rawLen={raw.Length})");

                            snap.EquippedEntries.Add(new EquippedEntrySnapshot
                            {
                                Slot = slot,
                                ItemId = itemId,
                                RawEntry = raw,
                                Item = item,
                            });
                        }
                    }
                }

                
                using (var cmd = new SqliteCommand("SELECT dim_key, val1, val2 FROM character_dimensions WHERE character_id=@cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            snap.Dimensions.Add(new DimensionEntrySnapshot
                            {
                                Key = (uint)r.GetInt64(0),
                                Val1 = (byte)r.GetInt32(1),
                                Val2 = (byte)r.GetInt32(2),
                            });
                        }
                    }
                }

                
                using (var cmd = new SqliteCommand("SELECT flag1, flag2, flag3, flag4 FROM character_dimension_flags WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            snap.DimFlag1 = (byte)r.GetInt32(0);
                            snap.DimFlag2 = (byte)r.GetInt32(1);
                            snap.DimFlag3 = (byte)r.GetInt32(2);
                            snap.DimFlag4 = (byte)r.GetInt32(3);
                        }
                    }
                }

                
                using (var cmd = new SqliteCommand("SELECT value_u32, value_u16a, value_u16b FROM character_pvp_results WHERE character_id=@cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            snap.PvpResults.Add(new PvpResultEntrySnapshot
                            {
                                Value32 = (uint)r.GetInt64(0),
                                Value16A = (ushort)r.GetInt32(1),
                                Value16B = (ushort)r.GetInt32(2),
                            });
                        }
                    }
                }

                
                using (var cmd = new SqliteCommand("SELECT abuse_value FROM character_abuse_values WHERE character_id=@cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            snap.AbuseValues.Add((uint)r.GetInt64(0));
                    }
                }
            }

            return snap;
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }
    }
}
