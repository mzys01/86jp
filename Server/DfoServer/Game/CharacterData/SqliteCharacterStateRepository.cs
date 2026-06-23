using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    public sealed class SqliteCharacterStateRepository
    {
        private readonly string _connectionString;

        public SqliteCharacterStateRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        

        public void LoadFlags(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    @"SELECT shop_coin_event_flag, level60_ui_state, pc_room_state, expert_job_blob, champion_break_blob,
                             boss_tower_placeholder, mailbox_loaded_count, mailbox_mode, mailbox_not_loaded_count, mailbox_unknown_count_c,
                             event_info_tail_byte, hotkey_key_type,
                             main_game_option_blob, quickchat_bank0, quickchat_bank1, charac_invisible_falgs_payload_len,
                             racing_dungeon_current_enter_count, racing_dungeon_group_flags,
                             ack_account_reg_time, ack_premium_blob, ack_quest_display_ids,
                             ack_char_slot_index, ack_fatigue_battery, ack_fatigue_grownup_buff,
                             ack_trade_punish_flag, ack_extra_field_86jp, ack_reserved_8b,
                             ack_tutorial_skipable, ack_post_tutorial_u16, ack_unread_tail
                      FROM character_init_flags WHERE character_id = @cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return;
                        snapshot.ShopCoinEventFlag = (byte)reader.GetInt32(0);
                        snapshot.Level60UiState = (byte)reader.GetInt32(1);
                        snapshot.PcRoomPlayTimeState = (byte)reader.GetInt32(2);

                        var expertBlob = reader.IsDBNull(3) ? null : (byte[])reader[3];
                        if (expertBlob != null)
                            DeserializeExpertJobInfo(expertBlob, snapshot.ExpertJobInfo);

                        var championBlob = reader.IsDBNull(4) ? null : (byte[])reader[4];
                        if (championBlob != null && championBlob.Length >= 9)
                            DeserializeChampionBreak(championBlob, snapshot.ChampionBreakSystem);

                        if (!reader.IsDBNull(5))
                            snapshot.BossTowerPlaceholder = reader.GetInt32(5);

                        snapshot.LoadedMailCount = reader.IsDBNull(6) ? (byte)0 : (byte)reader.GetInt32(6);
                        snapshot.MailboxMode = reader.IsDBNull(7) ? (byte)0 : (byte)reader.GetInt32(7);
                        snapshot.NotLoadedMailCount = reader.IsDBNull(8) ? (ushort)0 : (ushort)reader.GetInt32(8);
                        snapshot.MailboxUnknownCountC = reader.IsDBNull(9) ? (ushort)0 : (ushort)reader.GetInt32(9);

                        snapshot.EventInfoTailByte = reader.IsDBNull(10) ? (byte)0 : (byte)reader.GetInt32(10);
                        snapshot.HotkeyKeyType = reader.IsDBNull(11) ? (byte)0 : (byte)reader.GetInt32(11);

                        snapshot.MainGameOptionBlob = reader.IsDBNull(12) ? null : (byte[])reader[12];
                        snapshot.QuickchatBank0 = reader.IsDBNull(13) ? null : (byte[])reader[13];
                        snapshot.QuickchatBank1 = reader.IsDBNull(14) ? null : (byte[])reader[14];
                        snapshot.CharacInvisibleFalgsPayloadLen = reader.IsDBNull(15) ? 0u : (uint)reader.GetInt64(15);

                        snapshot.RacingDungeonCurrentEnterCount = reader.IsDBNull(16) ? 0u : (uint)reader.GetInt64(16);
                        if (!reader.IsDBNull(17))
                        {
                            var flagsBlob = (byte[])reader[17];
                            Buffer.BlockCopy(flagsBlob, 0, snapshot.RacingDungeonGroupFlags, 0, Math.Min(flagsBlob.Length, snapshot.RacingDungeonGroupFlags.Length));
                        }

                        
                        snapshot.AckAccountRegTime = reader.IsDBNull(18) ? 0 : (int)reader.GetInt64(18);
                        var premBlob = reader.IsDBNull(19) ? null : (byte[])reader[19];
                        if (premBlob != null)
                            DeserializeAckPremiums(premBlob, snapshot.AckPremiums);
                        snapshot.AckQuestDisplayIds = reader.IsDBNull(20) ? null : (byte[])reader[20];
                        snapshot.AckCharSlotIndex = reader.IsDBNull(21) ? (byte)0 : (byte)reader.GetInt32(21);
                        snapshot.AckFatigueBattery = reader.IsDBNull(22) ? (ushort)0 : (ushort)reader.GetInt32(22);
                        snapshot.AckFatigueGrownUpBuff = reader.IsDBNull(23) ? (ushort)0 : (ushort)reader.GetInt32(23);
                        snapshot.AckTradePunishFlag = reader.IsDBNull(24) ? (byte)0 : (byte)reader.GetInt32(24);
                        snapshot.AckExtraField86JP = reader.IsDBNull(25) ? (ushort)0 : (ushort)reader.GetInt32(25);
                        snapshot.AckReserved8B = reader.IsDBNull(26) ? null : (byte[])reader[26];
                        snapshot.AckTutorialSkipable = reader.IsDBNull(27) ? (byte)0 : (byte)reader.GetInt32(27);
                        snapshot.AckPostTutorialU16 = reader.IsDBNull(28) ? (ushort)0 : (ushort)reader.GetInt32(28);
                        snapshot.AckUnreadTail = reader.IsDBNull(29) ? null : (byte[])reader[29];
                    }
                }

                snapshot.GrowthWeaponStageIds.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT stage_id FROM character_growth_weapon_stages WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.GrowthWeaponStageIds.Add((byte)reader.GetInt32(0));
                    }
                }

                snapshot.ShowEffects.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT effect_index, duration_seconds FROM character_show_effects WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            snapshot.ShowEffects.Add(new ShowEffectEntrySnapshot
                            {
                                EffectIndex = (byte)reader.GetInt32(0),
                                DurationSeconds = (uint)reader.GetInt64(1),
                            });
                        }
                    }
                }

                snapshot.PvpMissions.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT mission_id, progress_value FROM character_pvp_missions WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            snapshot.PvpMissions.Add(new PvpMissionEntrySnapshot
                            {
                                MissionId = (uint)reader.GetInt64(0),
                                ProgressValue = (uint)reader.GetInt64(1),
                            });
                        }
                    }
                }

                snapshot.DungeonPermissions.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT dungeon_id, clear_state FROM character_dungeon_permissions WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            snapshot.DungeonPermissions.Add(new DungeonPermissionEntrySnapshot
                            {
                                DungeonId = (ushort)reader.GetInt32(0),
                                ClearState = (byte)reader.GetInt32(1),
                            });
                        }
                    }
                }

                snapshot.EventInfoEntries.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT repeat_event_index, event_data FROM character_event_info WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var entry = new EventInfoEntrySnapshot
                            {
                                RepeatEventIndex = (ushort)reader.GetInt32(0),
                            };
                            if (!reader.IsDBNull(1))
                            {
                                var blob = (byte[])reader[1];
                                Buffer.BlockCopy(blob, 0, entry.EventData, 0, Math.Min(blob.Length, entry.EventData.Length));
                            }
                            snapshot.EventInfoEntries.Add(entry);
                        }
                    }
                }

                snapshot.HotkeyConfigSlots.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT hotkey_value FROM character_hotkey_slots WHERE character_id = @cid ORDER BY slot_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.HotkeyConfigSlots.Add((ushort)reader.GetInt32(0));
                    }
                }

                snapshot.CharacInvisibleFalgs.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT slot_index, flag_value FROM character_invisible_falgs WHERE character_id = @cid ORDER BY slot_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            snapshot.CharacInvisibleFalgs.Add(new CharacInvisibleFalgEntrySnapshot
                            {
                                SlotIndex = (ushort)reader.GetInt32(0),
                                FlagValue = (byte)reader.GetInt32(1),
                            });
                        }
                    }
                }

                snapshot.RacingDungeonGroups.Clear();
                var racingGroupsByIndex = new Dictionary<int, RacingDungeonGroupSnapshot>();
                using (var cmd = new SqliteCommand(
                    "SELECT group_index, group_id FROM character_racing_dungeon_groups WHERE character_id = @cid ORDER BY group_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var groupIndex = reader.GetInt32(0);
                            var group = new RacingDungeonGroupSnapshot { GroupId = (uint)reader.GetInt64(1) };
                            racingGroupsByIndex[groupIndex] = group;
                            snapshot.RacingDungeonGroups.Add(group);
                        }
                    }
                }
                using (var cmd = new SqliteCommand(
                    "SELECT group_index, entry_index, track_like_id, value_a, value_b FROM character_racing_dungeon_entries WHERE character_id = @cid ORDER BY group_index, entry_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var groupIndex = reader.GetInt32(0);
                            if (!racingGroupsByIndex.TryGetValue(groupIndex, out var group))
                                continue;
                            group.Entries.Add(new RacingDungeonEntrySnapshot
                            {
                                TrackLikeId = (uint)reader.GetInt64(2),
                                ValueA = (uint)reader.GetInt64(3),
                                ValueB = (uint)reader.GetInt64(4),
                            });
                        }
                    }
                }

                snapshot.RacingDungeonTailIds.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT id_value FROM character_racing_dungeon_tail_ids WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.RacingDungeonTailIds.Add((uint)reader.GetInt64(0));
                    }
                }
            }
        }

        public void SaveFlags(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand(
                        @"INSERT OR REPLACE INTO character_init_flags
                          (character_id, shop_coin_event_flag, level60_ui_state, pc_room_state, expert_job_blob, champion_break_blob,
                           boss_tower_placeholder, mailbox_loaded_count, mailbox_mode, mailbox_not_loaded_count, mailbox_unknown_count_c,
                           event_info_tail_byte, hotkey_key_type,
                           main_game_option_blob, quickchat_bank0, quickchat_bank1, charac_invisible_falgs_payload_len,
                           racing_dungeon_current_enter_count, racing_dungeon_group_flags,
                           ack_account_reg_time, ack_premium_blob, ack_quest_display_ids,
                           ack_char_slot_index, ack_fatigue_battery, ack_fatigue_grownup_buff,
                           ack_trade_punish_flag, ack_extra_field_86jp, ack_reserved_8b,
                           ack_tutorial_skipable, ack_post_tutorial_u16, ack_unread_tail)
                          VALUES (@cid, @scef, @l60, @pcr, @expert, @champ,
                                  @btp, @mlc, @mm, @mnlc, @mukc,
                                  @eitb, @hkt,
                                  @mgo, @qb0, @qb1, @ciplen,
                                  @rdcc, @rdgf,
                                  @ackRegTime, @ackPremBlob, @ackQuestDisp,
                                  @ackSlot, @ackFatBat, @ackFatGrown,
                                  @ackTrade, @ackExtra86, @ackRes8b,
                                  @ackTutSkip, @ackPostTut, @ackTail)", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@scef", (int)snapshot.ShopCoinEventFlag);
                        cmd.Parameters.AddWithValue("@l60", (int)snapshot.Level60UiState);
                        cmd.Parameters.AddWithValue("@pcr", (int)snapshot.PcRoomPlayTimeState);
                        cmd.Parameters.AddWithValue("@expert", SerializeExpertJobInfo(snapshot.ExpertJobInfo));
                        cmd.Parameters.AddWithValue("@champ", SerializeChampionBreak(snapshot.ChampionBreakSystem));
                        cmd.Parameters.AddWithValue("@btp", snapshot.BossTowerPlaceholder);
                        cmd.Parameters.AddWithValue("@mlc", (int)snapshot.LoadedMailCount);
                        cmd.Parameters.AddWithValue("@mm", (int)snapshot.MailboxMode);
                        cmd.Parameters.AddWithValue("@mnlc", (int)snapshot.NotLoadedMailCount);
                        cmd.Parameters.AddWithValue("@mukc", (int)snapshot.MailboxUnknownCountC);
                        cmd.Parameters.AddWithValue("@eitb", (int)snapshot.EventInfoTailByte);
                        cmd.Parameters.AddWithValue("@hkt", (int)snapshot.HotkeyKeyType);
                        cmd.Parameters.AddWithValue("@mgo", (object)snapshot.MainGameOptionBlob ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@qb0", (object)snapshot.QuickchatBank0 ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@qb1", (object)snapshot.QuickchatBank1 ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ciplen", (long)snapshot.CharacInvisibleFalgsPayloadLen);
                        cmd.Parameters.AddWithValue("@rdcc", (long)snapshot.RacingDungeonCurrentEnterCount);
                        cmd.Parameters.AddWithValue("@rdgf", (object)snapshot.RacingDungeonGroupFlags ?? DBNull.Value);
                        
                        cmd.Parameters.AddWithValue("@ackRegTime", (long)snapshot.AckAccountRegTime);
                        cmd.Parameters.AddWithValue("@ackPremBlob", (object)SerializeAckPremiums(snapshot.AckPremiums) ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ackQuestDisp", (object)snapshot.AckQuestDisplayIds ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ackSlot", (int)snapshot.AckCharSlotIndex);
                        cmd.Parameters.AddWithValue("@ackFatBat", (int)snapshot.AckFatigueBattery);
                        cmd.Parameters.AddWithValue("@ackFatGrown", (int)snapshot.AckFatigueGrownUpBuff);
                        cmd.Parameters.AddWithValue("@ackTrade", (int)snapshot.AckTradePunishFlag);
                        cmd.Parameters.AddWithValue("@ackExtra86", (int)snapshot.AckExtraField86JP);
                        cmd.Parameters.AddWithValue("@ackRes8b", (object)snapshot.AckReserved8B ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ackTutSkip", (int)snapshot.AckTutorialSkipable);
                        cmd.Parameters.AddWithValue("@ackPostTut", (int)snapshot.AckPostTutorialU16);
                        cmd.Parameters.AddWithValue("@ackTail", (object)snapshot.AckUnreadTail ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_growth_weapon_stages WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var stages = snapshot.GrowthWeaponStageIds;
                    for (int i = 0; i < stages.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_growth_weapon_stages (character_id, sort_order, stage_id) VALUES (@cid, @ord, @sid)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@sid", (int)stages[i]);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_show_effects WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var effects = snapshot.ShowEffects;
                    for (int i = 0; i < effects.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_show_effects (character_id, sort_order, effect_index, duration_seconds) VALUES (@cid, @ord, @ei, @ds)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@ei", (int)effects[i].EffectIndex);
                            cmd.Parameters.AddWithValue("@ds", (long)effects[i].DurationSeconds);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_pvp_missions WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var missions = snapshot.PvpMissions;
                    for (int i = 0; i < missions.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_pvp_missions (character_id, sort_order, mission_id, progress_value) VALUES (@cid, @ord, @mid, @pv)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@mid", (long)missions[i].MissionId);
                            cmd.Parameters.AddWithValue("@pv", (long)missions[i].ProgressValue);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_dungeon_permissions WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var dungeons = snapshot.DungeonPermissions;
                    for (int i = 0; i < dungeons.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_dungeon_permissions (character_id, sort_order, dungeon_id, clear_state) VALUES (@cid, @ord, @did, @cs)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@did", (int)dungeons[i].DungeonId);
                            cmd.Parameters.AddWithValue("@cs", (int)dungeons[i].ClearState);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_event_info WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var events = snapshot.EventInfoEntries;
                    for (int i = 0; i < events.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_event_info (character_id, sort_order, repeat_event_index, event_data) VALUES (@cid, @ord, @rei, @ed)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@rei", (int)events[i].RepeatEventIndex);
                            cmd.Parameters.AddWithValue("@ed", (object)events[i].EventData ?? DBNull.Value);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_hotkey_slots WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var slots = snapshot.HotkeyConfigSlots;
                    for (int i = 0; i < slots.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_hotkey_slots (character_id, slot_index, hotkey_value) VALUES (@cid, @si, @hv)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@si", i);
                            cmd.Parameters.AddWithValue("@hv", (int)slots[i]);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_invisible_falgs WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    foreach (var entry in snapshot.CharacInvisibleFalgs)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_invisible_falgs (character_id, slot_index, flag_value) VALUES (@cid, @si, @fv)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@si", (int)entry.SlotIndex);
                            cmd.Parameters.AddWithValue("@fv", (int)entry.FlagValue);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    using (var cmd = new SqliteCommand("DELETE FROM character_racing_dungeon_groups WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new SqliteCommand("DELETE FROM character_racing_dungeon_entries WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new SqliteCommand("DELETE FROM character_racing_dungeon_tail_ids WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var racingGroups = snapshot.RacingDungeonGroups;
                    for (int i = 0; i < racingGroups.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_racing_dungeon_groups (character_id, group_index, group_id) VALUES (@cid, @gi, @gid)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@gi", i);
                            cmd.Parameters.AddWithValue("@gid", (long)racingGroups[i].GroupId);
                            cmd.ExecuteNonQuery();
                        }
                        var entries = racingGroups[i].Entries;
                        for (int j = 0; j < entries.Count; j++)
                        {
                            using (var cmd = new SqliteCommand(
                                "INSERT INTO character_racing_dungeon_entries (character_id, group_index, entry_index, track_like_id, value_a, value_b) VALUES (@cid, @gi, @ei, @tid, @va, @vb)", conn, tx))
                            {
                                cmd.Parameters.AddWithValue("@cid", characterId);
                                cmd.Parameters.AddWithValue("@gi", i);
                                cmd.Parameters.AddWithValue("@ei", j);
                                cmd.Parameters.AddWithValue("@tid", (long)entries[j].TrackLikeId);
                                cmd.Parameters.AddWithValue("@va", (long)entries[j].ValueA);
                                cmd.Parameters.AddWithValue("@vb", (long)entries[j].ValueB);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    var tailIds = snapshot.RacingDungeonTailIds;
                    for (int i = 0; i < tailIds.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_racing_dungeon_tail_ids (character_id, sort_order, id_value) VALUES (@cid, @ord, @v)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@v", (long)tailIds[i]);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                }
            }
        }

        public bool HasFlags(int characterId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM character_init_flags WHERE character_id = @cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
        }

        

        public List<ItemValueEntrySnapshot> LoadItemValueList(int characterId, string listKind)
        {
            var items = new List<ItemValueEntrySnapshot>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT item_id, value FROM character_item_values WHERE character_id = @cid AND list_kind = @kind ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@kind", listKind);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            items.Add(new ItemValueEntrySnapshot { ItemId = reader.GetInt32(0), Value = reader.GetInt32(1) });
                    }
                }
            }
            return items;
        }

        public void SaveItemValueList(int characterId, string listKind, List<ItemValueEntrySnapshot> items)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_item_values WHERE character_id = @cid AND list_kind = @kind", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@kind", listKind);
                        cmd.ExecuteNonQuery();
                    }
                    for (int i = 0; i < items.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_item_values (character_id, list_kind, sort_order, item_id, value) VALUES (@cid, @kind, @ord, @iid, @val)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@kind", listKind);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@iid", items[i].ItemId);
                            cmd.Parameters.AddWithValue("@val", items[i].Value);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        

        public ItemLockListSnapshot LoadItemLocks(int characterId)
        {
            var snapshot = new ItemLockListSnapshot();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT type_or_list, item_key_or_slot, state, extra_value FROM character_item_locks WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var entry = new ItemLockEntrySnapshot
                            {
                                TypeOrList = (byte)reader.GetInt32(0),
                                ItemKeyOrSlot = (ushort)reader.GetInt32(1),
                                State = (byte)reader.GetInt32(2),
                            };
                            if (!reader.IsDBNull(3))
                            {
                                entry.ExtraValue = reader.GetInt32(3);
                                entry.HasExtraValue = true;
                            }
                            snapshot.Entries.Add(entry);
                        }
                    }
                }
            }
            return snapshot;
        }

        public void SaveItemLocks(int characterId, ItemLockListSnapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_item_locks WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    for (int i = 0; i < snapshot.Entries.Count; i++)
                    {
                        var e = snapshot.Entries[i];
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_item_locks (character_id, sort_order, type_or_list, item_key_or_slot, state, extra_value) VALUES (@cid, @ord, @t, @k, @s, @ev)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@t", (int)e.TypeOrList);
                            cmd.Parameters.AddWithValue("@k", (int)e.ItemKeyOrSlot);
                            cmd.Parameters.AddWithValue("@s", (int)e.State);
                            cmd.Parameters.AddWithValue("@ev", e.HasExtraValue ? (object)e.ExtraValue : DBNull.Value);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        

        public AchievementCompleteSnapshot LoadAchievementComplete(int characterId)
        {
            var snapshot = new AchievementCompleteSnapshot();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT achievement_id, p1, p2, p3, p4 FROM character_achievement_complete WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            snapshot.Entries.Add(new AchievementCompleteEntrySnapshot
                            {
                                AchievementId = reader.GetInt32(0),
                                P1 = (ushort)reader.GetInt32(1),
                                P2 = (ushort)reader.GetInt32(2),
                                P3 = (ushort)reader.GetInt32(3),
                                P4 = (ushort)reader.GetInt32(4),
                            });
                        }
                    }
                }
            }
            return snapshot;
        }

        public void SaveAchievementComplete(int characterId, AchievementCompleteSnapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_achievement_complete WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    for (int i = 0; i < snapshot.Entries.Count; i++)
                    {
                        var e = snapshot.Entries[i];
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_achievement_complete (character_id, sort_order, achievement_id, p1, p2, p3, p4) VALUES (@cid, @ord, @aid, @p1, @p2, @p3, @p4)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@aid", e.AchievementId);
                            cmd.Parameters.AddWithValue("@p1", (int)e.P1);
                            cmd.Parameters.AddWithValue("@p2", (int)e.P2);
                            cmd.Parameters.AddWithValue("@p3", (int)e.P3);
                            cmd.Parameters.AddWithValue("@p4", (int)e.P4);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        

        public List<AchievementListChunkSnapshot> LoadAchievementChunks(int characterId)
        {
            var chunks = new List<AchievementListChunkSnapshot>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT chunk_index, mode_byte, owner_id16, entries_blob FROM character_achievement_chunks WHERE character_id = @cid ORDER BY chunk_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var chunk = new AchievementListChunkSnapshot
                            {
                                ChunkIndex = reader.GetInt32(0),
                                ModeByte = (byte)reader.GetInt32(1),
                                OwnerId16 = (ushort)reader.GetInt32(2),
                            };
                            var blob = reader.IsDBNull(3) ? null : (byte[])reader[3];
                            if (blob != null)
                                DeserializeAchievementEntries(blob, chunk.Entries);
                            chunks.Add(chunk);
                        }
                    }
                }
            }
            return chunks;
        }

        public void SaveAchievementChunks(int characterId, List<AchievementListChunkSnapshot> chunks)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_achievement_chunks WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    foreach (var chunk in chunks)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_achievement_chunks (character_id, chunk_index, mode_byte, owner_id16, entries_blob) VALUES (@cid, @ci, @mb, @oid, @eb)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ci", chunk.ChunkIndex);
                            cmd.Parameters.AddWithValue("@mb", (int)chunk.ModeByte);
                            cmd.Parameters.AddWithValue("@oid", (int)chunk.OwnerId16);
                            cmd.Parameters.AddWithValue("@eb", SerializeAchievementEntries(chunk.Entries));
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        

        public List<Unknown725Snapshot> LoadUnknown725(int characterId)
        {
            var list = new List<Unknown725Snapshot>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT param_a, mode_or_state, content_id, param_b FROM character_unknown725 WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            list.Add(new Unknown725Snapshot
                            {
                                ParamA = reader.GetInt32(0),
                                ModeOrState = reader.GetInt32(1),
                                ContentId = reader.GetInt32(2),
                                ParamB = reader.GetInt32(3),
                            });
                    }
                }
            }
            return list;
        }

        public void SaveUnknown725(int characterId, List<Unknown725Snapshot> packets)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_unknown725 WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    for (int i = 0; i < packets.Count; i++)
                    {
                        var p = packets[i];
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_unknown725 (character_id, sort_order, param_a, mode_or_state, content_id, param_b) VALUES (@cid, @ord, @pa, @ms, @ci, @pb)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@pa", p.ParamA);
                            cmd.Parameters.AddWithValue("@ms", p.ModeOrState);
                            cmd.Parameters.AddWithValue("@ci", p.ContentId);
                            cmd.Parameters.AddWithValue("@pb", p.ParamB);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        

        public Unknown730Snapshot LoadUnknown730(int characterId)
        {
            var snapshot = new Unknown730Snapshot();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT entry_id, sentinel_or_value, flag FROM character_unknown730 WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.Entries.Add(new Unknown730EntrySnapshot
                            {
                                EntryId = reader.GetInt32(0),
                                SentinelOrValue = reader.GetInt32(1),
                                Flag = reader.GetInt32(2),
                            });
                    }
                }
            }
            return snapshot;
        }

        public void SaveUnknown730(int characterId, Unknown730Snapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_unknown730 WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                    for (int i = 0; i < snapshot.Entries.Count; i++)
                    {
                        var e = snapshot.Entries[i];
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_unknown730 (character_id, sort_order, entry_id, sentinel_or_value, flag) VALUES (@cid, @ord, @eid, @sv, @f)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@eid", e.EntryId);
                            cmd.Parameters.AddWithValue("@sv", e.SentinelOrValue);
                            cmd.Parameters.AddWithValue("@f", e.Flag);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        

        public void SeedFromSnapshot(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            if (!HasFlags(characterId))
                SaveFlags(characterId, snapshot);

            SaveItemValueListIfEmpty(characterId, "cooltime", snapshot.CooltimeItems);
            SaveItemValueListIfEmpty(characterId, "effect", snapshot.EffectItems);

            if (LoadItemLocks(characterId).Entries.Count == 0 && snapshot.ItemLockList.Entries.Count > 0)
                SaveItemLocks(characterId, snapshot.ItemLockList);

            if (LoadAchievementComplete(characterId).Entries.Count == 0 && snapshot.AchievementComplete.Entries.Count > 0)
                SaveAchievementComplete(characterId, snapshot.AchievementComplete);

            if (LoadAchievementChunks(characterId).Count == 0 && snapshot.AchievementChunks.Count > 0)
                SaveAchievementChunks(characterId, snapshot.AchievementChunks);

            if (LoadUnknown725(characterId).Count == 0 && snapshot.Unknown725Packets.Count > 0)
                SaveUnknown725(characterId, snapshot.Unknown725Packets);

            if (LoadUnknown730(characterId).Entries.Count == 0 && snapshot.Unknown730.Entries.Count > 0)
                SaveUnknown730(characterId, snapshot.Unknown730);
        }

        public void LoadAll(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            LoadFlags(characterId, snapshot);

            var cooltime = LoadItemValueList(characterId, "cooltime");
            snapshot.CooltimeItems.Clear();
            snapshot.CooltimeItems.AddRange(cooltime);

            var effect = LoadItemValueList(characterId, "effect");
            snapshot.EffectItems.Clear();
            snapshot.EffectItems.AddRange(effect);

            var locks = LoadItemLocks(characterId);
            snapshot.ItemLockList = locks;

            snapshot.AchievementComplete = LoadAchievementComplete(characterId);

            var chunks = LoadAchievementChunks(characterId);
            snapshot.AchievementChunks.Clear();
            snapshot.AchievementChunks.AddRange(chunks);

            var u725 = LoadUnknown725(characterId);
            snapshot.Unknown725Packets.Clear();
            snapshot.Unknown725Packets.AddRange(u725);

            snapshot.Unknown730 = LoadUnknown730(characterId);
        }

        

        private void SaveItemValueListIfEmpty(int characterId, string kind, List<ItemValueEntrySnapshot> items)
        {
            if (LoadItemValueList(characterId, kind).Count == 0 && items.Count > 0)
                SaveItemValueList(characterId, kind, items);
        }

        private static byte[] SerializeExpertJobInfo(ExpertJobInfoSnapshot info)
        {
            var list = new List<byte>();
            list.Add(info.State0);
            list.Add(info.Mode);
            list.AddRange(BitConverter.GetBytes(info.ValueA));
            list.AddRange(BitConverter.GetBytes(info.ValueB));
            list.Add((byte)info.Entries.Count);
            foreach (var entry in info.Entries)
                list.AddRange(BitConverter.GetBytes(entry));
            return list.ToArray();
        }

        private static void DeserializeExpertJobInfo(byte[] blob, ExpertJobInfoSnapshot info)
        {
            if (blob.Length < 2) return;
            info.State0 = blob[0];
            info.Mode = blob[1];
            int offset = 2;
            if (offset + 8 <= blob.Length)
            {
                info.ValueA = BitConverter.ToInt32(blob, offset); offset += 4;
                info.ValueB = BitConverter.ToInt32(blob, offset); offset += 4;
            }
            if (offset < blob.Length)
            {
                var count = blob[offset++];
                info.Entries.Clear();
                for (int i = 0; i < count && offset + 4 <= blob.Length; i++)
                {
                    info.Entries.Add(BitConverter.ToInt32(blob, offset));
                    offset += 4;
                }
            }
        }

        private static byte[] SerializeChampionBreak(ChampionBreakSystemSnapshot snapshot)
        {
            var buf = new byte[9];
            Array.Copy(BitConverter.GetBytes(snapshot.KeyId), 0, buf, 0, 4);
            buf[4] = snapshot.Mode;
            Array.Copy(BitConverter.GetBytes(snapshot.Value), 0, buf, 5, 4);
            return buf;
        }

        private static void DeserializeChampionBreak(byte[] blob, ChampionBreakSystemSnapshot snapshot)
        {
            snapshot.KeyId = BitConverter.ToInt32(blob, 0);
            snapshot.Mode = blob[4];
            snapshot.Value = BitConverter.ToInt32(blob, 5);
        }

        private static byte[] SerializeAckPremiums(List<AckPremiumEntrySnapshot> premiums)
        {
            if (premiums == null || premiums.Count == 0)
                return new byte[] { 0 };
            var buf = new byte[1 + premiums.Count * 9];
            buf[0] = (byte)premiums.Count;
            for (int i = 0; i < premiums.Count; i++)
            {
                int off = 1 + i * 9;
                buf[off] = premiums[i].PremiumType;
                if (premiums[i].EndTime != null)
                    Buffer.BlockCopy(premiums[i].EndTime, 0, buf, off + 1, Math.Min(premiums[i].EndTime.Length, 8));
            }
            return buf;
        }

        private static void DeserializeAckPremiums(byte[] blob, List<AckPremiumEntrySnapshot> premiums)
        {
            premiums.Clear();
            if (blob == null || blob.Length < 1) return;
            int count = blob[0];
            for (int i = 0; i < count && 1 + (i + 1) * 9 <= blob.Length; i++)
            {
                int off = 1 + i * 9;
                var entry = new AckPremiumEntrySnapshot
                {
                    PremiumType = blob[off],
                    EndTime = new byte[8],
                };
                Buffer.BlockCopy(blob, off + 1, entry.EndTime, 0, 8);
                premiums.Add(entry);
            }
        }

        private static byte[] SerializeAchievementEntries(List<AchievementListEntrySnapshot> entries)
        {
            var buf = new byte[entries.Count * 22];
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                int off = i * 22;
                Array.Copy(BitConverter.GetBytes(e.AchievementId), 0, buf, off, 2); off += 2;
                Array.Copy(BitConverter.GetBytes(e.ValueA), 0, buf, off, 4); off += 4;
                Array.Copy(BitConverter.GetBytes(e.ValueB), 0, buf, off, 4); off += 4;
                buf[off++] = e.CategoryByte;
                Array.Copy(BitConverter.GetBytes(e.LinkId), 0, buf, off, 2); off += 2;
                buf[off++] = e.Flag0;
                Array.Copy(BitConverter.GetBytes(e.ValueC), 0, buf, off, 4); off += 4;
                buf[off++] = e.Flag1;
                buf[off++] = e.Flag2;
                Array.Copy(BitConverter.GetBytes(e.TailValue), 0, buf, off, 2);
            }
            return buf;
        }

        private static void DeserializeAchievementEntries(byte[] blob, List<AchievementListEntrySnapshot> entries)
        {
            for (int off = 0; off + 22 <= blob.Length; off += 22)
            {
                entries.Add(new AchievementListEntrySnapshot
                {
                    AchievementId = BitConverter.ToUInt16(blob, off),
                    ValueA = BitConverter.ToInt32(blob, off + 2),
                    ValueB = BitConverter.ToInt32(blob, off + 6),
                    CategoryByte = blob[off + 10],
                    LinkId = BitConverter.ToUInt16(blob, off + 11),
                    Flag0 = blob[off + 13],
                    ValueC = BitConverter.ToInt32(blob, off + 14),
                    Flag1 = blob[off + 18],
                    Flag2 = blob[off + 19],
                    TailValue = BitConverter.ToUInt16(blob, off + 20),
                });
            }
        }

        

        public byte[] LoadGlobalRawPacket(int notiType)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT packet_body FROM global_raw_packets WHERE noti_type = @nt", conn))
                {
                    cmd.Parameters.AddWithValue("@nt", notiType);
                    var result = cmd.ExecuteScalar();
                    return result == null || result == DBNull.Value ? null : (byte[])result;
                }
            }
        }

        public byte[] LoadServerEventPhaseBitmap()
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT event_phase_bitmap FROM global_server_event_phase WHERE id = 1", conn))
                {
                    var result = cmd.ExecuteScalar();
                    return result == null || result == DBNull.Value ? null : (byte[])result;
                }
            }
        }

        public void SeedRawPacketsFromTemplates(int characterId, List<SelectCharacterPacketTemplate> templates)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();

                using (var chk = new SqliteCommand("SELECT COUNT(*) FROM character_init_bodies WHERE character_id = @cid", conn))
                {
                    chk.Parameters.AddWithValue("@cid", characterId);
                    if (Convert.ToInt32(chk.ExecuteScalar()) > 0)
                        return;
                }

                using (var tx = conn.BeginTransaction())
                {
                    foreach (var t in templates)
                    {
                        if (t.PacketBytes == null || t.PacketBytes.Length == 0)
                            continue;

                        var headerLen = 15;
                        if (t.PacketBytes.Length <= headerLen)
                            continue;

                        var body = new byte[t.PacketBytes.Length - headerLen];
                        Buffer.BlockCopy(t.PacketBytes, headerLen, body, 0, body.Length);

                        if (t.Command == 0x00 && t.Type == 0x0187)
                        {
                            if (body.Length < 4)
                                continue;
                            var bitmap = new byte[body.Length - 4];
                            Buffer.BlockCopy(body, 4, bitmap, 0, bitmap.Length);
                            using (var cmd = new SqliteCommand("INSERT OR IGNORE INTO global_server_event_phase (id, event_phase_bitmap) VALUES (1, @b)", conn, tx))
                            {
                                cmd.Parameters.AddWithValue("@b", bitmap);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        else if (t.Command == 0x01 && t.Type == 0x0312)
                        {
                            using (var cmd = new SqliteCommand("INSERT OR IGNORE INTO global_raw_packets (noti_type, packet_body) VALUES (@nt, @body)", conn, tx))
                            {
                                cmd.Parameters.AddWithValue("@nt", 0x10312);
                                cmd.Parameters.AddWithValue("@body", body);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    const int hdrLen = 15;
                    var initBodyTypes = new System.Collections.Generic.HashSet<int> { 0x0035, 0x0077, 0x0111, 0x019F, 0x015F, 0x0381, 0x0357, 0x019D, 0x03D8 };
                    foreach (var t in templates)
                    {
                        if (t.Command != 0x00 || !initBodyTypes.Contains(t.Type))
                            continue;
                        if (t.PacketBytes == null || t.PacketBytes.Length <= hdrLen)
                            continue;
                        var b = new byte[t.PacketBytes.Length - hdrLen];
                        Buffer.BlockCopy(t.PacketBytes, hdrLen, b, 0, b.Length);
                        using (var cmd = new SqliteCommand(
                            "INSERT OR IGNORE INTO character_init_bodies(character_id, noti_type, occurrence_index, body) VALUES(@cid, @nt, @oi, @b)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@nt", t.Type);
                            cmd.Parameters.AddWithValue("@oi", t.OccurrenceIndex);
                            cmd.Parameters.AddWithValue("@b", b);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }
    }
}
