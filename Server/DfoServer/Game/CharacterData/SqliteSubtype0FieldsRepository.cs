using System;
using Microsoft.Data.Sqlite;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Game.CharacterData
{
    
    
    
    
    public sealed class SqliteSubtype0FieldsRepository
    {
        private readonly string _connectionString;

        public SqliteSubtype0FieldsRepository(string databasePath, string schemaFilePath)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public UserInfoMinimumTailSnapshot Load(int characterId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                return Load(conn, characterId);
            }
        }

        public static UserInfoMinimumTailSnapshot Load(SqliteConnection conn, int characterId)
        {
            using (var cmd = new SqliteCommand(@"SELECT
                name_tag_item_id, creature_field1, creature_field2, creature_field3, creature_field4,
                creature_buffer, stamina, fatigue_penalty, is_event_character, pc_room_id,
                is_private_store, is_premium_pc_room, server_group_id, black_count, guild_level,
                chaos_point, disguise_kind, is_disguised, expert_job_type, expert_job_exp,
                is_hardcore_mode, is_hardcore_dead, hardcore_death_count, user_state_bits, chat_ban_end_time,
                fatigue_update, return_user_flag, channel_display_mode, channel_type, channel_id,
                is_return_user, link_slot_enabled, link_type_a, link_type_b, emotion_index,
                action_byte, fatigue_display_update, costume_flag, aura_flag, pet_display_flag,
                title_display_flag, pvp_stat_a, pvp_win_streak, pvp_lose_streak, pvp_rank_point,
                trailing_byte
            FROM character_subtype0_fields WHERE character_id=@cid", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new UserInfoMinimumTailSnapshot
                    {
                        NameTagItemId = (uint)r.GetInt64(0),
                        CreatureField1 = (byte)r.GetInt32(1),
                        CreatureField2 = (byte)r.GetInt32(2),
                        CreatureField3 = (byte)r.GetInt32(3),
                        CreatureField4 = (byte)r.GetInt32(4),
                        CreatureBuffer = r.IsDBNull(5) ? new byte[8] : (byte[])r.GetValue(5),
                        Stamina = (byte)r.GetInt32(6),
                        FatiguePenalty = (uint)r.GetInt64(7),
                        IsEventCharacter = (byte)r.GetInt32(8),
                        PcRoomId = (uint)r.GetInt64(9),
                        IsPrivateStore = (byte)r.GetInt32(10),
                        IsPremiumPcRoom = (byte)r.GetInt32(11),
                        ServerGroupId = (byte)r.GetInt32(12),
                        BlackCount = (uint)r.GetInt64(13),
                        GuildLevel = (byte)r.GetInt32(14),
                        ChaosPoint = (uint)r.GetInt64(15),
                        DisguiseKind = (byte)r.GetInt32(16),
                        IsDisguised = (byte)r.GetInt32(17),
                        ExpertJobType = (byte)r.GetInt32(18),
                        ExpertJobExp = (uint)r.GetInt64(19),
                        IsHardcoreMode = (byte)r.GetInt32(20),
                        IsHardcoreDead = (byte)r.GetInt32(21),
                        HardcoreDeathCount = (ushort)r.GetInt32(22),
                        UserStateBits = (byte)r.GetInt32(23),
                        ChatBanEndTime = (uint)r.GetInt64(24),
                        FatigueUpdate = (ushort)r.GetInt32(25),
                        ReturnUserFlag = (byte)r.GetInt32(26),
                        ChannelDisplayMode = (ushort)r.GetInt32(27),
                        ChannelType = (byte)r.GetInt32(28),
                        ChannelId = (ushort)r.GetInt32(29),
                        IsReturnUser = (byte)r.GetInt32(30),
                        LinkSlotEnabled = (byte)r.GetInt32(31),
                        LinkTypeA = (byte)r.GetInt32(32),
                        LinkTypeB = (byte)r.GetInt32(33),
                        EmotionIndex = (ushort)r.GetInt32(34),
                        ActionByte = (byte)r.GetInt32(35),
                        FatigueDisplayUpdate = (ushort)r.GetInt32(36),
                        CostumeFlag = (byte)r.GetInt32(37),
                        AuraFlag = (byte)r.GetInt32(38),
                        PetDisplayFlag = (byte)r.GetInt32(39),
                        TitleDisplayFlag = (byte)r.GetInt32(40),
                        PvpStatA = (uint)r.GetInt64(41),
                        PvpWinStreak = (byte)r.GetInt32(42),
                        PvpLoseStreak = (byte)r.GetInt32(43),
                        PvpRankPoint = (uint)r.GetInt64(44),
                        TrailingByte = (byte)r.GetInt32(45),
                    };
                }
            }
        }

        public static void Save(SqliteConnection conn, int characterId, UserInfoMinimumTailSnapshot s)
        {
            using (var cmd = new SqliteCommand(@"INSERT OR REPLACE INTO character_subtype0_fields(
                character_id,
                name_tag_item_id, creature_field1, creature_field2, creature_field3, creature_field4,
                creature_buffer, stamina, fatigue_penalty, is_event_character, pc_room_id,
                is_private_store, is_premium_pc_room, server_group_id, black_count, guild_level,
                chaos_point, disguise_kind, is_disguised, expert_job_type, expert_job_exp,
                is_hardcore_mode, is_hardcore_dead, hardcore_death_count, user_state_bits, chat_ban_end_time,
                fatigue_update, return_user_flag, channel_display_mode, channel_type, channel_id,
                is_return_user, link_slot_enabled, link_type_a, link_type_b, emotion_index,
                action_byte, fatigue_display_update, costume_flag, aura_flag, pet_display_flag,
                title_display_flag, pvp_stat_a, pvp_win_streak, pvp_lose_streak, pvp_rank_point,
                trailing_byte
            ) VALUES(
                @cid, @uvp, @cf1, @cf2, @cf3, @cf4, @cb, @sta, @fp, @iec, @pcr,
                @ips, @ippr, @sgi, @bc, @gl, @cp, @dk, @id2, @ejt, @eje,
                @ihm, @ihd, @hdc, @usb, @cbe, @fu, @ruf, @cdm, @ct, @chid,
                @iru, @lse, @lta, @ltb, @ei, @ab, @fdu, @cof, @auf, @pdf,
                @tdf, @psa, @pws, @pls, @prp, @tb
            )", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@uvp", (long)s.NameTagItemId);
                cmd.Parameters.AddWithValue("@cf1", (int)s.CreatureField1);
                cmd.Parameters.AddWithValue("@cf2", (int)s.CreatureField2);
                cmd.Parameters.AddWithValue("@cf3", (int)s.CreatureField3);
                cmd.Parameters.AddWithValue("@cf4", (int)s.CreatureField4);
                cmd.Parameters.AddWithValue("@cb", s.CreatureBuffer ?? new byte[8]);
                cmd.Parameters.AddWithValue("@sta", (int)s.Stamina);
                cmd.Parameters.AddWithValue("@fp", (long)s.FatiguePenalty);
                cmd.Parameters.AddWithValue("@iec", (int)s.IsEventCharacter);
                cmd.Parameters.AddWithValue("@pcr", (long)s.PcRoomId);
                cmd.Parameters.AddWithValue("@ips", (int)s.IsPrivateStore);
                cmd.Parameters.AddWithValue("@ippr", (int)s.IsPremiumPcRoom);
                cmd.Parameters.AddWithValue("@sgi", (int)s.ServerGroupId);
                cmd.Parameters.AddWithValue("@bc", (long)s.BlackCount);
                cmd.Parameters.AddWithValue("@gl", (int)s.GuildLevel);
                cmd.Parameters.AddWithValue("@cp", (long)s.ChaosPoint);
                cmd.Parameters.AddWithValue("@dk", (int)s.DisguiseKind);
                cmd.Parameters.AddWithValue("@id2", (int)s.IsDisguised);
                cmd.Parameters.AddWithValue("@ejt", (int)s.ExpertJobType);
                cmd.Parameters.AddWithValue("@eje", (long)s.ExpertJobExp);
                cmd.Parameters.AddWithValue("@ihm", (int)s.IsHardcoreMode);
                cmd.Parameters.AddWithValue("@ihd", (int)s.IsHardcoreDead);
                cmd.Parameters.AddWithValue("@hdc", (int)s.HardcoreDeathCount);
                cmd.Parameters.AddWithValue("@usb", (int)s.UserStateBits);
                cmd.Parameters.AddWithValue("@cbe", (long)s.ChatBanEndTime);
                cmd.Parameters.AddWithValue("@fu", (int)s.FatigueUpdate);
                cmd.Parameters.AddWithValue("@ruf", (int)s.ReturnUserFlag);
                cmd.Parameters.AddWithValue("@cdm", (int)s.ChannelDisplayMode);
                cmd.Parameters.AddWithValue("@ct", (int)s.ChannelType);
                cmd.Parameters.AddWithValue("@chid", (int)s.ChannelId);
                cmd.Parameters.AddWithValue("@iru", (int)s.IsReturnUser);
                cmd.Parameters.AddWithValue("@lse", (int)s.LinkSlotEnabled);
                cmd.Parameters.AddWithValue("@lta", (int)s.LinkTypeA);
                cmd.Parameters.AddWithValue("@ltb", (int)s.LinkTypeB);
                cmd.Parameters.AddWithValue("@ei", (int)s.EmotionIndex);
                cmd.Parameters.AddWithValue("@ab", (int)s.ActionByte);
                cmd.Parameters.AddWithValue("@fdu", (int)s.FatigueDisplayUpdate);
                cmd.Parameters.AddWithValue("@cof", (int)s.CostumeFlag);
                cmd.Parameters.AddWithValue("@auf", (int)s.AuraFlag);
                cmd.Parameters.AddWithValue("@pdf", (int)s.PetDisplayFlag);
                cmd.Parameters.AddWithValue("@tdf", (int)s.TitleDisplayFlag);
                cmd.Parameters.AddWithValue("@psa", (long)s.PvpStatA);
                cmd.Parameters.AddWithValue("@pws", (int)s.PvpWinStreak);
                cmd.Parameters.AddWithValue("@pls", (int)s.PvpLoseStreak);
                cmd.Parameters.AddWithValue("@prp", (long)s.PvpRankPoint);
                cmd.Parameters.AddWithValue("@tb", (int)s.TrailingByte);
                cmd.ExecuteNonQuery();
            }
        }

        
        
        
        
        public static void MigrateFromBlobIfNeeded(SqliteConnection conn)
        {
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM character_subtype0_fields;";
                    if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return;
                }

                using (var cmd = new SqliteCommand(
                    "SELECT character_id, remaining_bytes FROM character_userinfo_blobs WHERE user_info_type=0 AND gate_or_count>0", conn))
                using (var r = cmd.ExecuteReader())
                {
                    var pending = new System.Collections.Generic.List<(int cid, byte[] blob)>();
                    while (r.Read())
                        pending.Add((r.GetInt32(0), (byte[])r.GetValue(1)));
                    r.Close();

                    var done = new System.Collections.Generic.HashSet<int>();
                    foreach (var (cid, blob) in pending)
                    {
                        if (done.Contains(cid)) continue; 
                        const int headerFields = 7, appearEntrySize = 23, tailLength = UserInfoMinimumTailSnapshot.TailLength;
                        if (blob == null || blob.Length < headerFields + tailLength) continue;
                        int appearCount = blob[6];
                        int tailStart = headerFields + appearCount * appearEntrySize;
                        if (tailStart + tailLength > blob.Length) continue;

                        var tail = new byte[tailLength];
                        Buffer.BlockCopy(blob, tailStart, tail, 0, tailLength);
                        Save(conn, cid, UserInfoMinimumTailSnapshot.FromBytes(tail));
                        done.Add(cid);
                        FileLogger.Log($"[Subtype0FieldsMigrator] char {cid}: 104B tail → character_subtype0_fields OK");

                        int extraBytes = blob.Length - (tailStart + tailLength);
                        if (extraBytes > 0)
                            FileLogger.Log($"[Subtype0FieldsMigrator] WARNING char {cid}: blob 含 {extraBytes}B 多用户城镇广播块 — " +
                                "运行时不回放(本服无其他在线用户), 该种子 occ0/occ2 验证将与抓包差这部分字节");
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Subtype0FieldsMigrator] ERROR: {ex}");
            }
        }
    }
}
