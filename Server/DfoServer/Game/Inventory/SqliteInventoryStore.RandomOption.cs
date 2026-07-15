using DfoServer.Game.Currency;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryUnsealRandomOption(int characterId, int accountId, short targetSlotIndex, int targetItemTemplateId, out RandomOptionUnsealResult result)
        {
            result = null;
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, targetSlotIndex);
                    if (target != null && target.ItemKind == "equipment"
                        && (targetItemTemplateId <= 0 || target.ItemTemplateId == targetItemTemplateId))
                        return UnsealInventoryItem(connection, transaction, characterId, target, out result);

                    if (targetItemTemplateId <= 0)
                        return false;

                    return UnsealEquippedItem(connection, transaction, characterId, targetSlotIndex, targetItemTemplateId, out result);
                }
            }
        }

        public bool TryChangeRandomOption(int characterId, int accountId, short targetSlotIndex, int targetItemTemplateId, byte requestedOptionIndex, out RandomOptionUnsealResult result)
        {
            result = null;
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, targetSlotIndex);
                    if (target != null && target.ItemKind == "equipment"
                        && (targetItemTemplateId <= 0 || target.ItemTemplateId == targetItemTemplateId))
                        return ChangeInventoryItemOption(connection, transaction, characterId, target, requestedOptionIndex, out result);

                    if (targetItemTemplateId <= 0)
                        return false;

                    return ChangeEquippedItemOption(connection, transaction, characterId, targetSlotIndex, targetItemTemplateId, requestedOptionIndex, out result);
                }
            }
        }

        private bool UnsealInventoryItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, ItemRecord target, out RandomOptionUnsealResult result)
        {
            result = null;
            var metadata = ItemMetadataResolver.Resolve(target.ItemTemplateId);
            if (!RandomOptionResolver.TryRollOptions(metadata, out var entries))
                return false;

            var goldCost = RandomOptionResolver.ResolveBreakSealGoldCost(metadata);
            if (goldCost > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, goldCost))
                return false;

            var updatedGold = CurrencyService.LoadWallet(connection, transaction, characterId).Gold;

            var targetView = InventoryItemView.ForCommon(target);
            targetView.Entry84.SetRandomOptions(entries);
            _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
            _auditLogger.WriteAuditLog(connection, transaction, characterId, "unseal_random_option", target, target.ListType, target.SlotIndex, entries.Count);
            transaction.Commit();

            result = new RandomOptionUnsealResult
            {
                TargetListType = target.ListType,
                TargetSlotIndex = target.SlotIndex,
                TargetItemTemplateId = target.ItemTemplateId,
                GoldCost = goldCost,
                UpdatedGold = updatedGold,
                RandomOptions = new List<RandomOptionEntry>(entries),
            };
            return true;
        }

        private bool UnsealEquippedItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, short slotIndex, int itemTemplateId, out RandomOptionUnsealResult result)
        {
            result = null;
            var entry = LoadEquippedEntryForRandomOption(connection, transaction, characterId, slotIndex);
            if (entry == null || entry.ItemId != itemTemplateId || entry.Raw == null || entry.Raw.Length == 0)
                return false;

            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (!RandomOptionResolver.TryRollOptions(metadata, out var entries))
                return false;

            var goldCost = RandomOptionResolver.ResolveBreakSealGoldCost(metadata);
            if (goldCost > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, goldCost))
                return false;

            var updatedGold = CurrencyService.LoadWallet(connection, transaction, characterId).Gold;

            InvenItem item;
            try { item = InvenItem.Parse(entry.Raw); }
            catch { return false; }

            ApplyMagicSealToEquippedItem(item, entries);
            entry.Raw = item.ToBytes();
            UpdateEquippedEntryRawForMagicSeal(connection, transaction, characterId, slotIndex, itemTemplateId, entry.Raw);
            transaction.Commit();

            result = new RandomOptionUnsealResult
            {
                TargetEquipped = true,
                TargetSlotIndex = slotIndex,
                TargetItemTemplateId = itemTemplateId,
                GoldCost = goldCost,
                UpdatedGold = updatedGold,
                RandomOptions = new List<RandomOptionEntry>(entries),
            };
            return true;
        }

        private bool ChangeInventoryItemOption(SqliteConnection connection, SqliteTransaction transaction, int characterId, ItemRecord target, byte requestedOptionIndex, out RandomOptionUnsealResult result)
        {
            result = null;
            var metadata = ItemMetadataResolver.Resolve(target.ItemTemplateId);
            var targetView = InventoryItemView.ForCommon(target);
            var entries = new List<RandomOptionEntry>(targetView.Entry84.RandomOptions);
            if (!TryReplaceSingleOption(metadata, requestedOptionIndex, entries, out var replacedIndex))
                return false;

            var goldCost = RandomOptionResolver.ResolveOptionModificationGoldCost(metadata);
            if (goldCost > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, goldCost))
                return false;

            var updatedGold = CurrencyService.LoadWallet(connection, transaction, characterId).Gold;
            targetView.Entry84.SetRandomOptions(entries);
            _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
            _auditLogger.WriteAuditLog(connection, transaction, characterId, "change_random_option", target, target.ListType, target.SlotIndex, replacedIndex);
            transaction.Commit();

            result = new RandomOptionUnsealResult
            {
                TargetListType = target.ListType,
                TargetSlotIndex = target.SlotIndex,
                TargetItemTemplateId = target.ItemTemplateId,
                GoldCost = goldCost,
                UpdatedGold = updatedGold,
                RandomOptions = new List<RandomOptionEntry>(entries),
                ReplacedOptionIndex = replacedIndex,
                ChangeOptionCandidates = RandomOptionResolver.ResolveChangeOptionCandidates(metadata, replacedIndex),
            };
            return true;
        }

        private bool ChangeEquippedItemOption(SqliteConnection connection, SqliteTransaction transaction, int characterId, short slotIndex, int itemTemplateId, byte requestedOptionIndex, out RandomOptionUnsealResult result)
        {
            result = null;
            var entry = LoadEquippedEntryForRandomOption(connection, transaction, characterId, slotIndex);
            if (entry == null || entry.ItemId != itemTemplateId || entry.Raw == null || entry.Raw.Length == 0)
                return false;

            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            InvenItem item;
            try { item = InvenItem.Parse(entry.Raw); }
            catch { return false; }

            var entries = ReadMagicSealOptionsFromEquipped(item);
            if (!TryReplaceSingleOption(metadata, requestedOptionIndex, entries, out var replacedIndex))
                return false;

            var goldCost = RandomOptionResolver.ResolveOptionModificationGoldCost(metadata);
            if (goldCost > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, goldCost))
                return false;

            var updatedGold = CurrencyService.LoadWallet(connection, transaction, characterId).Gold;
            ApplyMagicSealToEquippedItem(item, entries);
            entry.Raw = item.ToBytes();
            UpdateEquippedEntryRawForMagicSeal(connection, transaction, characterId, slotIndex, itemTemplateId, entry.Raw);
            transaction.Commit();

            result = new RandomOptionUnsealResult
            {
                TargetEquipped = true,
                TargetSlotIndex = slotIndex,
                TargetItemTemplateId = itemTemplateId,
                GoldCost = goldCost,
                UpdatedGold = updatedGold,
                RandomOptions = new List<RandomOptionEntry>(entries),
                ReplacedOptionIndex = replacedIndex,
                ChangeOptionCandidates = RandomOptionResolver.ResolveChangeOptionCandidates(metadata, replacedIndex),
            };
            return true;
        }

        private static bool TryReplaceSingleOption(ItemMetadata metadata, byte requestedOptionIndex, List<RandomOptionEntry> entries, out int replacedIndex)
        {
            replacedIndex = requestedOptionIndex;
            if (entries == null || entries.Count == 0 || replacedIndex >= entries.Count)
                return false;
            if (!RandomOptionResolver.TryRollReplacementOption(metadata, replacedIndex, entries, out var replacement) || replacement == null)
                return false;
            entries[replacedIndex] = replacement;
            return true;
        }

        internal static List<RandomOptionEntry> ReadMagicSealOptions(byte[] tailData2F)
        {
            return new List<RandomOptionEntry>(InventoryItemViewBytes.ParseRandomOptions(tailData2F));
        }

        private static List<RandomOptionEntry> ReadMagicSealOptionsFromEquipped(InvenItem item)
        {
            var result = new List<RandomOptionEntry>();
            if (item?.Seals == null) return result;
            for (var i = 0; i < item.Seals.Count && i < 3; i++)
                result.Add(new RandomOptionEntry { Type = item.Seals[i].Type, Value1 = item.Seals[i].Val1, Value2 = item.Seals[i].Val2 });
            return result;
        }

        private static void ApplyMagicSealToEquippedItem(InvenItem item, IReadOnlyList<RandomOptionEntry> entries)
        {
            item.Seals.Clear();
            foreach (var e in entries)
                item.Seals.Add(new InvenItem.SealEntry { Type = e.Type, Val1 = e.Value1, Val2 = e.Value2 });
            item.SealGenuineUpgrade = 0;
            item.SealCheck = 0xFF;
            item.SealExtra = 0;
        }

        private static MakeEquipListCodec.Entry LoadEquippedEntryForRandomOption(SqliteConnection connection, SqliteTransaction transaction, int characterId, short slotIndex)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT slot, item_id, expire_time, raw_entry FROM character_equipped_entries WHERE character_id = @cid AND slot = @slot LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", (int)slotIndex);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new MakeEquipListCodec.Entry { Slot = r.GetInt32(0), ItemId = r.GetInt32(1), ExpireTime = r.GetInt32(2), Raw = (byte[])r.GetValue(3) };
                }
            }
        }

        private static void UpdateEquippedEntryRawForMagicSeal(SqliteConnection connection, SqliteTransaction transaction, int characterId, short slotIndex, int itemTemplateId, byte[] rawEntry)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE character_equipped_entries SET raw_entry = @raw WHERE character_id = @cid AND slot = @slot AND item_id = @itemId;";
                cmd.Parameters.AddWithValue("@raw", rawEntry);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", (int)slotIndex);
                cmd.Parameters.AddWithValue("@itemId", itemTemplateId);
                cmd.ExecuteNonQuery();
            }
        }

        internal static byte[] NormalizeMagicSealTail(byte[] source)
        {
            var tail = new byte[37];
            if (source != null)
                Buffer.BlockCopy(source, 0, tail, 0, Math.Min(source.Length, 37));
            return tail;
        }

        internal static void WriteMagicSealOptions(byte[] tailData2F, IReadOnlyList<RandomOptionEntry> entries)
        {
            InventoryItemViewBytes.WriteRandomOptions(tailData2F, entries);
        }
    }
}
