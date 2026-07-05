using DfoServer.Game.Characters;
using DfoServer.Game.CharacterData;
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
        private const byte LegacyTitleCarrierSlot = 11;

        public static byte[] UpdateAndBroadcast(
            PlayerContext player,
            SqliteSelectCharacterDataSource dataSource,
            ICharacterRepository characterRepository,
            int characterId, int accountId)
        {
            var updated = LoadRuntimeAppearanceEntries(characterId, characterRepository);

            player.AppearanceEntries = updated;

            return BuildNoti2Body(player);
        }

        public static byte[] SetCloneTitleAndBroadcast(
            PlayerContext player,
            ICharacterRepository characterRepository,
            int characterId,
            int cloneTitleItemId)
        {
            SaveCloneTitleItemId(characterId, cloneTitleItemId);
            var tail = player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
            tail.CloneTitleItemId = (uint)(cloneTitleItemId > 0 ? cloneTitleItemId : 0);
            player.Subtype0Tail = tail;
            var updated = LoadRuntimeAppearanceEntries(characterId, characterRepository);

            player.AppearanceEntries = updated;

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
                var tail = player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
                tail.CloneTitleItemId = (uint)(cloneTitleItemId > 0 ? cloneTitleItemId : 0);
                player.Subtype0Tail = tail;
                player.AppearanceEntries = LoadRuntimeAppearanceEntries(player.CharacterId, null);
            }

            return BuildNoti2Body(player);
        }

        public static byte[] BuildCloneTitleAckBody(int cloneTitleItemId, byte state = 0, byte suppressMessage = 0)
        {
            var ack = new byte[5];
            ack[0] = 0x01;
            BitConverter.GetBytes(cloneTitleItemId).CopyTo(ack, 1);
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
                // 外观列表的 itemId 保持真实穿戴模板；替换称号动画还会由 subtype0 tail 首字段刷新。
                if (entry.Slot >= TitleAppearanceSlot) continue;
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
                    (byte)entry.Slot, displayItemId, 4, new byte[4], 0, 0, 0u, 0));
            }

            return result.ToArray();
        }

        public static CharacterAppearanceEntry[] LoadRuntimeAppearanceEntries(
            int characterId,
            ICharacterRepository characterRepository)
        {
            var rebuilt = LoadAppearanceFromEquipEntries(characterId);
            RepairLegacyTitleAppearanceBlobIfNeeded(characterId);

            var stored = characterRepository?.GetById(characterId)?.Appearance;
            if (stored == null || stored.Length == 0)
                return rebuilt;

            return MergeRuntimeAppearance(stored, rebuilt);
        }

        private static CharacterAppearanceEntry[] MergeRuntimeAppearance(
            CharacterAppearanceEntry[] stored,
            CharacterAppearanceEntry[] rebuilt)
        {
            var rebuiltBySlot = new Dictionary<byte, CharacterAppearanceEntry>();
            if (rebuilt != null)
            {
                foreach (var entry in rebuilt)
                {
                    if (entry == null || entry.Slot >= LegacyTitleCarrierSlot)
                        continue;
                    rebuiltBySlot[entry.Slot] = entry;
                }
            }

            var result = new List<CharacterAppearanceEntry>();
            var seen = new HashSet<byte>();
            foreach (var entry in stored)
            {
                if (entry == null)
                    continue;

                if (entry.Slot < LegacyTitleCarrierSlot && rebuiltBySlot.TryGetValue(entry.Slot, out var replacement))
                {
                    result.Add(CloneAppearanceEntry(replacement));
                    seen.Add(entry.Slot);
                    continue;
                }

                result.Add(CloneAppearanceEntry(entry));
                seen.Add(entry.Slot);
            }

            foreach (var entry in rebuiltBySlot.Values)
            {
                if (!seen.Contains(entry.Slot))
                    result.Add(CloneAppearanceEntry(entry));
            }

            return result.ToArray();
        }

        private static CharacterAppearanceEntry CloneAppearanceEntry(CharacterAppearanceEntry entry)
        {
            var expansionData = entry.ExpansionData != null
                ? (byte[])entry.ExpansionData.Clone()
                : new byte[4];
            return new CharacterAppearanceEntry(
                entry.Slot,
                entry.DisplayItemId,
                entry.ExpansionLen,
                expansionData,
                entry.State,
                entry.LinkItemId,
                entry.EnchantValue,
                entry.Flag20);
        }

        public static void RepairLegacyTitleAppearanceBlobIfNeeded(int characterId)
        {
            if (characterId <= 0)
                return;

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                if (!HasEquippedPetCreature(connection, characterId))
                    return;

                var blob = LoadAppearanceBlob(connection, characterId);
                var titleItemId = LoadEquippedTitleItemId(connection, characterId);
                if (!TryRepairLegacyTitleTail(blob, titleItemId, out var repaired))
                    return;

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE characters
SET appearance_blob = @blob,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid;";
                    command.Parameters.AddWithValue("@blob", repaired);
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.ExecuteNonQuery();
                }

                FileLogger.Log($"[AppearanceService] repaired legacy title appearance blob cid={characterId}");
            }
        }

        private static bool HasEquippedPetCreature(SqliteConnection connection, int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT 1
FROM character_equipped_entries
WHERE character_id = @cid
  AND slot = 24
  AND item_id > 0
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                return command.ExecuteScalar() != null;
            }
        }

        private static byte[] LoadAppearanceBlob(SqliteConnection connection, int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT appearance_blob FROM characters WHERE character_id = @cid LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                return command.ExecuteScalar() as byte[];
            }
        }

        private static int LoadEquippedTitleItemId(SqliteConnection connection, int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT item_id
FROM character_equipped_entries
WHERE character_id = @cid
  AND slot = @slot
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@slot", (int)TitleAppearanceSlot);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
            }
        }

        private static bool TryRepairLegacyTitleTail(byte[] blob, int equippedTitleItemId, out byte[] repaired)
        {
            repaired = null;
            const int entrySize = 23;
            const int legacyCount = 13;
            const byte carrierSlot = 11;

            if (blob == null || blob.Length < 1 + 12 * entrySize)
                return false;

            var count = blob[0];
            if (count != 12 && count != legacyCount)
                return false;

            var carrierStart = 1 + carrierSlot * entrySize;
            if (carrierStart + entrySize > blob.Length || blob[carrierStart] != carrierSlot)
                return false;

            var titleItemId = equippedTitleItemId;
            if (count == legacyCount)
            {
                var titleStart = carrierStart + entrySize;
                if (titleStart + entrySize > blob.Length)
                    return false;

                var hasNormalTitleEntry = blob[titleStart] == TitleAppearanceSlot
                    && BitConverter.ToInt32(blob, titleStart + 5) == 4;
                if (!hasNormalTitleEntry)
                    return false;

                if (titleItemId <= 0)
                    titleItemId = BitConverter.ToInt32(blob, titleStart + 1);
            }
            else if (BitConverter.ToInt32(blob, carrierStart + 5) != 4)
            {
                return false;
            }

            var targetLength = 1 + legacyCount * entrySize;
            repaired = new byte[targetLength];
            Buffer.BlockCopy(blob, 0, repaired, 0, Math.Min(carrierStart, blob.Length));
            repaired[0] = legacyCount;

            // Old TW server stores title data across the slot11 tail and the following placeholder record.
            // Rebuilt normal slot12 entries make later subtype0 minimum packets drop active creature state.
            repaired[carrierStart] = carrierSlot;
            Buffer.BlockCopy(blob, carrierStart + 1, repaired, carrierStart + 1, 4);
            repaired[carrierStart + 9] = 0x19;
            repaired[carrierStart + 19] = TitleAppearanceSlot;
            BitConverter.GetBytes(titleItemId > 0 ? titleItemId : 0).CopyTo(repaired, carrierStart + 20);

            if (blob.Length == repaired.Length)
            {
                var changed = false;
                for (var i = 0; i < blob.Length; i++)
                {
                    if (blob[i] == repaired[i])
                        continue;
                    changed = true;
                    break;
                }

                if (!changed)
                {
                    repaired = null;
                    return false;
                }
            }

            return true;
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

            if (record.Subtype0Tail == null && player.CharacterId > 0)
            {
                record.Subtype0Tail = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath)
                    .Load(player.CharacterId);
                player.Subtype0Tail = record.Subtype0Tail;
            }

            if (record.Subtype0Tail == null)
                record.Subtype0Tail = new UserInfoMinimumTailSnapshot();

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
