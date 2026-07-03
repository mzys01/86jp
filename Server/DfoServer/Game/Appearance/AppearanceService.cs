using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Appearance
{
    public static class AppearanceService
    {
        private const byte TitleAppearanceSlot = 12;

        public static byte[] UpdateAndBroadcast(
            PlayerContext player,
            SqliteSelectCharacterDataSource dataSource,
            ICharacterRepository characterRepository,
            int characterId, int accountId)
        {
            var updated = LoadAppearanceFromEquipEntries(characterId);

            player.AppearanceEntries = updated;
            characterRepository.UpdateAppearance(characterId, updated);

            return BuildNoti2Body(player);
        }

        public static byte[] SetCloneTitleAndBroadcast(
            PlayerContext player,
            ICharacterRepository characterRepository,
            int characterId,
            int cloneTitleItemId)
        {
            SaveCloneTitleItemId(characterId, cloneTitleItemId);
            var updated = LoadAppearanceFromEquipEntries(characterId);

            player.AppearanceEntries = updated;
            characterRepository.UpdateAppearance(characterId, updated);

            return BuildNoti2Body(player);
        }

        public static void PersistCloneTitle(int characterId, int cloneTitleItemId)
        {
            SaveCloneTitleItemId(characterId, cloneTitleItemId);
        }

        public static byte[] SetRuntimeCloneTitleAndBuildNoti2(PlayerContext player, int cloneTitleItemId)
        {
            if (player == null)
                return Array.Empty<byte>();

            if (player.CharacterId > 0)
            {
                SaveCloneTitleItemId(player.CharacterId, cloneTitleItemId);
                player.AppearanceEntries = LoadAppearanceFromEquipEntries(player.CharacterId);
            }

            return BuildNoti2Body(player);
        }

        public static byte[] BuildCloneTitleAckBody(int cloneTitleItemId, byte state = 0, byte suppressMessage = 0)
        {
            var ack = new byte[7];
            ack[0] = 0x01;
            BitConverter.GetBytes(cloneTitleItemId).CopyTo(ack, 1);
            ack[5] = state;
            ack[6] = suppressMessage;
            return ack;
        }

        public static CharacterAppearanceEntry[] LoadAppearanceFromEquipEntries(int characterId)
        {
            var result = new List<CharacterAppearanceEntry>();
            var dbPath = ServerPaths.DatabasePath;
            var schemaPath = ServerPaths.SchemaFilePath;
            var repo = new Game.CharacterData.SqliteSubtype1Repository(dbPath, schemaPath);

            if (!repo.HasData(characterId))
                return result.ToArray();

            var addition = repo.Load(characterId);
            if (addition.EquippedEntries == null)
                return result.ToArray();

            foreach (var entry in addition.EquippedEntries)
            {
                // 外观列表的 itemId 是显示用模板ID；称号替换动画由 0x0239 独立状态恢复。
                if (entry.Slot > TitleAppearanceSlot) continue;
                if (entry.ItemId == 0) continue;

                int displayItemId = entry.ItemId;
                if (entry.Slot <= 9
                    && entry.RawEntry != null
                    && entry.RawEntry.Length >= 16)
                {
                    uint cloneTarget = BitConverter.ToUInt32(entry.RawEntry, 12);
                    if (cloneTarget > 0)
                        displayItemId = (int)cloneTarget;
                }

                result.Add(new CharacterAppearanceEntry(
                    (byte)entry.Slot, displayItemId, 4, new byte[4], 0x00, 0, 0u, 0));
            }

            return result.ToArray();
        }

        public static int LoadCloneTitleItemId(int characterId)
        {
            if (characterId <= 0)
                return 0;

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = "SELECT clone_title_item_id FROM characters WHERE character_id = @cid LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
            }
        }

        public static void SaveCloneTitleItemId(int characterId, int cloneTitleItemId)
        {
            if (characterId <= 0)
                return;

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = @"
UPDATE characters
SET clone_title_item_id = @cloneTitleItemId,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cloneTitleItemId", cloneTitleItemId > 0 ? cloneTitleItemId : 0);
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
        }

        public static byte[] BuildNoti2Body(PlayerContext player)
        {
            var record = new CharacterRecord
            {
                CharacterId = player.CharacterId,
                Name = player.Name,
                Job = player.Job,
                GrowType = player.GrowType,
                Level = player.Level,
                UserState = player.UserState,
                Appearance = player.AppearanceEntries,
                Subtype0Tail = player.Subtype0Tail,
            };

            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteUInt16(0x0001);
            writer.WriteUInt16(player.UserId);
            writer.WriteDstr(player.Name);
            writer.WriteBytes(UserInfoSubtype0Builder.BuildRemainingBytes(record));
            return writer.ToArray();
        }
    }
}
