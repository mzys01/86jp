using System;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.CharacterData
{
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public static class Subtype1BlobMigrator
    {
        public static void Migrate(SqliteConnection conn, int characterId)
        {
            byte[] blob;
            using (var cmd = new SqliteCommand("SELECT equip_list_blob FROM equipped_items WHERE character_id=@cid", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                var result = cmd.ExecuteScalar();
                blob = result as byte[];
            }
            if (blob == null || blob.Length < 92) return;

            int o = 0;
            uint exp = ReadU32(blob, ref o);
            int statSize = ReadI32(blob, ref o);
            int statBytes = Math.Max(statSize - 1, 0);
            byte[] statBlock = Slice(blob, o, Math.Min(statBytes, 82));
            o += statBytes;
            byte level = blob[o++];
            byte exEquipSlot = blob[o++];

            
            using (var cmd = new SqliteCommand("UPDATE characters SET exp=@e, ex_equip_slot_stat=@es WHERE character_id=@cid", conn))
            {
                cmd.Parameters.AddWithValue("@e", (long)exp);
                cmd.Parameters.AddWithValue("@es", (int)exEquipSlot);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }

            
            var parsed = MakeEquipListCodec.Parse(blob);

            
            using (var cmd = new SqliteCommand("DELETE FROM character_equipped_entries WHERE character_id=@cid", conn))
            { cmd.Parameters.AddWithValue("@cid", characterId); cmd.ExecuteNonQuery(); }
            foreach (var entry in parsed.Entries)
            {
                using (var cmd = new SqliteCommand(
                    "INSERT OR REPLACE INTO character_equipped_entries(character_id,slot,item_id,raw_entry) VALUES(@cid,@s,@iid,@raw)", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@s", entry.Slot);
                    cmd.Parameters.AddWithValue("@iid", entry.ItemId);
                    cmd.Parameters.AddWithValue("@raw", entry.Raw);
                    cmd.ExecuteNonQuery();
                }
            }

            
            o = 92 + 1;
            foreach (var e in parsed.Entries) o += e.Raw.Length;

            
            
            
            uint equipListTrailing = ReadU32(blob, ref o);

            
            uint nameTagItemId = ReadU32(blob, ref o);
            uint nameTagExpireTime = ReadU32(blob, ref o);

            
            byte skillTreeIndex = blob[o++];
            int page0Count = blob[o++];
            o += page0Count * 3;
            int page1Count = blob[o++];
            o += page1Count * 3;

            
            byte creatureLevel = blob[o++];

            
            int dimCount = blob[o++];
            
            using (var cmd = new SqliteCommand("DELETE FROM character_dimensions WHERE character_id=@cid", conn))
            { cmd.Parameters.AddWithValue("@cid", characterId); cmd.ExecuteNonQuery(); }
            for (int i = 0; i < dimCount; i++)
            {
                uint dk = ReadU32(blob, ref o);
                byte v1 = blob[o++], v2 = blob[o++];
                using (var cmd = new SqliteCommand(
                    "INSERT OR REPLACE INTO character_dimensions(character_id,sort_order,dim_key,val1,val2) VALUES(@cid,@so,@dk,@v1,@v2)", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@so", i);
                    cmd.Parameters.AddWithValue("@dk", (long)dk);
                    cmd.Parameters.AddWithValue("@v1", (int)v1);
                    cmd.Parameters.AddWithValue("@v2", (int)v2);
                    cmd.ExecuteNonQuery();
                }
            }
            byte df1 = blob[o++], df2 = blob[o++], df3 = blob[o++], df4 = blob[o++];
            using (var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO character_dimension_flags(character_id,flag1,flag2,flag3,flag4) VALUES(@cid,@f1,@f2,@f3,@f4)", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@f1", (int)df1); cmd.Parameters.AddWithValue("@f2", (int)df2);
                cmd.Parameters.AddWithValue("@f3", (int)df3); cmd.Parameters.AddWithValue("@f4", (int)df4);
                cmd.ExecuteNonQuery();
            }

            
            int pvpCount = blob[o++];
            using (var cmd = new SqliteCommand("DELETE FROM character_pvp_results WHERE character_id=@cid", conn))
            { cmd.Parameters.AddWithValue("@cid", characterId); cmd.ExecuteNonQuery(); }
            for (int i = 0; i < pvpCount; i++)
            {
                uint v32 = ReadU32(blob, ref o);
                ushort v16a = ReadU16(blob, ref o), v16b = ReadU16(blob, ref o);
                using (var cmd = new SqliteCommand(
                    "INSERT OR REPLACE INTO character_pvp_results(character_id,sort_order,value_u32,value_u16a,value_u16b) VALUES(@cid,@so,@a,@b,@c)", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId); cmd.Parameters.AddWithValue("@so", i);
                    cmd.Parameters.AddWithValue("@a", (long)v32); cmd.Parameters.AddWithValue("@b", (int)v16a); cmd.Parameters.AddWithValue("@c", (int)v16b);
                    cmd.ExecuteNonQuery();
                }
            }

            
            byte manageLevel = blob[o++];

            
            uint abuseCount = ReadU32(blob, ref o);
            using (var cmd = new SqliteCommand("DELETE FROM character_abuse_values WHERE character_id=@cid", conn))
            { cmd.Parameters.AddWithValue("@cid", characterId); cmd.ExecuteNonQuery(); }
            for (uint i = 0; i < abuseCount; i++)
            {
                uint av = ReadU32(blob, ref o);
                using (var cmd = new SqliteCommand(
                    "INSERT OR REPLACE INTO character_abuse_values(character_id,sort_order,abuse_value) VALUES(@cid,@so,@v)", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId); cmd.Parameters.AddWithValue("@so", (int)i);
                    cmd.Parameters.AddWithValue("@v", (long)av);
                    cmd.ExecuteNonQuery();
                }
            }

            
            byte flag = blob[o++];
            uint guildPowerWar = ReadU32(blob, ref o);
            uint serverTimestamp = ReadU32(blob, ref o);
            ushort questShopCount = ReadU16(blob, ref o);
            uint progress1 = ReadU32(blob, ref o);
            uint progress2 = ReadU32(blob, ref o);

            
            
            int so = 0;
            uint hpMax = S32(statBlock, ref so, 4);
            uint mpMax = S32(statBlock, ref so, 4);
            short physAtk = SI16(statBlock, ref so); short physDef = SI16(statBlock, ref so);
            short magAtk = SI16(statBlock, ref so); short magDef = SI16(statBlock, ref so);
            short fireRes = SI16(statBlock, ref so); short waterRes = SI16(statBlock, ref so);
            short darkRes = SI16(statBlock, ref so); short lightRes = SI16(statBlock, ref so);
            
            
            for (int mi = 0; mi < 34; mi++)
                if (statBlock[so + mi] != 0)
                    throw new System.IO.InvalidDataException(
                        $"[Subtype1BlobMigrator] char {characterId}: stat_modifiers[{mi / 2}] 非零 (byte +{mi}=0x{statBlock[so + mi]:X2}) — “常量零”假设被打破, 需恢复该字段持久化");
            so += 34;
            uint invLimit = S32(statBlock, ref so, 4);
            ushort hpRegen = SU16(statBlock, ref so); ushort mpRegen = SU16(statBlock, ref so);
            uint moveSpeed = S32(statBlock, ref so, 4);
            ushort atkSpeed = SU16(statBlock, ref so); ushort castSpeed = SU16(statBlock, ref so);
            ushort hitRecovery = SU16(statBlock, ref so); ushort jumpPower = SU16(statBlock, ref so);
            uint weight = S32(statBlock, ref so, 4);

            using (var cmd = new SqliteCommand(@"INSERT OR REPLACE INTO character_subtype1_fields(
                character_id, stat_hp_max, stat_mp_max, stat_physical_attack, stat_physical_defense,
                stat_magical_attack, stat_magical_defense, stat_fire_resistance, stat_water_resistance,
                stat_dark_resistance, stat_light_resistance, stat_inventory_limit,
                stat_hp_regen_speed, stat_mp_regen_speed, stat_move_speed, stat_attack_speed,
                stat_cast_speed, stat_hit_recovery, stat_jump_power, stat_weight, stat_level,
                name_tag_item_id, name_tag_expire_time, skill_tree_index, equipped_creature_level, equip_list_trailing,
                manage_level, flag_byte, guild_power_war, server_timestamp, quest_shop_count,
                progress1, progress2
            ) VALUES(
                @cid, @hp, @mp, @pa, @pd, @ma, @md, @fr, @wr, @dr, @lr, @il,
                @hr, @mr, @ms, @as, @cs, @hrc, @jp, @wt, @sl,
                @nti, @nte, @sti, @ecl, @elt,
                @ml, @fb, @gpw, @st, @qsc, @p1, @p2
            )", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@hp", (long)hpMax); cmd.Parameters.AddWithValue("@mp", (long)mpMax);
                cmd.Parameters.AddWithValue("@pa", (int)physAtk); cmd.Parameters.AddWithValue("@pd", (int)physDef);
                cmd.Parameters.AddWithValue("@ma", (int)magAtk); cmd.Parameters.AddWithValue("@md", (int)magDef);
                cmd.Parameters.AddWithValue("@fr", (int)fireRes); cmd.Parameters.AddWithValue("@wr", (int)waterRes);
                cmd.Parameters.AddWithValue("@dr", (int)darkRes); cmd.Parameters.AddWithValue("@lr", (int)lightRes);
                cmd.Parameters.AddWithValue("@il", (long)invLimit);
                cmd.Parameters.AddWithValue("@hr", (int)hpRegen); cmd.Parameters.AddWithValue("@mr", (int)mpRegen);
                cmd.Parameters.AddWithValue("@ms", (long)moveSpeed);
                cmd.Parameters.AddWithValue("@as", (int)atkSpeed); cmd.Parameters.AddWithValue("@cs", (int)castSpeed);
                cmd.Parameters.AddWithValue("@hrc", (int)hitRecovery); cmd.Parameters.AddWithValue("@jp", (int)jumpPower);
                cmd.Parameters.AddWithValue("@wt", (long)weight); cmd.Parameters.AddWithValue("@sl", (int)level);
                cmd.Parameters.AddWithValue("@nti", (long)nameTagItemId);
                cmd.Parameters.AddWithValue("@nte", (long)nameTagExpireTime);
                cmd.Parameters.AddWithValue("@sti", (int)skillTreeIndex);
                cmd.Parameters.AddWithValue("@ecl", (int)creatureLevel);
                cmd.Parameters.AddWithValue("@elt", (long)equipListTrailing);
                cmd.Parameters.AddWithValue("@ml", (int)manageLevel);
                cmd.Parameters.AddWithValue("@fb", (int)flag);
                cmd.Parameters.AddWithValue("@gpw", (long)guildPowerWar);
                cmd.Parameters.AddWithValue("@st", (long)serverTimestamp);
                cmd.Parameters.AddWithValue("@qsc", (int)questShopCount);
                cmd.Parameters.AddWithValue("@p1", (long)progress1);
                cmd.Parameters.AddWithValue("@p2", (long)progress2);
                cmd.ExecuteNonQuery();
            }

            FileLogger.Log($"[Subtype1BlobMigrator] char {characterId}: blob {blob.Length}B → structured tables OK (equip={parsed.Entries.Count} dim={dimCount} pvp={pvpCount} abuse={abuseCount})");
        }

        private static byte[] Slice(byte[] src, int offset, int len)
        {
            var dst = new byte[len];
            Buffer.BlockCopy(src, offset, dst, 0, Math.Min(len, src.Length - offset));
            return dst;
        }
        private static uint ReadU32(byte[] b, ref int o) { uint v = BitConverter.ToUInt32(b, o); o += 4; return v; }
        private static int ReadI32(byte[] b, ref int o) { int v = BitConverter.ToInt32(b, o); o += 4; return v; }
        private static ushort ReadU16(byte[] b, ref int o) { ushort v = BitConverter.ToUInt16(b, o); o += 2; return v; }
        private static uint S32(byte[] b, ref int o, int _) { return ReadU32(b, ref o); }
        private static short SI16(byte[] b, ref int o) { short v = BitConverter.ToInt16(b, o); o += 2; return v; }
        private static ushort SU16(byte[] b, ref int o) { return ReadU16(b, ref o); }
    }
}
