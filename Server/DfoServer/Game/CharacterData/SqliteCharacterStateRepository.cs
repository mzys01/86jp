using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    public sealed class SqliteCharacterStateRepository : ICharacterStateRepository
    {
        private readonly string _connectionString;
        private readonly CharacterAchievementRepository _achievement;
        private readonly CharacterItemValueRepository _itemValue;
        private readonly CharacterItemLockRepository _itemLock;
        private readonly CharacterMiscStateRepository _miscState;
        private readonly GlobalStateRepository _globalState;

        public SqliteCharacterStateRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
            _achievement = new CharacterAchievementRepository(_connectionString);
            _itemValue = new CharacterItemValueRepository(_connectionString);
            _itemLock = new CharacterItemLockRepository(_connectionString);
            _miscState = new CharacterMiscStateRepository(_connectionString);
            _globalState = new GlobalStateRepository(_connectionString);
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



        public void SeedFromSnapshot(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            if (!HasFlags(characterId))
                SaveFlags(characterId, snapshot);

            _itemValue.SaveItemValueListIfEmpty(characterId, "cooltime", snapshot.CooltimeItems);
            _itemValue.SaveItemValueListIfEmpty(characterId, "effect", snapshot.EffectItems);

            if (_itemLock.LoadItemLocks(characterId).Entries.Count == 0 && snapshot.ItemLockList.Entries.Count > 0)
                _itemLock.SaveItemLocks(characterId, snapshot.ItemLockList);

            if (_achievement.LoadAchievementComplete(characterId).Entries.Count == 0 && snapshot.AchievementComplete.Entries.Count > 0)
                _achievement.SaveAchievementComplete(characterId, snapshot.AchievementComplete);

            if (_achievement.LoadAchievementChunks(characterId).Count == 0 && snapshot.AchievementChunks.Count > 0)
                _achievement.SaveAchievementChunks(characterId, snapshot.AchievementChunks);

            if (_miscState.LoadUnknown725(characterId).Count == 0 && snapshot.Unknown725Packets.Count > 0)
                _miscState.SaveUnknown725(characterId, snapshot.Unknown725Packets);

            if (_miscState.LoadUnknown730(characterId).Entries.Count == 0 && snapshot.Unknown730.Entries.Count > 0)
                _miscState.SaveUnknown730(characterId, snapshot.Unknown730);
        }

        public void LoadAll(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            LoadFlags(characterId, snapshot);

            var cooltime = _itemValue.LoadItemValueList(characterId, "cooltime");
            snapshot.CooltimeItems.Clear();
            snapshot.CooltimeItems.AddRange(cooltime);

            var effect = _itemValue.LoadItemValueList(characterId, "effect");
            snapshot.EffectItems.Clear();
            snapshot.EffectItems.AddRange(effect);

            var locks = _itemLock.LoadItemLocks(characterId);
            snapshot.ItemLockList = locks;

            snapshot.AchievementComplete = _achievement.LoadAchievementComplete(characterId);

            var chunks = _achievement.LoadAchievementChunks(characterId);
            snapshot.AchievementChunks.Clear();
            snapshot.AchievementChunks.AddRange(chunks);

            var u725 = _miscState.LoadUnknown725(characterId);
            snapshot.Unknown725Packets.Clear();
            snapshot.Unknown725Packets.AddRange(u725);

            snapshot.Unknown730 = _miscState.LoadUnknown730(characterId);
        }



        public byte[] LoadGlobalRawPacket(int notiType)
            => _globalState.LoadGlobalRawPacket(notiType);

        public byte[] LoadServerEventPhaseBitmap()
            => _globalState.LoadServerEventPhaseBitmap();

        public void SeedRawPacketsFromTemplates(int characterId, List<SelectCharacterPacketTemplate> templates)
            => _globalState.SeedRawPacketsFromTemplates(characterId, templates);



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
    }
}
