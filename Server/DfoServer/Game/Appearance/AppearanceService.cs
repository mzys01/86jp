using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Appearance
{
    public static class AppearanceService
    {
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
                if (entry.Slot > 11) continue;
                if (entry.ItemId == 0) continue;

                int displayItemId = entry.ItemId;
                if (entry.Slot <= 9 && entry.RawEntry != null && entry.RawEntry.Length >= 16)
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
