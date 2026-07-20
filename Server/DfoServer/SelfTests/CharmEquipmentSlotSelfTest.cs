using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text;

namespace DfoServer.SelfTests
{
    public static class CharmEquipmentSlotSelfTest
    {
        private const int AccountId = 966001;
        private const int CharacterId = 966002;
        private const int CharmItemId = 400360000;
        private const int ReplacementCharmItemId = 400360001;
        private const int ThirdCharmItemId = 400360002;
        private const int ElfCharmGiftBoxItemTemplateId = 10004007;
        private static readonly int[] ElfCharmRewardItemTemplateIds = { CharmItemId, ReplacementCharmItemId, ThirdCharmItemId };
        private const short FirstCharmQuickSlot = 3;
        private const short EmptyQuickSlot = 4;
        private const short ReplacementCharmBagSlot = 9;
        private const short WarehouseCharmSlot = 9;
        private const short WarehouseCharmBagDestinationSlot = 12;
        private const short ElfCharmGiftBoxSlot = 84;
        private const short SecondElfCharmGiftBoxSlot = 85;
        private const short NormalEquipmentSlot = 11;
        private const short UnsealedEquipmentReturnSlot = 64;
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

            var noticeBody = ServerNoticeMessageBuilder.Build(InventoryHandler.CharmQuickSlotLimitNoticeMessage);
            Check("charm quick-slot limit notice uses system-message UTF-8 dstr",
                noticeBody.Length > 5
                && noticeBody[0] == 0
                && BitConverter.ToInt32(noticeBody, 1) == noticeBody.Length - 5
                && Encoding.UTF8.GetString(noticeBody, 5, noticeBody.Length - 5)
                    == InventoryHandler.CharmQuickSlotLimitNoticeMessage);

            var tempDb = Path.Combine(Path.GetTempPath(), "dfo-charm-equipment-slot-selftest.db");
            DeleteTempDatabase(tempDb);
            Seed(tempDb, normalItemId);
            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);

            Check("main inventory starts with one quick-slot charm",
                LoadItem(tempDb, InventoryListType.Main, FirstCharmQuickSlot) == CharmItemId);
            Check("main backpack starts with another charm",
                LoadItem(tempDb, InventoryListType.Main, ReplacementCharmBagSlot) == ReplacementCharmItemId);
            Check("second charm pickup is allowed in backpack",
                store.TryPickupItem(CharacterId, AccountId, CharmItemId, 1, out var pickedCharmSlot)
                && pickedCharmSlot > SqliteInventoryStore.QuickSlotEnd);
            Check("cargo charm can enter backpack while quick slot has charm",
                MoveToMain(store, InventoryListType.PersonalCargo, WarehouseCharmSlot, WarehouseCharmBagDestinationSlot, out _));
            Check("moved cargo charm exists in backpack",
                LoadItem(tempDb, InventoryListType.Main, WarehouseCharmBagDestinationSlot) == ThirdCharmItemId);

            var charmCountsBeforeGiftBoxes = new int[ElfCharmRewardItemTemplateIds.Length];
            var snapshotBeforeGiftBoxes = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
            for (var i = 0; i < ElfCharmRewardItemTemplateIds.Length; i++)
                charmCountsBeforeGiftBoxes[i] = snapshotBeforeGiftBoxes.MainItems.FindAll(
                    item => item.ItemTemplateId == ElfCharmRewardItemTemplateIds[i]).Count;

            Check("elf charm gift box grants three different charms", store.TryUseBoosterItem(
                CharacterId,
                AccountId,
                new BoosterUseRequest
                {
                    SlotIndex = ElfCharmGiftBoxSlot,
                    ExpectedItemTemplateId = ElfCharmGiftBoxItemTemplateId,
                    SelectedItemTemplateIds = Array.Empty<int>(),
                },
                out var firstGiftBoxResult)
                && firstGiftBoxResult?.Rewards.Count == ElfCharmRewardItemTemplateIds.Length);
            Check("second elf charm gift box can grant duplicate backpack charms", store.TryUseBoosterItem(
                CharacterId,
                AccountId,
                new BoosterUseRequest
                {
                    SlotIndex = SecondElfCharmGiftBoxSlot,
                    ExpectedItemTemplateId = ElfCharmGiftBoxItemTemplateId,
                    SelectedItemTemplateIds = Array.Empty<int>(),
                },
                out var secondGiftBoxResult)
                && secondGiftBoxResult?.Rewards.Count == ElfCharmRewardItemTemplateIds.Length);
            var snapshotAfterGiftBoxes = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
            for (var i = 0; i < ElfCharmRewardItemTemplateIds.Length; i++)
            {
                var rewardItemTemplateId = ElfCharmRewardItemTemplateIds[i];
                Check($"elf charm gift boxes keep duplicate charm {rewardItemTemplateId} in backpack",
                    snapshotAfterGiftBoxes.MainItems.FindAll(item => item.ItemTemplateId == rewardItemTemplateId).Count
                    == charmCountsBeforeGiftBoxes[i] + 2);
            }

            Check("second charm cannot enter another quick slot",
                !MoveToMain(store, InventoryListType.Main, ReplacementCharmBagSlot, EmptyQuickSlot, out var quickLimitResult)
                && quickLimitResult?.FailureReason == InventoryMoveFailureReason.CharmCarryLimit);
            Check("rejected quick-slot move keeps both charms",
                LoadItem(tempDb, InventoryListType.Main, FirstCharmQuickSlot) == CharmItemId
                && LoadItem(tempDb, InventoryListType.Main, ReplacementCharmBagSlot) == ReplacementCharmItemId);
            Check("backpack charm can replace the quick-slot charm",
                MoveToMain(store, InventoryListType.Main, ReplacementCharmBagSlot, FirstCharmQuickSlot, out var quickReplaceResult)
                && quickReplaceResult != null && quickReplaceResult.Mutated);
            Check("replaced quick-slot charm returns to source backpack slot",
                LoadItem(tempDb, InventoryListType.Main, FirstCharmQuickSlot) == ReplacementCharmItemId
                && LoadItem(tempDb, InventoryListType.Main, ReplacementCharmBagSlot) == CharmItemId);

            Check("charm equips to slot 29",
                MoveToEquipment(store, FirstCharmQuickSlot, ReplacementCharmItemId, CharmEquipSlot, out var firstResult)
                && firstResult != null && firstResult.Mutated);
            Check("slot 29 contains charm", LoadEquippedItem(tempDb, CharmEquipSlot) == ReplacementCharmItemId);
            SetEquippedExpireTime(tempDb, CharmEquipSlot, -1);
            Check("legacy -1 permanent equipment remains in subtype1",
                new SqliteSubtype1Repository(tempDb, ServerPaths.SchemaFilePath)
                    .Load(CharacterId).EquippedEntries.Exists(entry => entry.Slot == CharmEquipSlot));
            SetEquippedExpireTime(tempDb, CharmEquipSlot, 1);
            Check("positive expired equipment is excluded from subtype1",
                !new SqliteSubtype1Repository(tempDb, ServerPaths.SchemaFilePath)
                    .Load(CharacterId).EquippedEntries.Exists(entry => entry.Slot == CharmEquipSlot));
            SetEquippedExpireTime(tempDb, CharmEquipSlot, 0);

            Check("backpack charm can enter empty quick slot after previous charm is equipped",
                MoveToMain(store, InventoryListType.Main, ReplacementCharmBagSlot, FirstCharmQuickSlot, out _));
            Check("quick slot contains the moved charm",
                LoadItem(tempDb, InventoryListType.Main, FirstCharmQuickSlot) == CharmItemId);
            Check("another backpack charm remains blocked from a second quick slot",
                !MoveToMain(store, InventoryListType.Main, WarehouseCharmBagDestinationSlot, EmptyQuickSlot, out _));

            Check("second charm replaces slot 29",
                MoveToEquipment(store, FirstCharmQuickSlot, CharmItemId, CharmEquipSlot, out var replaceResult)
                && replaceResult != null && replaceResult.Mutated);
            Check("replaced charm returns to source slot",
                LoadItem(tempDb, InventoryListType.Main, FirstCharmQuickSlot) == ReplacementCharmItemId);
            Check("slot 29 still contains replacement charm", LoadEquippedItem(tempDb, CharmEquipSlot) == CharmItemId);

            Check("unequip to occupied backpack slot is rejected",
                !MoveToEquipment(store, FirstCharmQuickSlot, 0, CharmEquipSlot, out var occupiedUnequipResult)
                && occupiedUnequipResult == null);
            Check("rejected unequip keeps equipped charm", LoadEquippedItem(tempDb, CharmEquipSlot) == CharmItemId);
            Check("rejected unequip keeps backpack charm",
                LoadItem(tempDb, InventoryListType.Main, FirstCharmQuickSlot) == ReplacementCharmItemId);

            Check("charm cannot equip to another slot",
                !MoveToEquipment(store, FirstCharmQuickSlot, ReplacementCharmItemId, 11, out var wrongSlotResult)
                && wrongSlotResult == null);
            Check("rejected charm stays in quick slot",
                LoadItem(tempDb, InventoryListType.Main, FirstCharmQuickSlot) == ReplacementCharmItemId);

            Check("normal equipment cannot equip to slot 29",
                !MoveToEquipment(store, NormalEquipmentSlot, normalItemId, CharmEquipSlot, out var normalToCharmResult)
                && normalToCharmResult == null);
            Check("rejected normal equipment stays in backpack", LoadItem(tempDb, InventoryListType.Main, NormalEquipmentSlot) == normalItemId);
            Check("rejected normal equipment does not replace charm", LoadEquippedItem(tempDb, CharmEquipSlot) == CharmItemId);

            Check("sealed normal equipment equips to its slot",
                MoveToEquipment(store, NormalEquipmentSlot, normalItemId, NormalEquipmentSlot, out var normalEquipResult)
                && normalEquipResult != null && normalEquipResult.Mutated);
            Check("first equip clears seal flag in equipped entry",
                LoadEquippedSealFlag(tempDb, NormalEquipmentSlot) == 0);
            Check("unsealed equipment returns to backpack",
                MoveToEquipment(store, UnsealedEquipmentReturnSlot, 0, NormalEquipmentSlot, out var normalUnequipResult)
                && normalUnequipResult != null && normalUnequipResult.Mutated);
            Check("unequip persists cleared seal flag",
                LoadItem(tempDb, InventoryListType.Main, UnsealedEquipmentReturnSlot) == normalItemId
                && LoadSealFlag(tempDb, InventoryListType.Main, UnsealedEquipmentReturnSlot) == 0);

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
    ('character', @characterId, @characterId, 0, @firstCharmQuickSlot, @charmItemId, 'equipment',
     100001, @charmItemId, 0, 0, 0, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 0, @replacementCharmBagSlot, @replacementCharmItemId, 'equipment',
     100002, @replacementCharmItemId, 0, 0, 0, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 2, @warehouseCharmSlot, @thirdCharmItemId, 'equipment',
     100003, @thirdCharmItemId, 0, 0, 0, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 0, @normalEquipmentSlot, @normalItemId, 'equipment',
     100004, @normalItemId, 1, 1, 0, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 0, @elfCharmGiftBoxSlot, @elfCharmGiftBoxItemTemplateId, 'special',
     1, 1, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @secondElfCharmGiftBoxSlot, @elfCharmGiftBoxItemTemplateId, 'special',
     1, 1, 0, 0, 0, 0, 0, 0, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@firstCharmQuickSlot", FirstCharmQuickSlot);
                    command.Parameters.AddWithValue("@replacementCharmBagSlot", ReplacementCharmBagSlot);
                    command.Parameters.AddWithValue("@warehouseCharmSlot", WarehouseCharmSlot);
                    command.Parameters.AddWithValue("@normalEquipmentSlot", NormalEquipmentSlot);
                    command.Parameters.AddWithValue("@elfCharmGiftBoxSlot", ElfCharmGiftBoxSlot);
                    command.Parameters.AddWithValue("@secondElfCharmGiftBoxSlot", SecondElfCharmGiftBoxSlot);
                    command.Parameters.AddWithValue("@charmItemId", CharmItemId);
                    command.Parameters.AddWithValue("@replacementCharmItemId", ReplacementCharmItemId);
                    command.Parameters.AddWithValue("@thirdCharmItemId", ThirdCharmItemId);
                    command.Parameters.AddWithValue("@normalItemId", normalItemId);
                    command.Parameters.AddWithValue("@elfCharmGiftBoxItemTemplateId", ElfCharmGiftBoxItemTemplateId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int LoadItem(string databasePath, InventoryListType listType, short slot)
            => ExecuteScalar(databasePath,
                "SELECT COALESCE(MAX(item_template_id), 0) FROM character_items WHERE character_id=@characterId AND list_type=@listType AND slot_index=@value;",
                slot,
                listType);

        private static int LoadSealFlag(string databasePath, InventoryListType listType, short slot)
            => ExecuteScalar(databasePath,
                "SELECT COALESCE(MAX(seal_flag), -1) FROM character_items WHERE character_id=@characterId AND list_type=@listType AND slot_index=@value;",
                slot,
                listType);

        private static int LoadEquippedSealFlag(string databasePath, short slot)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT raw_entry FROM character_equipped_entries WHERE character_id=@characterId AND slot=@slot LIMIT 1;";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slot", slot);
                    var raw = command.ExecuteScalar() as byte[];
                    return raw == null ? -1 : MakeEquipListCodec.ParseDisplayFields(raw).SealFlag;
                }
            }
        }

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
