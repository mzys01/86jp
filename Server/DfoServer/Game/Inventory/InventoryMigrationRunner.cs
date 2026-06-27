using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryMigrationRunner
    {
        public void RunMigrations(SqliteConnection connection)
        {
            RunMigrationsInternal(connection);
        }

        internal void RunMigrationsInternal(SqliteConnection connection)
        {
            DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(connection, "characters", new[]
            {
                ("direction", "INTEGER NOT NULL DEFAULT 5"),
                ("area_state", "INTEGER NOT NULL DEFAULT 3"),
                ("appearance_blob", "BLOB"),
                ("delete_flag", "INTEGER NOT NULL DEFAULT 0"),
            });
            DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(connection, "account_cargo_state", new[]
            {
                ("item_count", "INTEGER NOT NULL DEFAULT 0"),
            });
            DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(connection, "character_equipped_entries", new[]
            {
                ("expire_time", "INTEGER NOT NULL DEFAULT 0"),
            });
            // 点券/代币券/欢乐代币券账号化: 旧库补列(账号级钱包)
            DfoServer.Sqlite.SqliteSchemaMigrator.EnsureColumns(connection, "accounts", new[]
            {
                ("cera", "INTEGER NOT NULL DEFAULT 0"),
                ("token_cera", "INTEGER NOT NULL DEFAULT 0"),
                ("happy_token_cera", "INTEGER NOT NULL DEFAULT 0"),
            });
            DfoServer.Sqlite.SqliteSchemaMigrator.MigrateCharacterItemsUniqueConstraint(connection);
            CurrencyService.MigrateCeraFromPacketTemplates(connection);
            MigrateAccountCargoFromPacketTemplates(connection);
            MigrateSubtype1BlobIfNeeded(connection);
            DfoServer.Game.CharacterData.SqliteSubtype0FieldsRepository.MigrateFromBlobIfNeeded(connection);
        }

        private void MigrateSubtype1BlobIfNeeded(SqliteConnection connection)
        {
            try
            {
                bool hasNewShape;
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(character_subtype1_fields);";
                    hasNewShape = false;
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            if (string.Equals(r.GetString(1), "name_tag_item_id", StringComparison.OrdinalIgnoreCase))
                                hasNewShape = true;
                }
                if (!hasNewShape)
                {
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = @"
ALTER TABLE character_subtype1_fields ADD COLUMN name_tag_item_id INTEGER NOT NULL DEFAULT 0;
ALTER TABLE character_subtype1_fields ADD COLUMN name_tag_expire_time INTEGER NOT NULL DEFAULT 0;
DELETE FROM character_subtype1_fields;";
                        cmd.ExecuteNonQuery();
                    }
                    FileLogger.Log("[MigrateSubtype1] 检测到旧列形(无 name_tag_item_id): 已加列并清空, 从 equip_list_blob 重迁移");
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM character_subtype1_fields;";
                    if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return;
                }
                var cids = new List<int>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT character_id FROM equipped_items WHERE equip_list_blob IS NOT NULL;";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read()) cids.Add(r.GetInt32(0));
                    }
                }
                foreach (var cid in cids)
                    DfoServer.Game.CharacterData.Subtype1BlobMigrator.Migrate(connection, cid);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[MigrateSubtype1] ERROR: {ex}");
            }
        }

        private void MigrateAccountCargoFromPacketTemplates(SqliteConnection connection)
        {
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM character_items WHERE owner_scope = 'account' AND list_type = 12;";
                if (Convert.ToInt32(check.ExecuteScalar()) > 0)
                    return;
            }
            byte[] body = null;
            int cid = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT pt.character_id, pt.body FROM packet_templates pt WHERE pt.noti_type = 13 AND pt.occurrence_index = (SELECT MAX(ps.occurrence_index) FROM packet_sequence ps WHERE ps.character_id = pt.character_id AND ps.noti_type = 13 AND ps.kind = 1);";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var b = r[1] as byte[];
                        if (b != null && b.Length > 0 && b[0] == (byte)InventoryListType.AccountCargo)
                        {
                            body = b;
                            cid = r.GetInt32(0);
                            break;
                        }
                    }
                }
            }
            if (body == null || body.Length < 9) return;
            int itemCount = BitConverter.ToUInt16(body, 3);
            if (itemCount == 0) return;

            int accountId = 1;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT account_id FROM characters WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", cid);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value) accountId = Convert.ToInt32(result);
            }

            using (var tx = connection.BeginTransaction())
            {
                int offset = 9;
                for (int i = 0; i < itemCount && offset + 84 <= body.Length; i++)
                {
                    var entry = CharacterItemListSnapshot.Slice(body, offset, 84);
                    var item = new CommonInventoryItem
                    {
                        SlotIndex = BitConverter.ToInt16(entry, 0),
                        ItemTemplateId = BitConverter.ToInt32(entry, 2),
                        CountOrInstanceValue = BitConverter.ToInt32(entry, 6),
                        ExtData0 = entry[10],
                        Durability = BitConverter.ToUInt16(entry, 11),
                        SealFlag = entry[13],
                        PrefixData0E = CharacterItemListSnapshot.Slice(entry, 14, 8),
                        Marker16 = BitConverter.ToInt32(entry, 22),
                        MiddleData1A = CharacterItemListSnapshot.Slice(entry, 26, 17),
                        ExpireTime = BitConverter.ToInt32(entry, 43),
                        TailData2F = CharacterItemListSnapshot.Slice(entry, 47, 37),
                    };
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'account', @ownerId, @characterId, @listType, @slotIndex, @templateId, @itemKind,
    @stackCount, @instanceValue, @durability, @sealFlag, 0, @expireTime, @marker16,
    0, @extraJson);";
                        cmd.Parameters.AddWithValue("@ownerId", accountId);
                        cmd.Parameters.AddWithValue("@characterId", cid);
                        cmd.Parameters.AddWithValue("@listType", (int)InventoryListType.AccountCargo);
                        cmd.Parameters.AddWithValue("@slotIndex", item.SlotIndex);
                        cmd.Parameters.AddWithValue("@templateId", item.ItemTemplateId);
                        cmd.Parameters.AddWithValue("@itemKind", InventoryItemCodec.InferCommonItemKind(item));
                        cmd.Parameters.AddWithValue("@stackCount", item.CountOrInstanceValue);
                        cmd.Parameters.AddWithValue("@instanceValue", item.CountOrInstanceValue);
                        cmd.Parameters.AddWithValue("@durability", item.Durability);
                        cmd.Parameters.AddWithValue("@sealFlag", item.SealFlag);
                        cmd.Parameters.AddWithValue("@expireTime", item.ExpireTime);
                        cmd.Parameters.AddWithValue("@marker16", item.Marker16);
                        cmd.Parameters.AddWithValue("@extraJson", InventoryItemCodec.SerializeCommon(item));
                        cmd.ExecuteNonQuery();
                    }
                    offset += 84;
                }
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE account_cargo_state SET item_count = @ic WHERE account_id = @aid;";
                    cmd.Parameters.AddWithValue("@ic", itemCount);
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
    }
}
