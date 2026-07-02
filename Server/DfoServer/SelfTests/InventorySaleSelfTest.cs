using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class InventorySaleSelfTest
    {
        private const int AccountId = 910016;
        private const int CharacterId = 910116;
        private const short ConsumableSlot = 86;
        private const short LegacyPackageBeadSlot = 87;
        private const short LowPositiveStackableSlot = 88;
        private const int ShiningRemyAidItemId = 2660671;
        private const int LegacyPackageBeadItemId = 10008360;
        private const int LowPositiveStackableItemId = 1004;
        private const int StartingGold = 12345;
        private const int LegacyPackageBeadStartingStack = 53;
        private const int LegacyPackageBeadSaleCount = 4;
        private const int FutureExpireTime = 2147480000;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== INVENTORY_SALE selftest ===");

            var metadata = ItemMetadataResolver.Resolve(ShiningRemyAidItemId);
            Check("shining remy aid is stackable", metadata.IsStackable);
            Check("shining remy aid has zero sell gold", metadata.SellGold == 0);

            var packageBeadMetadata = ItemMetadataResolver.Resolve(LegacyPackageBeadItemId);
            Check("legacy package bead is stackable by metadata", packageBeadMetadata.IsStackable);
            Check("legacy package bead has zero sell gold", packageBeadMetadata.SellGold == 0);

            var lowPositiveMetadata = ItemMetadataResolver.Resolve(LowPositiveStackableItemId);
            Check("low positive stackable is stackable", lowPositiveMetadata.IsStackable);
            Check("low positive stackable keeps one-gold floor", lowPositiveMetadata.SellGold == 1);

            var tempDb = Path.Combine(Path.GetTempPath(), "inventory_sale_selftest.db");
            DeleteTempDatabase(tempDb);

            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);
            SeedCharacterAndItem(tempDb);

            InventoryMutationResult result = null;
            Check("zero-price consumable sale succeeds", store.TrySellItem(CharacterId, AccountId, InventoryListType.Main, ConsumableSlot, 4, out result));

            if (result != null)
            {
                Check("sale leaves gold unchanged", result.UpdatedGold == StartingGold);
                Check("sale applies requested count", result.AppliedCount == 4);
                Check("sale decrements remaining stack", result.RemainingStackCount == 96);

                var ack = SellItemBuilder.Build((byte)InventoryListType.Main, result.SlotIndex, result.AppliedCount, result.UpdatedGold);
                Check("sell ACK success flag", ack.Length >= 1 && ack[0] == 1);
                Check("sell ACK unchanged gold", ack.Length >= 5 && BitConverter.ToInt32(ack, 1) == StartingGold);
                Check("sell ACK source slot", ack.Length >= 8 && BitConverter.ToInt16(ack, 6) == ConsumableSlot);
                Check("sell ACK applied count", ack.Length >= 10 && BitConverter.ToInt16(ack, 8) == 4);
            }

            InventoryMutationResult legacyPackageResult = null;
            Check("legacy special-kind package bead sale succeeds", store.TrySellItem(CharacterId, AccountId, InventoryListType.Main, LegacyPackageBeadSlot, LegacyPackageBeadSaleCount, out legacyPackageResult));

            if (legacyPackageResult != null)
            {
                Check("legacy package bead sale leaves gold unchanged", legacyPackageResult.UpdatedGold == StartingGold);
                Check("legacy package bead sale applies requested count", legacyPackageResult.AppliedCount == LegacyPackageBeadSaleCount);
                Check("legacy package bead sale decrements remaining stack", legacyPackageResult.RemainingStackCount == LegacyPackageBeadStartingStack - LegacyPackageBeadSaleCount);

                var ack = SellItemBuilder.Build((byte)InventoryListType.Main, legacyPackageResult.SlotIndex, legacyPackageResult.AppliedCount, legacyPackageResult.UpdatedGold);
                Check("legacy package bead sell ACK unchanged gold", ack.Length >= 5 && BitConverter.ToInt32(ack, 1) == StartingGold);
                Check("legacy package bead sell ACK source slot", ack.Length >= 8 && BitConverter.ToInt16(ack, 6) == LegacyPackageBeadSlot);
                Check("legacy package bead sell ACK applied count", ack.Length >= 10 && BitConverter.ToInt16(ack, 8) == LegacyPackageBeadSaleCount);
            }

            InventoryMutationResult lowPositiveResult = null;
            Check("low positive stackable sale succeeds", store.TrySellItem(CharacterId, AccountId, InventoryListType.Main, LowPositiveStackableSlot, 1, out lowPositiveResult));

            if (lowPositiveResult != null)
            {
                Check("low positive stackable sale adds one gold", lowPositiveResult.UpdatedGold == StartingGold + 1);
                Check("low positive stackable sale applies one", lowPositiveResult.AppliedCount == 1);
                Check("low positive stackable sale decrements remaining stack", lowPositiveResult.RemainingStackCount == 1);

                var ack = SellItemBuilder.Build((byte)InventoryListType.Main, lowPositiveResult.SlotIndex, lowPositiveResult.AppliedCount, lowPositiveResult.UpdatedGold);
                Check("low positive stackable sell ACK updated gold", ack.Length >= 5 && BitConverter.ToInt32(ack, 1) == StartingGold + 1);
                Check("low positive stackable sell ACK source slot", ack.Length >= 8 && BitConverter.ToInt16(ack, 6) == LowPositiveStackableSlot);
                Check("low positive stackable sell ACK applied count", ack.Length >= 10 && BitConverter.ToInt16(ack, 8) == 1);
            }

            {
                var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
                var remaining = snapshot.MainItems.Find(x => x.SlotIndex == ConsumableSlot);
                Check("snapshot still has partial stack", remaining != null);
                if (remaining != null)
                {
                    Check("snapshot remaining item id", remaining.ItemTemplateId == ShiningRemyAidItemId);
                    Check("snapshot remaining stack count", remaining.CountOrInstanceValue == 96);
                }

                var legacyPackageBead = snapshot.MainItems.Find(x => x.SlotIndex == LegacyPackageBeadSlot);
                Check("snapshot still has legacy package bead partial stack", legacyPackageBead != null);
                if (legacyPackageBead != null)
                {
                    Check("snapshot legacy package bead item id", legacyPackageBead.ItemTemplateId == LegacyPackageBeadItemId);
                    Check("snapshot legacy package bead stack count", legacyPackageBead.CountOrInstanceValue == LegacyPackageBeadStartingStack - LegacyPackageBeadSaleCount);
                }

                var lowPositive = snapshot.MainItems.Find(x => x.SlotIndex == LowPositiveStackableSlot);
                Check("snapshot still has low positive partial stack", lowPositive != null);
                if (lowPositive != null)
                {
                    Check("snapshot low positive item id", lowPositive.ItemTemplateId == LowPositiveStackableItemId);
                    Check("snapshot low positive stack count", lowPositive.CountOrInstanceValue == 1);
                }
            }

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static void SeedCharacterAndItem(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'inventory-sale-selftest', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name, gold)
VALUES (@characterId, @accountId, 'inventory-sale-selftest', @gold);

UPDATE characters
SET gold = @gold
WHERE character_id = @characterId;

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 0, 24);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, 0, 0, 'special',
    @gold, @gold, 0, 0, 0, 0, 0,
    0, '{}');

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, @slot, @templateId, 'stackable',
    100, 100, 0, 0, 0, 0, 0,
    0, '{}');

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, @legacyPackageBeadSlot, @legacyPackageBeadTemplateId, 'special',
    @legacyPackageBeadStack, @legacyPackageBeadStack, 0, 0, 0, @futureExpireTime, 0,
    0, '{}');

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, @lowPositiveSlot, @lowPositiveTemplateId, 'stackable',
    2, 2, 0, 0, 0, 0, 0,
    0, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@gold", StartingGold);
                    command.Parameters.AddWithValue("@slot", ConsumableSlot);
                    command.Parameters.AddWithValue("@templateId", ShiningRemyAidItemId);
                    command.Parameters.AddWithValue("@legacyPackageBeadSlot", LegacyPackageBeadSlot);
                    command.Parameters.AddWithValue("@legacyPackageBeadTemplateId", LegacyPackageBeadItemId);
                    command.Parameters.AddWithValue("@legacyPackageBeadStack", LegacyPackageBeadStartingStack);
                    command.Parameters.AddWithValue("@futureExpireTime", FutureExpireTime);
                    command.Parameters.AddWithValue("@lowPositiveSlot", LowPositiveStackableSlot);
                    command.Parameters.AddWithValue("@lowPositiveTemplateId", LowPositiveStackableItemId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            try
            {
                if (File.Exists(databasePath))
                    File.Delete(databasePath);

                var wal = databasePath + "-wal";
                if (File.Exists(wal))
                    File.Delete(wal);

                var shm = databasePath + "-shm";
                if (File.Exists(shm))
                    File.Delete(shm);
            }
            catch
            {
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok)
                _pass++;
            else
                _fail++;
        }

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }
    }
}
