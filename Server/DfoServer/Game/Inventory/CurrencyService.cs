using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public static class CurrencyService
    {
        public static WalletSnapshot LoadWallet(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var w = new WalletSnapshot();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT stack_count FROM character_items WHERE character_id = @cid AND list_type = 0 AND slot_index = 0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    w.Gold = Convert.ToInt32(result);
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT coin FROM characters WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    w.Cera = Convert.ToInt32(result);
            }
            return w;
        }

        public static void UpdateGold(SqliteConnection connection, SqliteTransaction transaction, int characterId, int newGold)
        {
            UpdateCurrencySlot(connection, transaction, characterId, 0, newGold);
        }

        public static void UpdateCera(SqliteConnection connection, SqliteTransaction transaction, int characterId, int newCera)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE characters SET coin = @val WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@val", newCera);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        private static void UpdateCurrencySlot(SqliteConnection connection, SqliteTransaction transaction, int characterId, int slot, int value)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"INSERT OR REPLACE INTO character_items
(owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16)
VALUES ('character', @cid, @cid, 0, @slot, 0, 'stackable', @val, @val, 0, 0, 0, 0, 0);";
                cmd.Parameters.AddWithValue("@val", value);
                cmd.Parameters.AddWithValue("@slot", slot);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        public static int LoadCera(SqliteConnection connection, int characterId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT coin FROM characters WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    return Convert.ToInt32(result);
                return 0;
            }
        }

        public static void MigrateCeraFromPacketTemplates(SqliteConnection connection)
        {
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM characters WHERE coin != 0";
                if (Convert.ToInt32(check.ExecuteScalar()) > 0)
                    return;
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT character_id, body FROM packet_templates WHERE noti_type = 53";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var cid = reader.GetInt32(0);
                        var body = reader[1] as byte[];
                        if (body != null && body.Length >= 5)
                        {
                            int cera = BitConverter.ToInt32(body, 1);
                            using (var upd = connection.CreateCommand())
                            {
                                upd.CommandText = "UPDATE characters SET coin = @coin WHERE character_id = @cid AND coin = 0;";
                                upd.Parameters.AddWithValue("@coin", cera);
                                upd.Parameters.AddWithValue("@cid", cid);
                                upd.ExecuteNonQuery();
                            }
                        }
                    }
                }
            }
        }
    }

    public sealed class WalletSnapshot
    {
        public int Gold { get; set; }
        public int Cera { get; set; }
    }
}
