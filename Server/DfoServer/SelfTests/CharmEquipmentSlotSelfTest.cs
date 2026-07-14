using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class CharmEquipmentSlotSelfTest
    {
        private const int AccountId = 966001;
        private const int CharacterId = 966002;
        private const int CharmItemId = 400360000;
        private const short FirstCharmSlot = 9;
        private const short WarehouseCharmSlot = 9;
        private const short NormalEquipmentSlot = 11;
        private const short CharmEquipSlot = 29;
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== CHARM_EQUIPMENT_SLOT selftest ===");

            Check("real PVF charm resolves as charm",
                EquipmentTypeInfo.ParseOrUnknown(ItemMetadataResolver.ResolveEquipmentType(CharmItemId)) == EquipmentType.Charm);

            var normalItemId = ResolveNormalEquipmentItemId();
            Check("normal equipment fixture resolved", normalItemId > 0);
            Check("normal equipment is not charm",
                EquipmentTypeInfo.ParseOrUnknown(ItemMetadataResolver.ResolveEquipmentType(normalItemId)) != EquipmentType.Charm);
            var tempDb = Path.Combine(Path.GetTempPath(), "dfo-charm-equipment-slot-selftest.db");
            DeleteTempDatabase(tempDb);
            Seed(tempDb, normalItemId);
            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);

            Check("main inventory starts with one charm", CountItems(tempDb, InventoryListType.Main, CharmItemId) == 1);
            Check("personal cargo charm does not count toward main limit", CountItems(tempDb, InventoryListType.PersonalCargo, CharmItemId) == 1);
            Check("second charm pickup is rejected while main has charm",
                !store.TryPickupItem(CharacterId, AccountId, CharmItemId, 1, out _));
            Check("rejected pickup keeps one main charm", CountItems(tempDb, InventoryListType.Main, CharmItemId) == 1);
            Check("cargo charm cannot enter main while main has charm",
                !MoveToMain(store, InventoryListType.PersonalCargo, WarehouseCharmSlot, 12, out _));
            Check("rejected cargo move keeps charm in cargo", LoadItem(tempDb, InventoryListType.PersonalCargo, WarehouseCharmSlot) == CharmItemId);

            Check("charm equips to slot 29",
                MoveToEquipment(store, FirstCharmSlot, CharmItemId, CharmEquipSlot, out var firstResult)
                && firstResult != null && firstResult.Mutated);
            Check("slot 29 contains charm", LoadEquippedItem(tempDb, CharmEquipSlot) == CharmItemId);
            SetEquippedExpireTime(tempDb, CharmEquipSlot, -1);
            Check("legacy -1 permanent equipment remains in subtype1",
                new SqliteSubtype1Repository(tempDb, ServerPaths.SchemaFilePath)
                    .Load(CharacterId).EquippedEntries.Exists(entry => entry.Slot == CharmEquipSlot));
            SetEquippedExpireTime(tempDb, CharmEquipSlot, 1);
            Check("positive expired equipment is excluded from subtype1",
                !new SqliteSubtype1Repository(tempDb, ServerPaths.SchemaFilePath)
                    .Load(CharacterId).EquippedEntries.Exists(entry => entry.Slot == CharmEquipSlot));
            SetEquippedExpireTime(tempDb, CharmEquipSlot, 0);

            Check("bulk charm pickup is rejected even when main is empty",
                !store.TryPickupItem(CharacterId, AccountId, CharmItemId, 2, out _));
            Check("rejected bulk pickup keeps main without charm", CountItems(tempDb, InventoryListType.Main, CharmItemId) == 0);
            Check("equipped and cargo charms do not block main pickup",
                store.TryPickupItem(CharacterId, AccountId, CharmItemId, 1, out var secondCharmSlot));
            Check("pickup creates exactly one main charm", CountItems(tempDb, InventoryListType.Main, CharmItemId) == 1);

            Check("second charm replaces slot 29",
                MoveToEquipment(store, secondCharmSlot, CharmItemId, CharmEquipSlot, out var replaceResult)
                && replaceResult != null && replaceResult.Mutated);
            Check("replaced charm returns to source slot", LoadItem(tempDb, InventoryListType.Main, secondCharmSlot) == CharmItemId);
            Check("slot 29 still contains replacement charm", LoadEquippedItem(tempDb, CharmEquipSlot) == CharmItemId);

            Check("unequip to occupied backpack slot is rejected",
                !MoveToEquipment(store, secondCharmSlot, 0, CharmEquipSlot, out var occupiedUnequipResult)
                && occupiedUnequipResult == null);
            Check("rejected unequip keeps equipped charm", LoadEquippedItem(tempDb, CharmEquipSlot) == CharmItemId);
            Check("rejected unequip keeps backpack charm", LoadItem(tempDb, InventoryListType.Main, secondCharmSlot) == CharmItemId);

            Check("charm cannot equip to another slot",
                !MoveToEquipment(store, secondCharmSlot, CharmItemId, 11, out var wrongSlotResult)
                && wrongSlotResult == null);
            Check("rejected charm stays in backpack", LoadItem(tempDb, InventoryListType.Main, secondCharmSlot) == CharmItemId);

            Check("normal equipment cannot equip to slot 29",
                !MoveToEquipment(store, NormalEquipmentSlot, normalItemId, CharmEquipSlot, out var normalToCharmResult)
                && normalToCharmResult == null);
            Check("rejected normal equipment stays in backpack", LoadItem(tempDb, InventoryListType.Main, NormalEquipmentSlot) == normalItemId);
            Check("rejected normal equipment does not replace charm", LoadEquippedItem(tempDb, CharmEquipSlot) == CharmItemId);

            DeleteTempDatabase(tempDb);
            Console.WriteLine($"=== CHARM_EQUIPMENT_SLOT selftest result: pass={_pass}, fail={_fail} ===");
            return _fail == 0 ? 0 : 1;
        }

        private static int ResolveNormalEquipmentItemId()
        {
            for (byte job = 0; job < 16; job++)
            {
                var equipment = InitialCharacterEquipment.Get(job);
                if (equipment == null)
                    continue;
                foreach (var entry in equipment)
                {
                    if (entry.itemId > 0
                        && EquipmentTypeInfo.ParseOrUnknown(ItemMetadataResolver.ResolveEquipmentType(entry.itemId)) != EquipmentType.Charm)
                        return entry.itemId;
                }
            }
            return 0;
        }

        private static bool MoveToEquipment(
            SqliteInventoryStore store,
            short sourceSlot,
            int itemTemplateId,
            short destinationSlot,
            out InventoryMoveResult result)
        {
            return store.TryMoveItem(CharacterId, AccountId, new InventoryMoveRequest
            {
                SourceListType = InventoryListType.Main,
                SourceSlotIndex = sourceSlot,
                MoveCount = 1,
                SourceInstanceValue = itemTemplateId,
                DestinationListType = InventoryListType.Equipment,
                DestinationSlotIndex = destinationSlot,
            }, out result);
        }

        private static bool MoveToMain(
            SqliteInventoryStore store,
            InventoryListType sourceListType,
            short sourceSlot,
            short destinationSlot,
            out InventoryMoveResult result)
        {
            return store.TryMoveItem(CharacterId, AccountId, new InventoryMoveRequest
            {
                SourceListType = sourceListType,
                SourceSlotIndex = sourceSlot,
                MoveCount = 1,
                DestinationListType = InventoryListType.Main,
                DestinationSlotIndex = destinationSlot,
            }, out result);
        }

        private static void Seed(string databasePath, int normalItemId)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'charm-slot-selftest', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'charm-slot-selftest');
INSERT OR IGNORE INTO character_subtype1_fields (character_id)
VALUES (@characterId);
INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES
    ('character', @characterId, @characterId, 0, @firstCharmSlot, @charmItemId, 'equipment',
     100001, @charmItemId, 0, 0, 0, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 2, @warehouseCharmSlot, @charmItemId, 'equipment',
     100002, @charmItemId, 0, 0, 0, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 0, @normalEquipmentSlot, @normalItemId, 'equipment',
     100003, @normalItemId, 1, 0, 0, 0, -1, 0, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@firstCharmSlot", FirstCharmSlot);
                    command.Parameters.AddWithValue("@warehouseCharmSlot", WarehouseCharmSlot);
                    command.Parameters.AddWithValue("@normalEquipmentSlot", NormalEquipmentSlot);
                    command.Parameters.AddWithValue("@charmItemId", CharmItemId);
                    command.Parameters.AddWithValue("@normalItemId", normalItemId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int CountItems(string databasePath, InventoryListType listType, int itemTemplateId)
            => ExecuteScalar(databasePath,
                "SELECT COUNT(1) FROM character_items WHERE character_id=@characterId AND list_type=@listType AND item_template_id=@value;",
                itemTemplateId,
                listType);

        private static int LoadItem(string databasePath, InventoryListType listType, short slot)
            => ExecuteScalar(databasePath,
                "SELECT COALESCE(MAX(item_template_id), 0) FROM character_items WHERE character_id=@characterId AND list_type=@listType AND slot_index=@value;",
                slot,
                listType);

        private static int LoadEquippedItem(string databasePath, short slot)
            => ExecuteScalar(databasePath,
                "SELECT COALESCE(MAX(item_id), 0) FROM character_equipped_entries WHERE character_id=@characterId AND slot=@value;",
                slot,
                null);

        private static void SetEquippedExpireTime(string databasePath, short slot, int expireTime)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE character_equipped_entries SET expire_time=@expireTime WHERE character_id=@characterId AND slot=@slot;";
                    command.Parameters.AddWithValue("@expireTime", expireTime);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slot", slot);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int ExecuteScalar(string databasePath, string sql, int value, InventoryListType? listType)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@value", value);
                    if (listType.HasValue)
                        command.Parameters.AddWithValue("@listType", (int)listType.Value);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static void DeleteTempDatabase(string path)
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                try { if (File.Exists(candidate)) File.Delete(candidate); }
                catch { }
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++; else _fail++;
        }
    }
}
