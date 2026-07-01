using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class PetConsumableSelfTest
    {
        private const int AccountId = 163001;
        private const int CharacterId = 163001;
        private const int NoActiveCharacterId = 163002;
        private const int EquippedActiveCharacterId = 163003;
        private const int MoveEquippedCharacterId = 163004;
        private const short PetFoodSlot = 189;
        private const short RenameCardSlot = 190;
        private const short MovePetSlot = 35;
        private const int PetFoodItemTemplateId = 24;
        private const int RenameCardItemTemplateId = 25;
        private const int EquippedPetItemTemplateId = 100330649;
        private const int MovePetItemTemplateId = 400990054;
        private const int InitialPetFoodCount = 999;
        private const int InitialRenameCardCount = 5;
        private const int PetCreatureKey = 1;
        private const int OtherPetCreatureKey = 2;
        private const int NoActiveFirstPetCreatureKey = 10;
        private const int NoActiveSecondPetCreatureKey = 11;
        private const int EquippedActivePetCreatureKey = 153;
        private const int EquippedInactivePetCreatureKey = 154;
        private const int MovePetCreatureKey = 36;
        private const int InitialPetSatiety = 40;
        private const int InitialOtherPetSatiety = 10;
        private const int InitialPetVisibleSatiety = 5;
        private const int InitialOtherPetVisibleSatiety = 7;
        private const int InitialPetProgressValue = 1234;
        private const int PetFoodSatietyDelta = 30;

        public static int Run()
        {
            Console.WriteLine("=== PET_CONSUMABLE selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "pet-consumable.db");
            DeleteTempDatabase(dbPath);

            var store = new SqliteInventoryStore(dbPath, ServerPaths.SchemaFilePath);
            SeedPetConsumable(dbPath);

            var failures = 0;
            Check("cera-shop 1000-count stack is not truncated to 999",
                InventoryShopStore.NormalizeCeraShopEffectiveStackCount(1, 1000, 1000) == 1000,
                ref failures);
            Check("cera-shop product count is honored when PVF stack limit is missing",
                InventoryShopStore.NormalizeCeraShopEffectiveStackCount(1, 1000, 0) == 1000,
                ref failures);
            Check("cera-shop missing PVF stack limit stays uncapped",
                InventoryShopStore.ResolveCeraShopStackLimit(1, 0) == 0,
                ref failures);
            Check("cera-shop missing PVF stack limit allows repeated stacks past 999",
                InventoryShopStore.NormalizeCeraShopEffectiveStackCount(1000, 1, 0) == 1000,
                ref failures);
            CheckUseStackableProtocolPlan(ref failures);
            CheckCreatureStateProtocolBody(ref failures);

            using (store.BeginScope(CharacterId, AccountId))
            {
                Check("using one pet consumable succeeds",
                    store.TryDeleteItem(InventoryListType.Pet, PetFoodSlot, 1, out var result),
                    ref failures);
                if (result != null)
                {
                    Check("pet consumable result remains in pet list", result.ListType == InventoryListType.Pet, ref failures);
                    Check("pet consumable result keeps source slot", result.SlotIndex == PetFoodSlot, ref failures);
                    Check("pet consumable remaining count is decremented", result.RemainingStackCount == InitialPetFoodCount - 1, ref failures);
                    Check("pet consumable instance mirrors remaining count", result.InstanceValue == InitialPetFoodCount - 1, ref failures);
                    Check("pet consumable applied count is one", result.AppliedCount == 1, ref failures);
                }
            }

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                connection.Open();
                var row = LoadPetConsumableRow(connection);
                Check("pet consumable row still exists after single use", row.Exists, ref failures);
                if (row.Exists)
                {
                    Check("pet consumable stack_count decremented", row.StackCount == InitialPetFoodCount - 1, ref failures);
                    Check("pet consumable instance_value decremented", row.InstanceValue == InitialPetFoodCount - 1, ref failures);
                    Check("pet consumable pet_serial_or_handle decremented", row.PetSerialOrHandle == InitialPetFoodCount - 1, ref failures);
                }

                Check("pet food raises creature satiety by PVF feed amount",
                    LoadCreatureSatiety(connection) == InitialPetSatiety + PetFoodSatietyDelta,
                    ref failures);
                Check("pet food raises creature visible satiety by PVF feed amount",
                    LoadCreatureVisibleSatiety(connection) == InitialPetSatiety + PetFoodSatietyDelta,
                    ref failures);
                Check("pet food does not raise inactive creature satiety",
                    LoadCreatureSatiety(connection, OtherPetCreatureKey) == InitialOtherPetSatiety,
                    ref failures);
                Check("pet food does not raise inactive creature visible satiety",
                    LoadCreatureVisibleSatiety(connection, OtherPetCreatureKey) == InitialOtherPetVisibleSatiety,
                    ref failures);
                Check("pet food does not change creature progress value",
                    LoadCreatureProgressValue(connection) == InitialPetProgressValue,
                    ref failures);
            }

            using (store.BeginScope(CharacterId, AccountId))
            {
                Check("non-feed pet consumable succeeds",
                    store.TryDeleteItem(InventoryListType.Pet, RenameCardSlot, 1, out var renameResult),
                    ref failures);
                if (renameResult != null)
                    Check("non-feed pet consumable decrements",
                        renameResult.RemainingStackCount == InitialRenameCardCount - 1,
                        ref failures);
            }

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                connection.Open();
                Check("non-feed pet consumable does not raise creature satiety",
                    LoadCreatureSatiety(connection) == InitialPetSatiety + PetFoodSatietyDelta,
                    ref failures);
                Check("non-feed pet consumable does not raise creature visible satiety",
                    LoadCreatureVisibleSatiety(connection) == InitialPetSatiety + PetFoodSatietyDelta,
                    ref failures);
                Check("non-feed pet consumable does not change creature progress value",
                    LoadCreatureProgressValue(connection) == InitialPetProgressValue,
                    ref failures);
            }

            using (store.BeginScope(CharacterId, AccountId))
            {
                var snapshot = store.LoadCharacterItemListSnapshot();
                var petItem = snapshot.PetItems.FirstOrDefault(x => x.SlotIndex == PetFoodSlot);
                Check("pet consumable snapshot still has slot", petItem != null, ref failures);
                if (petItem != null)
                {
                    Check("pet consumable snapshot serial mirrors count",
                        petItem.CreatureSerialOrHandle == InitialPetFoodCount - 1,
                        ref failures);
                }

                Check("second pet consumable use succeeds",
                    store.TryDeleteItem(InventoryListType.Pet, PetFoodSlot, 1, out var secondResult),
                    ref failures);
                if (secondResult != null)
                    Check("second pet consumable use decrements again",
                        secondResult.RemainingStackCount == InitialPetFoodCount - 2,
                        ref failures);
            }

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                connection.Open();
                Check("second pet food use clamps creature satiety to 100",
                    LoadCreatureSatiety(connection) == 100,
                    ref failures);
                Check("second pet food use clamps creature visible satiety to 100",
                    LoadCreatureVisibleSatiety(connection) == 100,
                    ref failures);
                Check("second pet food use still leaves inactive creature satiety unchanged",
                    LoadCreatureSatiety(connection, OtherPetCreatureKey) == InitialOtherPetSatiety,
                    ref failures);
                Check("second pet food use still leaves inactive creature visible satiety unchanged",
                    LoadCreatureVisibleSatiety(connection, OtherPetCreatureKey) == InitialOtherPetVisibleSatiety,
                    ref failures);
                Check("second pet food use does not change creature progress value",
                    LoadCreatureProgressValue(connection) == InitialPetProgressValue,
                    ref failures);
            }

            SetCreatureVisibleSatiety(dbPath, PetCreatureKey, 42);
            using (store.BeginScope(CharacterId, AccountId))
            {
                Check("third pet consumable use at full active satiety succeeds",
                    store.TryDeleteItem(InventoryListType.Pet, PetFoodSlot, 1, out var thirdResult),
                    ref failures);
                if (thirdResult != null)
                {
                    Check("third pet consumable use decrements again",
                        thirdResult.RemainingStackCount == InitialPetFoodCount - 3,
                        ref failures);
                    Check("third pet consumable syncs stale visible satiety",
                        thirdResult.PetSatietyChanged && thirdResult.PetSatietyBefore == 100 && thirdResult.PetSatietyAfter == 100,
                        ref failures);
                }
            }

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                connection.Open();
                Check("full active pet food use keeps active creature satiety at 100",
                    LoadCreatureSatiety(connection) == 100,
                    ref failures);
                Check("full active pet food use keeps active creature visible satiety at 100",
                    LoadCreatureVisibleSatiety(connection) == 100,
                    ref failures);
                Check("full active pet food use does not feed inactive creature",
                    LoadCreatureSatiety(connection, OtherPetCreatureKey) == InitialOtherPetSatiety,
                    ref failures);
                Check("full active pet food use does not change inactive creature visible satiety",
                    LoadCreatureVisibleSatiety(connection, OtherPetCreatureKey) == InitialOtherPetVisibleSatiety,
                    ref failures);
            }

            SeedNoActivePetConsumable(dbPath);
            using (store.BeginScope(NoActiveCharacterId, AccountId))
            {
                Check("no-active pet consumable succeeds",
                    store.TryDeleteItem(InventoryListType.Pet, PetFoodSlot, 1, out var noActiveResult),
                    ref failures);
                if (noActiveResult != null)
                    Check("no-active pet consumable does not report satiety sync",
                        !noActiveResult.PetSatietyChanged,
                        ref failures);
            }

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                connection.Open();
                Check("no-active pet food does not raise first creature satiety",
                    LoadCreatureSatiety(connection, NoActiveCharacterId, NoActiveFirstPetCreatureKey) == InitialPetSatiety,
                    ref failures);
                Check("no-active pet food does not raise first creature visible satiety",
                    LoadCreatureVisibleSatiety(connection, NoActiveCharacterId, NoActiveFirstPetCreatureKey) == InitialPetVisibleSatiety,
                    ref failures);
                Check("no-active pet food does not raise second creature satiety",
                    LoadCreatureSatiety(connection, NoActiveCharacterId, NoActiveSecondPetCreatureKey) == InitialOtherPetSatiety,
                    ref failures);
                Check("no-active pet food does not raise second creature visible satiety",
                    LoadCreatureVisibleSatiety(connection, NoActiveCharacterId, NoActiveSecondPetCreatureKey) == InitialOtherPetVisibleSatiety,
                    ref failures);
            }

            SeedEquippedActivePetConsumable(dbPath);
            using (store.BeginScope(EquippedActiveCharacterId, AccountId))
            {
                Check("equipped-active pet consumable succeeds",
                    store.TryDeleteItem(InventoryListType.Pet, PetFoodSlot, 1, out var equippedResult),
                    ref failures);
                if (equippedResult != null)
                {
                    Check("equipped-active pet consumable reports satiety sync",
                        equippedResult.PetSatietyChanged,
                        ref failures);
                    Check("equipped-active pet consumable targets equipped creature key",
                        equippedResult.PetCreatureKey == EquippedActivePetCreatureKey,
                        ref failures);
                }
            }

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                connection.Open();
                Check("equipped-active pet food raises equipped creature satiety",
                    LoadCreatureSatiety(connection, EquippedActiveCharacterId, EquippedActivePetCreatureKey) == InitialPetSatiety + PetFoodSatietyDelta,
                    ref failures);
                Check("equipped-active pet food raises equipped creature visible satiety",
                    LoadCreatureVisibleSatiety(connection, EquippedActiveCharacterId, EquippedActivePetCreatureKey) == InitialPetSatiety + PetFoodSatietyDelta,
                    ref failures);
                Check("equipped-active pet food does not raise inactive equipped-list creature satiety",
                    LoadCreatureSatiety(connection, EquippedActiveCharacterId, EquippedInactivePetCreatureKey) == InitialOtherPetSatiety,
                    ref failures);
                Check("equipped-active pet food does not raise inactive equipped-list creature visible satiety",
                    LoadCreatureVisibleSatiety(connection, EquippedActiveCharacterId, EquippedInactivePetCreatureKey) == InitialOtherPetVisibleSatiety,
                    ref failures);
            }

            SeedMoveEquippedPetConsumable(dbPath);
            using (store.BeginScope(MoveEquippedCharacterId, AccountId))
            {
                var moveRequest = new InventoryMoveRequest
                {
                    SourceListType = InventoryListType.Pet,
                    SourceSlotIndex = MovePetSlot,
                    SourceInstanceValue = MovePetItemTemplateId,
                    MoveCount = 1,
                    DestinationListType = InventoryListType.Equipment,
                    DestinationSlotIndex = 24,
                    DestinationInstanceValue = EquippedPetItemTemplateId,
                };

                Check("pet creature equipment move succeeds",
                    store.TryMoveItem(moveRequest, out var moveResult),
                    ref failures);
                if (moveResult != null)
                {
                    Check("pet creature equipment move mutates equipped slot", moveResult.Mutated, ref failures);
                    Check("pet creature equipment move is not acked as reverse error", !moveResult.AckError, ref failures);
                }

                Check("moved-equipped pet consumable succeeds",
                    store.TryDeleteItem(InventoryListType.Pet, PetFoodSlot, 1, out var movedFeedResult),
                    ref failures);
                if (movedFeedResult != null)
                {
                    Check("moved-equipped pet consumable reports satiety sync",
                        movedFeedResult.PetSatietyChanged,
                        ref failures);
                    Check("moved-equipped pet consumable targets newly equipped creature key",
                        movedFeedResult.PetCreatureKey == MovePetCreatureKey,
                        ref failures);
                }
            }

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                connection.Open();
                Check("pet equipment move records new equipped creature key",
                    LoadEquippedCreatureKey(connection, MoveEquippedCharacterId) == MovePetCreatureKey,
                    ref failures);
                Check("pet equipment move swaps previous equipped creature back to source slot",
                    LoadPetItemSerialOrHandle(connection, MoveEquippedCharacterId, MovePetSlot) == EquippedActivePetCreatureKey,
                    ref failures);
                Check("moved-equipped pet food raises newly equipped creature satiety",
                    LoadCreatureSatiety(connection, MoveEquippedCharacterId, MovePetCreatureKey) == InitialPetSatiety + PetFoodSatietyDelta,
                    ref failures);
                Check("moved-equipped pet food raises newly equipped creature visible satiety",
                    LoadCreatureVisibleSatiety(connection, MoveEquippedCharacterId, MovePetCreatureKey) == InitialPetSatiety + PetFoodSatietyDelta,
                    ref failures);
                Check("moved-equipped pet food does not feed previously equipped creature",
                    LoadCreatureSatiety(connection, MoveEquippedCharacterId, EquippedActivePetCreatureKey) == 100,
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckUseStackableProtocolPlan(ref int failures)
        {
            var consumedPetPlan = InventoryHandler.BuildUseStackableResponsePlan(
                consumed: true,
                result: new InventoryMutationResult
                {
                    ListType = InventoryListType.Pet,
                    SlotIndex = PetFoodSlot,
                    ItemTemplateId = PetFoodItemTemplateId,
                    RemainingStackCount = 0,
                    InstanceValue = 0,
                },
                listType: InventoryListType.Pet,
                slotIndex: PetFoodSlot,
                instanceValue: 1,
                itemCode: PetFoodItemTemplateId);
            Check("pet use success ACK uses 0x002C success", consumedPetPlan.AckBody.Length > 0 && consumedPetPlan.AckBody[0] == 0x01, ref failures);
            Check("pet use success does not send pet item-list update", consumedPetPlan.ItemListUpdateBody == null, ref failures);

            var stalePetPlan = InventoryHandler.BuildUseStackableResponsePlan(
                consumed: false,
                result: null,
                listType: InventoryListType.Pet,
                slotIndex: PetFoodSlot,
                instanceValue: 0,
                itemCode: PetFoodItemTemplateId);
            Check("stale pet use is acknowledged as success", stalePetPlan.AckBody.Length > 0 && stalePetPlan.AckBody[0] == 0x01, ref failures);
            Check("stale pet use does not send pet item-list update", stalePetPlan.ItemListUpdateBody == null, ref failures);

            var mainFailurePlan = InventoryHandler.BuildUseStackableResponsePlan(
                consumed: false,
                result: null,
                listType: InventoryListType.Main,
                slotIndex: PetFoodSlot,
                instanceValue: 0,
                itemCode: PetFoodItemTemplateId);
            Check("non-pet use failure still uses error ACK", mainFailurePlan.AckBody.Length > 0 && mainFailurePlan.AckBody[0] == 0x00, ref failures);
        }

        private static void CheckCreatureStateProtocolBody(ref int failures)
        {
            var entry = new CreatureItemEntrySnapshot
            {
                CreatureKey = 153,
                Field04 = 61,
                ModeFlag = 0,
                ProgressValue32 = 3294,
                FieldAfterValue32 = 61,
                CreatureTextBytes = new byte[0],
                TailFlag = 0x07,
            };

            var stateBody = CreatureListBodyBuilder.BuildCreatureStateBody(entry);
            Check("creature state refresh body omits creature-list count",
                stateBody.Length == 16 && BitConverter.ToInt32(stateBody, 0) == entry.CreatureKey,
                ref failures);
            Check("creature state refresh body carries satiety fields",
                stateBody[4] == entry.Field04 && stateBody[10] == entry.FieldAfterValue32,
                ref failures);

            var snapshot = new SelectCharacterDataSnapshot();
            snapshot.InitializationSnapshot.CreatureItemList.Entries.Add(entry);
            var hasListBody = new CreatureListBodyBuilder().TryBuild(snapshot, 0, out var listBody);
            Check("creature item-list body keeps leading count",
                hasListBody && listBody.Length == stateBody.Length + 1 && listBody[0] == 1,
                ref failures);
        }

        private static void SeedPetConsumable(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'pet-consumable-selftest', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'pet-consumable-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 7, 0);

INSERT OR REPLACE INTO character_subtype0_fields (character_id, creature_buffer)
VALUES (@characterId, @activeCreatureBuffer);

INSERT OR REPLACE INTO character_creatures (
    character_id, sort_order, creature_key, field04, mode_flag, progress_value,
    mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag)
VALUES (
    @characterId, 0, @creatureKey, @initialPetSatiety, 0, @initialPetProgressValue,
    0, 0, @initialPetVisibleSatiety, NULL, 0);

INSERT OR REPLACE INTO character_creatures (
    character_id, sort_order, creature_key, field04, mode_flag, progress_value,
    mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag)
VALUES (
    @characterId, 1, @otherCreatureKey, @initialOtherPetSatiety, 0, 0,
    0, 0, @initialOtherPetVisibleSatiety, NULL, 0);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 7, @slotIndex, @itemTemplateId, 'pet',
    @stackCount, @stackCount, 0, 0, 0, 0, 0,
    @stackCount, '{}');

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 7, @renameCardSlot, @renameCardItemTemplateId, 'pet',
    @renameCardCount, @renameCardCount, 0, 0, 0, 0, 0,
    @renameCardCount, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slotIndex", PetFoodSlot);
                    command.Parameters.AddWithValue("@itemTemplateId", PetFoodItemTemplateId);
                    command.Parameters.AddWithValue("@stackCount", InitialPetFoodCount);
                    command.Parameters.AddWithValue("@renameCardSlot", RenameCardSlot);
                    command.Parameters.AddWithValue("@renameCardItemTemplateId", RenameCardItemTemplateId);
                    command.Parameters.AddWithValue("@renameCardCount", InitialRenameCardCount);
                    command.Parameters.AddWithValue("@creatureKey", PetCreatureKey);
                    command.Parameters.AddWithValue("@otherCreatureKey", OtherPetCreatureKey);
                    command.Parameters.AddWithValue("@initialPetSatiety", InitialPetSatiety);
                    command.Parameters.AddWithValue("@initialOtherPetSatiety", InitialOtherPetSatiety);
                    command.Parameters.AddWithValue("@initialPetVisibleSatiety", InitialPetVisibleSatiety);
                    command.Parameters.AddWithValue("@initialOtherPetVisibleSatiety", InitialOtherPetVisibleSatiety);
                    command.Parameters.AddWithValue("@initialPetProgressValue", InitialPetProgressValue);
                    command.Parameters.AddWithValue("@activeCreatureBuffer", BuildActiveCreatureBuffer(PetCreatureKey));
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SeedNoActivePetConsumable(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'no-active-pet-consumable-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 7, 0);

INSERT OR REPLACE INTO character_subtype0_fields (character_id, creature_buffer)
VALUES (@characterId, @emptyCreatureBuffer);

INSERT OR REPLACE INTO character_creatures (
    character_id, sort_order, creature_key, field04, mode_flag, progress_value,
    mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag)
VALUES (
    @characterId, 0, @firstCreatureKey, @initialPetSatiety, 0, 0,
    0, 0, @initialPetVisibleSatiety, NULL, 0);

INSERT OR REPLACE INTO character_creatures (
    character_id, sort_order, creature_key, field04, mode_flag, progress_value,
    mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag)
VALUES (
    @characterId, 1, @secondCreatureKey, @initialOtherPetSatiety, 0, 0,
    0, 0, @initialOtherPetVisibleSatiety, NULL, 0);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 7, @slotIndex, @itemTemplateId, 'pet',
    @stackCount, @stackCount, 0, 0, 0, 0, 0,
    @stackCount, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", NoActiveCharacterId);
                    command.Parameters.AddWithValue("@emptyCreatureBuffer", new byte[8]);
                    command.Parameters.AddWithValue("@firstCreatureKey", NoActiveFirstPetCreatureKey);
                    command.Parameters.AddWithValue("@secondCreatureKey", NoActiveSecondPetCreatureKey);
                    command.Parameters.AddWithValue("@initialPetSatiety", InitialPetSatiety);
                    command.Parameters.AddWithValue("@initialOtherPetSatiety", InitialOtherPetSatiety);
                    command.Parameters.AddWithValue("@initialPetVisibleSatiety", InitialPetVisibleSatiety);
                    command.Parameters.AddWithValue("@initialOtherPetVisibleSatiety", InitialOtherPetVisibleSatiety);
                    command.Parameters.AddWithValue("@slotIndex", PetFoodSlot);
                    command.Parameters.AddWithValue("@itemTemplateId", PetFoodItemTemplateId);
                    command.Parameters.AddWithValue("@stackCount", InitialPetFoodCount);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SeedEquippedActivePetConsumable(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'equipped-active-pet-consumable-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 7, 0);

INSERT OR REPLACE INTO character_subtype0_fields (character_id, creature_buffer)
VALUES (@characterId, @emptyCreatureBuffer);

INSERT OR REPLACE INTO character_equipped_entries (character_id, slot, item_id, raw_entry)
VALUES (@characterId, 24, @equippedPetItemTemplateId, @equippedPetRawEntry);

INSERT OR REPLACE INTO character_creatures (
    character_id, sort_order, creature_key, field04, mode_flag, progress_value,
    mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag)
VALUES (
    @characterId, 0, @activeCreatureKey, @initialPetSatiety, 0, 0,
    0, 0, @initialPetVisibleSatiety, NULL, 0);

INSERT OR REPLACE INTO character_creatures (
    character_id, sort_order, creature_key, field04, mode_flag, progress_value,
    mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag)
VALUES (
    @characterId, 1, @inactiveCreatureKey, @initialOtherPetSatiety, 0, 0,
    0, 0, @initialOtherPetVisibleSatiety, NULL, 0);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 7, @slotIndex, @itemTemplateId, 'pet',
    @stackCount, @stackCount, 0, 0, 0, 0, 0,
    @stackCount, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", EquippedActiveCharacterId);
                    command.Parameters.AddWithValue("@emptyCreatureBuffer", new byte[8]);
                    command.Parameters.AddWithValue("@equippedPetItemTemplateId", EquippedPetItemTemplateId);
                    command.Parameters.AddWithValue("@equippedPetRawEntry", BuildEquippedCreatureRaw(EquippedPetItemTemplateId, EquippedActivePetCreatureKey));
                    command.Parameters.AddWithValue("@activeCreatureKey", EquippedActivePetCreatureKey);
                    command.Parameters.AddWithValue("@inactiveCreatureKey", EquippedInactivePetCreatureKey);
                    command.Parameters.AddWithValue("@initialPetSatiety", InitialPetSatiety);
                    command.Parameters.AddWithValue("@initialOtherPetSatiety", InitialOtherPetSatiety);
                    command.Parameters.AddWithValue("@initialPetVisibleSatiety", InitialPetVisibleSatiety);
                    command.Parameters.AddWithValue("@initialOtherPetVisibleSatiety", InitialOtherPetVisibleSatiety);
                    command.Parameters.AddWithValue("@slotIndex", PetFoodSlot);
                    command.Parameters.AddWithValue("@itemTemplateId", PetFoodItemTemplateId);
                    command.Parameters.AddWithValue("@stackCount", InitialPetFoodCount);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SeedMoveEquippedPetConsumable(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'move-equipped-pet-consumable-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 7, 0);

INSERT OR REPLACE INTO character_subtype0_fields (character_id, creature_buffer)
VALUES (@characterId, @emptyCreatureBuffer);

INSERT OR REPLACE INTO character_equipped_entries (character_id, slot, item_id, raw_entry)
VALUES (@characterId, 24, @equippedPetItemTemplateId, @equippedPetRawEntry);

INSERT OR REPLACE INTO character_creatures (
    character_id, sort_order, creature_key, field04, mode_flag, progress_value,
    mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag)
VALUES (
    @characterId, 0, @movePetCreatureKey, @initialPetSatiety, 0, 0,
    0, 0, @initialPetVisibleSatiety, NULL, 0);

INSERT OR REPLACE INTO character_creatures (
    character_id, sort_order, creature_key, field04, mode_flag, progress_value,
    mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag)
VALUES (
    @characterId, 1, @equippedPetCreatureKey, 100, 0, 0,
    0, 0, 100, NULL, 0);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 7, @movePetSlot, @movePetItemTemplateId, 'pet',
    0, 0, 0, 0, 0, 0, 0,
    @movePetCreatureKey, '{}');

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 7, @slotIndex, @itemTemplateId, 'pet',
    @stackCount, @stackCount, 0, 0, 0, 0, 0,
    @stackCount, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", MoveEquippedCharacterId);
                    command.Parameters.AddWithValue("@emptyCreatureBuffer", new byte[8]);
                    command.Parameters.AddWithValue("@equippedPetItemTemplateId", EquippedPetItemTemplateId);
                    command.Parameters.AddWithValue("@equippedPetRawEntry", BuildEquippedCreatureRaw(EquippedPetItemTemplateId, EquippedActivePetCreatureKey));
                    command.Parameters.AddWithValue("@equippedPetCreatureKey", EquippedActivePetCreatureKey);
                    command.Parameters.AddWithValue("@movePetSlot", MovePetSlot);
                    command.Parameters.AddWithValue("@movePetItemTemplateId", MovePetItemTemplateId);
                    command.Parameters.AddWithValue("@movePetCreatureKey", MovePetCreatureKey);
                    command.Parameters.AddWithValue("@initialPetSatiety", InitialPetSatiety);
                    command.Parameters.AddWithValue("@initialPetVisibleSatiety", InitialPetVisibleSatiety);
                    command.Parameters.AddWithValue("@slotIndex", PetFoodSlot);
                    command.Parameters.AddWithValue("@itemTemplateId", PetFoodItemTemplateId);
                    command.Parameters.AddWithValue("@stackCount", InitialPetFoodCount);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int LoadCreatureSatiety(SqliteConnection connection)
        {
            return LoadCreatureSatiety(connection, PetCreatureKey);
        }

        private static int LoadCreatureSatiety(SqliteConnection connection, int creatureKey)
        {
            return LoadCreatureSatiety(connection, CharacterId, creatureKey);
        }

        private static int LoadCreatureSatiety(SqliteConnection connection, int characterId, int creatureKey)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT field04
FROM character_creatures
WHERE character_id = @characterId
  AND creature_key = @creatureKey;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@creatureKey", creatureKey);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static int LoadCreatureVisibleSatiety(SqliteConnection connection)
        {
            return LoadCreatureVisibleSatiety(connection, PetCreatureKey);
        }

        private static int LoadCreatureVisibleSatiety(SqliteConnection connection, int creatureKey)
        {
            return LoadCreatureVisibleSatiety(connection, CharacterId, creatureKey);
        }

        private static int LoadCreatureVisibleSatiety(SqliteConnection connection, int characterId, int creatureKey)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT field_after_value
FROM character_creatures
WHERE character_id = @characterId
  AND creature_key = @creatureKey;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@creatureKey", creatureKey);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static int LoadCreatureProgressValue(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT progress_value
FROM character_creatures
WHERE character_id = @characterId
  AND creature_key = @creatureKey;";
                command.Parameters.AddWithValue("@characterId", CharacterId);
                command.Parameters.AddWithValue("@creatureKey", PetCreatureKey);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static int LoadEquippedCreatureKey(SqliteConnection connection, int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT raw_entry
FROM character_equipped_entries
WHERE character_id = @characterId
  AND slot = 24;";
                command.Parameters.AddWithValue("@characterId", characterId);
                var raw = command.ExecuteScalar() as byte[];
                return raw != null && raw.Length >= 9 ? BitConverter.ToInt32(raw, 5) : 0;
            }
        }

        private static int LoadPetItemSerialOrHandle(SqliteConnection connection, int characterId, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT pet_serial_or_handle
FROM character_items
WHERE character_id = @characterId
  AND list_type = 7
  AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@slotIndex", slotIndex);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static byte[] BuildActiveCreatureBuffer(int creatureKey)
        {
            return new[]
            {
                (byte)(creatureKey & 0xFF),
                (byte)((creatureKey >> 8) & 0xFF),
                (byte)((creatureKey >> 16) & 0xFF),
                (byte)((creatureKey >> 24) & 0xFF),
                (byte)0,
                (byte)0,
                (byte)0,
                (byte)0,
            };
        }

        private static byte[] BuildEquippedCreatureRaw(int itemTemplateId, int creatureKey)
        {
            var raw = new byte[45];
            raw[0] = 24;
            WriteInt32LittleEndian(raw, 1, itemTemplateId);
            WriteInt32LittleEndian(raw, 5, creatureKey);
            return raw;
        }

        private static void WriteInt32LittleEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void SetCreatureVisibleSatiety(string databasePath, int creatureKey, int visibleSatiety)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE character_creatures
SET field_after_value = @visibleSatiety
WHERE character_id = @characterId
  AND creature_key = @creatureKey;";
                    command.Parameters.AddWithValue("@visibleSatiety", visibleSatiety);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@creatureKey", creatureKey);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static (bool Exists, int StackCount, int InstanceValue, int PetSerialOrHandle) LoadPetConsumableRow(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT stack_count, instance_value, pet_serial_or_handle
FROM character_items
WHERE character_id = @characterId
  AND list_type = 7
  AND slot_index = @slotIndex;";
                command.Parameters.AddWithValue("@characterId", CharacterId);
                command.Parameters.AddWithValue("@slotIndex", PetFoodSlot);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return (false, 0, 0, 0);

                    return (true, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
                }
            }
        }

        private static void DeleteTempDatabase(string path)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file))
                    File.Delete(file);
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok)
                failures++;
        }
    }
}
