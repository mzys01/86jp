using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class ChronicleGrowthSelfTest
    {
        private const int AccountId = 941109;
        private const int CharacterId = 941209;
        private const int TargetItemId = 135000;
        private const int NormalTicketId = 10094062;
        private const int AdvancedTicketId = 10094063;
        private const short TargetSlot = 13;
        private const short NormalTicketSlot = 105;
        private const short AdvancedTicketSlot = 106;
        private const short FragmentSlot = 125;
        private static int _failures;

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== CHRONICLE_GROWTH selftest ===");

            TestPvfParsing();
            TestProtocol();
            TestCostFormula();
            TestStore();

            Console.WriteLine(_failures == 0 ? "ChronicleGrowthSelfTest OK" : $"ChronicleGrowthSelfTest FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void TestPvfParsing()
        {
            Check(ItemMetadataResolver.TryLoadStackableFile(NormalTicketId, out var normal)
                && normal.EmancipateTicket == 5
                && normal.EquipmentLevelEmancipate?.UpgradeLevel == 3
                && normal.EquipmentLevelEmancipate.Condition.Rarities.Contains(5)
                && normal.EquipmentLevelEmancipate.Condition.MinimumLevel == 70
                && normal.EquipmentLevelEmancipate.Condition.MaximumLevel == 86
                && normal.EquipmentLevelEmancipate.IgnoreIndexes.Contains(450114),
                "normal ticket PVF");
            Check(ItemMetadataResolver.TryLoadStackableFile(AdvancedTicketId, out var advanced)
                && advanced.EquipmentLevelEmancipate?.UpgradeLevel == 5,
                "advanced ticket PVF");
        }

        private static void TestProtocol()
        {
            var captured = Hex("69 00 EE 05 9A 00 0D 00 58 0F 02 00 01 7D 00 EF 0C 00 00");
            Check(ChronicleGrowthRequest.TryParse(captured, out var command)
                && command.TicketSlotIndex == NormalTicketSlot
                && command.TicketItemTemplateId == NormalTicketId
                && command.TargetSlotIndex == TargetSlot
                && command.TargetItemTemplateId == TargetItemId
                && command.Materials.Count == 1
                && command.Materials[0].SlotIndex == FragmentSlot
                && command.Materials[0].ItemTemplateId == ChronicleGrowthCostCalculator.FragmentItemTemplateId,
                "captured 0x010F request");
            Check(!ChronicleGrowthRequest.TryParse(captured[..^1], out _), "truncated request rejected");

            var result = new ChronicleGrowthResult { GrowthSucceeded = true };
            result.Consumptions.Add(new ChronicleGrowthConsumption
                { ListType = InventoryListType.Main, SlotIndex = NormalTicketSlot, ConsumedCount = 1 });
            result.Consumptions.Add(new ChronicleGrowthConsumption
                { ListType = InventoryListType.Main, SlotIndex = FragmentSlot, ConsumedCount = 6 });
            var ack = ChronicleGrowthAckBuilder.BuildSuccess(result);
            Check(ack.Length == 17
                && ack[0] == 1 && ack[1] == 1 && ack[2] == 2
                && BitConverter.ToInt16(ack, 4) == NormalTicketSlot
                && BitConverter.ToInt32(ack, 6) == 1
                && BitConverter.ToInt16(ack, 11) == FragmentSlot
                && BitConverter.ToInt32(ack, 13) == 6,
                "success response consumptions");
        }

        private static void TestCostFormula()
        {
            Check(ChronicleGrowthCostCalculator.Calculate(70, Game.ItemUpgrade.EquipmentType.Coat, 0, 0, 0) == 6,
                "Lv70 +0 coat costs 6 fragments");
            Check(ChronicleGrowthCostCalculator.Calculate(70, Game.ItemUpgrade.EquipmentType.Coat, 3, 0, 0) == 7,
                "Lv70 +3 coat truncates to 7 fragments");
            var levels = new[] { 70, 73, 75, 76, 79, 80, 82, 85 };
            var forgingCosts = new[]
            {
                new[] { 7, 8, 9, 9, 10, 10, 11, 12 },
                new[] { 7, 9, 9, 10, 11, 11, 12, 13 },
                new[] { 8, 9, 10, 11, 12, 12, 13, 15 },
                new[] { 9, 11, 12, 12, 14, 14, 15, 17 },
                new[] { 10, 12, 13, 14, 15, 16, 17, 18 },
                new[] { 14, 16, 18, 18, 20, 21, 23, 25 },
                new[] { 18, 20, 22, 23, 26, 27, 28, 31 },
                new[] { 21, 24, 27, 28, 31, 32, 34, 37 },
                new[] { 7, 8, 9, 9, 10, 10, 11, 12 },
            };
            for (var forging = 0; forging < forgingCosts.Length; forging++)
            {
                for (var levelIndex = 0; levelIndex < levels.Length; levelIndex++)
                {
                    var expected = forgingCosts[forging][levelIndex];
                    Check(ChronicleGrowthCostCalculator.Calculate(levels[levelIndex],
                            Game.ItemUpgrade.EquipmentType.Weapon, 0, 0,
                            ChronicleGrowthCostCalculator.ResolveCostGenuineGrade(forging)) == expected,
                        $"Lv{levels[levelIndex]} forging +{forging} weapon costs {expected} fragments");
                }
            }
        }

        private static void TestStore()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests", "chronicle-growth.db");
            DeleteDatabase(databasePath);
            Seed(databasePath);
            var store = new SqliteInventoryStore(databasePath, ServerPaths.SchemaFilePath);

            var normal = CreateCommand(NormalTicketSlot, NormalTicketId);
            Check(store.TryGrowChronicleEquipment(CharacterId, AccountId, normal, out var normalResult)
                && normalResult.GrowthSucceeded
                && normalResult.OldLevel == 70
                && normalResult.NewLevel == 73
                && normalResult.RequiredFragmentCount == 6,
                "normal ticket grows forged equipment 70 to 73 without a forging surcharge");
            Check(ReadGrowthLevel(store) == 3 && store.CountItem(CharacterId, NormalTicketId) == 0
                && store.CountItem(CharacterId, ChronicleGrowthCostCalculator.FragmentItemTemplateId) == 94,
                "normal growth persists and consumes atomically");

            var advanced = CreateCommand(AdvancedTicketSlot, AdvancedTicketId);
            Check(store.TryGrowChronicleEquipment(CharacterId, AccountId, advanced, out var advancedResult)
                && advancedResult.GrowthSucceeded
                && advancedResult.OldLevel == 73
                && advancedResult.NewLevel == 78,
                "advanced ticket grows 73 to 78");

            SetGrowthLevel(databasePath, 15);
            AddTicket(databasePath, AdvancedTicketSlot, AdvancedTicketId);
            Check(store.TryGrowChronicleEquipment(CharacterId, AccountId, advanced, out var cappedResult)
                && cappedResult.NewLevel == 86 && ReadGrowthLevel(store) == 16,
                "advanced ticket caps at 86");

            AddTicket(databasePath, AdvancedTicketSlot, AdvancedTicketId);
            Check(!store.TryGrowChronicleEquipment(CharacterId, AccountId, advanced, out var maximumResult)
                && maximumResult.ErrorCode == ChronicleGrowthResult.ErrorMaximumLevel
                && store.CountItem(CharacterId, AdvancedTicketId) == 1,
                "maximum level rejects without consuming");
        }

        private static ChronicleGrowthCommand CreateCommand(short ticketSlot, int ticketId)
        {
            var command = new ChronicleGrowthCommand
            {
                TicketSlotIndex = ticketSlot,
                TicketItemTemplateId = ticketId,
                TargetSlotIndex = TargetSlot,
                TargetItemTemplateId = TargetItemId,
            };
            command.Materials.Add(new ChronicleGrowthMaterialRequest
                { SlotIndex = FragmentSlot, ItemTemplateId = ChronicleGrowthCostCalculator.FragmentItemTemplateId });
            return command;
        }

        private static void Seed(string databasePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath));
            using var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash) VALUES (@aid, 'chronicle-growth', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name) VALUES (@cid, @aid, 'chronicle-growth');
INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16) VALUES (@cid, 0, 24);
INSERT OR REPLACE INTO character_items (owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json)
VALUES ('character', @cid, @cid, 0, @targetSlot, @targetId, 'equipment', @quality, @quality, 40, 0, 0, 0, -1, 0, @extraJson);
INSERT OR REPLACE INTO character_items (owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json)
VALUES ('character', @cid, @cid, 0, @normalSlot, @normalId, 'stackable', 1, 1, 0, 0, 0, 0, 0, 0, '{}');
INSERT OR REPLACE INTO character_items (owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json)
VALUES ('character', @cid, @cid, 0, @advancedSlot, @advancedId, 'stackable', 1, 1, 0, 0, 0, 0, 0, 0, '{}');
INSERT OR REPLACE INTO character_items (owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json)
VALUES ('character', @cid, @cid, 0, @fragmentSlot, @fragmentId, 'stackable', 100, 100, 0, 0, 0, 0, 0, 0, '{}');";
            command.Parameters.AddWithValue("@aid", AccountId);
            command.Parameters.AddWithValue("@cid", CharacterId);
            command.Parameters.AddWithValue("@targetSlot", TargetSlot);
            command.Parameters.AddWithValue("@targetId", TargetItemId);
            command.Parameters.AddWithValue("@quality", unchecked((int)ItemQuality.TopQualitySeed));
            command.Parameters.AddWithValue("@extraJson", InventoryItemCodec.SerializeCommon(CreateTarget()));
            command.Parameters.AddWithValue("@normalSlot", NormalTicketSlot);
            command.Parameters.AddWithValue("@normalId", NormalTicketId);
            command.Parameters.AddWithValue("@advancedSlot", AdvancedTicketSlot);
            command.Parameters.AddWithValue("@advancedId", AdvancedTicketId);
            command.Parameters.AddWithValue("@fragmentSlot", FragmentSlot);
            command.Parameters.AddWithValue("@fragmentId", ChronicleGrowthCostCalculator.FragmentItemTemplateId);
            command.ExecuteNonQuery();
        }

        private static CommonInventoryItem CreateTarget()
        {
            var tail = new byte[37];
            tail[27] = 8;
            var item = new CommonInventoryItem
            {
                SlotIndex = TargetSlot,
                ItemTemplateId = TargetItemId,
                CountOrInstanceValue = unchecked((int)ItemQuality.TopQualitySeed),
                Durability = 40,
                Marker16 = -1,
                TailData2F = tail,
                JewelSocket = new byte[30],
            };
            return item;
        }

        private static byte ReadGrowthLevel(SqliteInventoryStore store)
        {
            var snapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
            var item = snapshot.MainItems.Find(entry => entry.SlotIndex == TargetSlot);
            return item == null ? (byte)0 : new InventoryItemEntry84View(
                item.SlotIndex, item.ItemTemplateId, item.CountOrInstanceValue, item.ExtData0, item.Durability,
                item.SealFlag, item.PrefixData0E, item.Marker16, item.MiddleData1A, item.ExpireTime,
                item.TailData2F, item.JewelSocket, null).EmancipateEquipmentLevel;
        }

        private static void SetGrowthLevel(string databasePath, byte value)
        {
            using var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath));
            connection.Open();
            using var load = connection.CreateCommand();
            load.CommandText = "SELECT extra_json FROM character_items WHERE character_id=@cid AND list_type=0 AND slot_index=@slot";
            load.Parameters.AddWithValue("@cid", CharacterId);
            load.Parameters.AddWithValue("@slot", TargetSlot);
            var extra = Convert.ToString(load.ExecuteScalar());
            var tail = InventoryItemCodec.ReadHexValue(extra, "tailData2F", 37);
            tail[28] = value;
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE character_items SET extra_json=@json WHERE character_id=@cid AND list_type=0 AND slot_index=@slot";
            update.Parameters.AddWithValue("@json", extra.Replace(InventoryItemCodec.ToHex(InventoryItemCodec.ReadHexValue(extra, "tailData2F", 37)), InventoryItemCodec.ToHex(tail)));
            update.Parameters.AddWithValue("@cid", CharacterId);
            update.Parameters.AddWithValue("@slot", TargetSlot);
            update.ExecuteNonQuery();
        }

        private static void AddTicket(string databasePath, short slot, int itemId)
        {
            using var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"INSERT OR REPLACE INTO character_items (owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json)
VALUES ('character', @cid, @cid, 0, @slot, @itemId, 'stackable', 1, 1, 0, 0, 0, 0, 0, 0, '{}');";
            command.Parameters.AddWithValue("@cid", CharacterId);
            command.Parameters.AddWithValue("@slot", slot);
            command.Parameters.AddWithValue("@itemId", itemId);
            command.ExecuteNonQuery();
        }

        private static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", string.Empty));

        private static void DeleteDatabase(string path)
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(candidate)) File.Delete(candidate);
        }

        private static void Check(bool condition, string label)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {label}");
            if (!condition) _failures++;
        }
    }
}
