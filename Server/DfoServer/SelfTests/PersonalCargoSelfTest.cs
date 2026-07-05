using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class PersonalCargoSelfTest
    {
        private const int AccountId = 910390;
        private const int CharacterId = 910391;
        private const int NewCharacterId = 910392;
        private const int CargoUpgradeTicketItemId = 10005815;
        private const int LegacyPersonalCargoUpgradeCommodityNo = 100063;
        private const int LegacyPersonalCargoUpgradeItemId = 50;
        private const int AccountCargoUpgradeCommodityNo = 100534;
        private const int AccountCargoUpgradeToolItemId = 2681921;
        private const int VoidMagicStoneItemId = 3299;
        private const int NonCargoStackableItemId = 1004;
        private const int StartingCera = 5000;
        private const short TicketSlot = 86;
        private const short MaxedTicketSlot = 87;
        private const short NonCargoSlot = 88;
        private const short AccountCargoToolSlot = 89;
        private const short LegacyPersonalCargoToolSlot = 90;
        private const short VoidMagicStoneSlot = 91;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== PERSONAL_CARGO selftest ===");

            var stackable = InventoryDbPrimitives.LoadStackableItem(CargoUpgradeTicketItemId);
            Check("cargo upgrade ticket stackable exists", stackable != null);
            Check("cargo upgrade ticket action type", NormalizeTag(stackable?.ActionTypeName) == "upgrade cargo");
            Check(
                "legacy personal cargo upgrade tool resolves target 24",
                SqliteInventoryStore.TryResolvePersonalCargoUpgradeTarget(LegacyPersonalCargoUpgradeItemId, out var legacyTarget)
                && legacyTarget == 24);
            Check(
                "account cargo tool is not personal cargo",
                !SqliteInventoryStore.TryResolvePersonalCargoUpgradeTarget(AccountCargoUpgradeToolItemId, out _));
            var ceraUpdateBody = CeraUpdateBuilder.Build(1, 2, 3);
            Check(
                "cera update builder encodes balances",
                ceraUpdateBody.Length == 13
                && ceraUpdateBody[0] == 1
                && BitConverter.ToInt32(ceraUpdateBody, 1) == 1
                && BitConverter.ToInt32(ceraUpdateBody, 5) == 2
                && BitConverter.ToInt32(ceraUpdateBody, 9) == 3);

            var tempDb = Path.Combine(Path.GetTempPath(), "personal_cargo_selftest.db");
            DeleteTempDatabase(tempDb);
            Seed(tempDb);

            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);

            store.EnsureContainerState(NewCharacterId, AccountId);
            Check("new character personal cargo defaults to 8", LoadPersonalCargoCapacity(tempDb, NewCharacterId) == 8);

            Check(
                "ticket use is handled",
                store.TryUsePersonalCargoUpgradeTicket(
                    CharacterId,
                    AccountId,
                    InventoryListType.Main,
                    TicketSlot,
                    CargoUpgradeTicketItemId,
                    out var ticketResult));

            Check("ticket use succeeds", ticketResult.Success);
            Check("ticket previous capacity 24", ticketResult.PreviousListParam16 == 24);
            Check("ticket upgrades capacity to 40", ticketResult.NewListParam16 == 40);
            Check("ticket consumed one item", ticketResult.ConsumedItem != null && ticketResult.ConsumedItem.RemainingStackCount == 1);
            Check("database capacity after ticket", LoadPersonalCargoCapacity(tempDb) == 40);
            Check("database ticket stack after ticket", LoadStackCount(tempDb, TicketSlot) == 1);

            Check("direct personal cargo upgrade succeeds", store.TryUpgradePersonalCargo(CharacterId, AccountId, out var directCapacity, out _));
            Check("direct personal cargo upgrade reaches 56", directCapacity == 56 && LoadPersonalCargoCapacity(tempDb) == 56);

            SetPersonalCargoCapacity(tempDb, 0);
            var zeroSnapshot = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
            Check("zero personal cargo snapshot normalizes to 8", zeroSnapshot.PersonalCargoListParam16 == 8);
            Check("zero personal cargo upgrade reaches 24", store.TryUpgradePersonalCargo(CharacterId, AccountId, out var zeroUpgradeCapacity, out _) && zeroUpgradeCapacity == 24);
            Check("zero personal cargo upgrade persists 24", LoadPersonalCargoCapacity(tempDb) == 24);

            SetPersonalCargoCapacity(tempDb, 8);
            Check(
                "legacy personal cargo tool use is handled",
                store.TryUsePersonalCargoUpgradeTicket(
                    CharacterId,
                    AccountId,
                    InventoryListType.Main,
                    LegacyPersonalCargoToolSlot,
                    LegacyPersonalCargoUpgradeItemId,
                    out var legacyToolResult));
            Check("legacy personal cargo tool use succeeds", legacyToolResult.Success);
            Check("legacy personal cargo previous capacity 8", legacyToolResult.PreviousListParam16 == 8);
            Check("legacy personal cargo upgrades to 24", legacyToolResult.NewListParam16 == 24);
            Check("legacy personal cargo tool consumes one item", LoadStackCount(tempDb, LegacyPersonalCargoToolSlot) == 1);
            Check("database capacity after legacy personal cargo tool", LoadPersonalCargoCapacity(tempDb) == 24);

            SetPersonalCargoCapacity(tempDb, 152);
            Check(
                "maxed ticket use is handled",
                store.TryUsePersonalCargoUpgradeTicket(
                    CharacterId,
                    AccountId,
                    InventoryListType.Main,
                    MaxedTicketSlot,
                    CargoUpgradeTicketItemId,
                    out var maxedResult));
            Check("maxed ticket use fails without consume", maxedResult.Status == PersonalCargoUpgradeTicketStatus.Maxed);
            Check("maxed ticket remains", LoadStackCount(tempDb, MaxedTicketSlot) == 1);
            Check("maxed capacity remains 152", LoadPersonalCargoCapacity(tempDb) == 152);

            Check(
                "cargo ticket trusts database slot over client item code",
                store.TryUsePersonalCargoUpgradeTicket(
                    CharacterId,
                    AccountId,
                    InventoryListType.Main,
                    MaxedTicketSlot,
                    NonCargoStackableItemId,
                    out var mismatchedItemCodeResult)
                && mismatchedItemCodeResult.Status == PersonalCargoUpgradeTicketStatus.Maxed);
            Check("mismatched item-code maxed ticket remains", LoadStackCount(tempDb, MaxedTicketSlot) == 1);

            Check(
                "non-cargo stackable is not handled",
                !store.TryUsePersonalCargoUpgradeTicket(
                    CharacterId,
                    AccountId,
                    InventoryListType.Main,
                    NonCargoSlot,
                    NonCargoStackableItemId,
                    out var nonCargoResult)
                && nonCargoResult.Status == PersonalCargoUpgradeTicketStatus.NotApplicable);
            Check("non-cargo stackable remains", LoadStackCount(tempDb, NonCargoSlot) == 2);

            SetPersonalCargoCapacity(tempDb, 8);
            SetAccountCera(tempDb, StartingCera);
            var legacyToolCountBeforeShop = store.CountItem(CharacterId, LegacyPersonalCargoUpgradeItemId);
            Check(
                "legacy personal cargo shop upgrade succeeds",
                store.TryBuyCeraShopItem(CharacterId, AccountId, LegacyPersonalCargoUpgradeCommodityNo, 1, 0, 0, out var personalCargoShopResult));
            Check("legacy personal cargo shop consumed on purchase", personalCargoShopResult != null && personalCargoShopResult.ConsumedOnPurchase);
            Check("legacy personal cargo shop result marks personal cargo", personalCargoShopResult != null && personalCargoShopResult.ListType == InventoryListType.PersonalCargo);
            Check("legacy personal cargo shop upgrades capacity", LoadPersonalCargoCapacity(tempDb) == 24);
            Check("legacy personal cargo shop tool is not delivered", store.CountItem(CharacterId, LegacyPersonalCargoUpgradeItemId) == legacyToolCountBeforeShop);
            Check("legacy personal cargo shop deducts cera", LoadAccountCera(tempDb) == StartingCera - 60);

            DeleteAccountCargoState(tempDb);
            SetCharacterGold(tempDb, 100000);
            Check(
                "account cargo create spends gold",
                store.TryCreateAccountCargo(CharacterId, AccountId, out var createCostResult, out _)
                && createCostResult != null
                && createCostResult.GoldSpent
                && createCostResult.UpdatedGold == 0);
            Check("account cargo create opens selection key 1", LoadAccountCargoSelectionKey(tempDb) == 1);
            Check("account cargo create gold deducted", LoadCharacterGold(tempDb) == 0);

            SetCharacterGold(tempDb, 2000000);
            Check(
                "account cargo upgrade 1 to 8 spends gold",
                store.TryUpgradeAccountCargo(CharacterId, AccountId, out var firstUpgradeCostResult, out _)
                && firstUpgradeCostResult != null
                && firstUpgradeCostResult.GoldSpent
                && firstUpgradeCostResult.UpdatedGold == 0);
            Check("account cargo normal upgrade reaches 8", LoadAccountCargoSelectionKey(tempDb) == 8);
            Check("account cargo normal upgrade gold deducted", LoadCharacterGold(tempDb) == 0);

            SetAccountCera(tempDb, StartingCera);
            Check(
                "account cargo upgrade 8 to 16 spends cera",
                store.TryUpgradeAccountCargo(CharacterId, AccountId, out var ceraUpgradeCostResult, out _)
                && ceraUpgradeCostResult != null
                && ceraUpgradeCostResult.UpdatedCoin == StartingCera - 2000);
            Check("account cargo normal upgrade reaches 16", LoadAccountCargoSelectionKey(tempDb) == 16);
            Check("account cargo normal upgrade cera deducted", LoadAccountCera(tempDb) == StartingCera - 2000);

            InsertMainStackable(tempDb, VoidMagicStoneSlot, VoidMagicStoneItemId, 300);
            Check(
                "account cargo upgrade 16 to 24 consumes void magic stones",
                store.TryUpgradeAccountCargo(CharacterId, AccountId, out var voidUpgradeCostResult, out _)
                && voidUpgradeCostResult != null
                && voidUpgradeCostResult.CostItemTemplateId == VoidMagicStoneItemId
                && voidUpgradeCostResult.CostItemNewStackCount == 50);
            Check("account cargo void magic upgrade reaches 24", LoadAccountCargoSelectionKey(tempDb) == 24);
            Check("account cargo void magic stones deducted to 50", LoadStackCount(tempDb, VoidMagicStoneSlot) == 50);

            Check(
                "account cargo upgrade rejects insufficient void magic stones",
                !store.TryUpgradeAccountCargo(CharacterId, AccountId, out _, out var insufficientVoidErrorCode)
                && insufficientVoidErrorCode == 0x14);
            Check("insufficient void magic upgrade keeps selection key", LoadAccountCargoSelectionKey(tempDb) == 24);
            Check("insufficient void magic upgrade keeps stones", LoadStackCount(tempDb, VoidMagicStoneSlot) == 50);

            InsertMainStackable(tempDb, VoidMagicStoneSlot, VoidMagicStoneItemId, 300);
            Check(
                "account cargo upgrade 24 to 32 consumes void magic stones",
                store.TryUpgradeAccountCargo(CharacterId, AccountId, out var secondVoidUpgradeCostResult, out _)
                && secondVoidUpgradeCostResult != null
                && secondVoidUpgradeCostResult.CostItemTemplateId == VoidMagicStoneItemId
                && secondVoidUpgradeCostResult.CostItemNewStackCount == 50);
            Check("account cargo second void magic upgrade reaches 32", LoadAccountCargoSelectionKey(tempDb) == 32);
            Check("account cargo second void magic stones deducted", LoadStackCount(tempDb, VoidMagicStoneSlot) == 50);

            SetAccountCargoSelectionKey(tempDb, 1);
            SetAccountCera(tempDb, StartingCera);
            Check(
                "account cargo shop upgrade succeeds",
                store.TryBuyCeraShopItem(CharacterId, AccountId, AccountCargoUpgradeCommodityNo, 1, 0, 0, out var shopResult));
            Check("account cargo shop upgrade consumed on purchase", shopResult != null && shopResult.ConsumedOnPurchase);
            Check("account cargo shop upgrade result marks account cargo", shopResult != null && shopResult.ListType == InventoryListType.AccountCargo);
            Check("account cargo selection key upgraded", LoadAccountCargoSelectionKey(tempDb) == 8);
            Check("account cargo upgrade tool is not delivered", store.CountItem(CharacterId, AccountCargoUpgradeToolItemId) == 0);
            Check("account cargo shop upgrade deducts cera", LoadAccountCera(tempDb) == StartingCera - 2000);
            Check(
                "multi-count account cargo shop upgrade is rejected",
                !store.TryBuyCeraShopItem(CharacterId, AccountId, AccountCargoUpgradeCommodityNo, 2, 0, 0, out _));
            Check("multi-count account cargo shop reject keeps selection key", LoadAccountCargoSelectionKey(tempDb) == 8);
            Check("multi-count account cargo shop reject keeps cera", LoadAccountCera(tempDb) == StartingCera - 2000);

            Check(
                "non-account cargo stackable is not handled",
                !store.TryUseAccountCargoUpgradeTool(
                    CharacterId,
                    AccountId,
                    InventoryListType.Main,
                    NonCargoSlot,
                    out var nonAccountCargoResult)
                && nonAccountCargoResult.Status == AccountCargoUpgradeToolStatus.NotApplicable);

            InsertMainStackable(tempDb, AccountCargoToolSlot, AccountCargoUpgradeToolItemId, 2);
            Check(
                "account cargo upgrade tool use is handled",
                store.TryUseAccountCargoUpgradeTool(
                    CharacterId,
                    AccountId,
                    InventoryListType.Main,
                    AccountCargoToolSlot,
                    out var accountCargoToolResult));
            Check("account cargo upgrade tool use succeeds", accountCargoToolResult.Success);
            Check("account cargo tool upgrades selection key to 16", LoadAccountCargoSelectionKey(tempDb) == 16);
            Check("account cargo tool consumes one item", LoadStackCount(tempDb, AccountCargoToolSlot) == 1);

            SetAccountCargoSelectionKey(tempDb, 64);
            Check(
                "maxed account cargo tool use is handled",
                store.TryUseAccountCargoUpgradeTool(
                    CharacterId,
                    AccountId,
                    InventoryListType.Main,
                    AccountCargoToolSlot,
                    out var maxedAccountCargoToolResult));
            Check("maxed account cargo tool fails without consume", maxedAccountCargoToolResult.Status == AccountCargoUpgradeToolStatus.Maxed);
            Check("maxed account cargo tool remains", LoadStackCount(tempDb, AccountCargoToolSlot) == 1);

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static void Seed(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash, cera)
VALUES (@accountId, 'personal-cargo-selftest', '', @startingCera);

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'personal-cargo-selftest');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@newCharacterId, @accountId, 'personal-cargo-new-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 2, 24);

INSERT OR REPLACE INTO account_cargo_state (account_id, selection_key, value32, item_count)
VALUES (@accountId, 1, 0, 0);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES
    ('character', @characterId, @characterId, 0, @ticketSlot, @ticketItemId, 'stackable',
     2, 2, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @maxedTicketSlot, @ticketItemId, 'stackable',
     1, 1, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @nonCargoSlot, @nonCargoItemId, 'stackable',
     2, 2, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @legacyPersonalCargoToolSlot, @legacyPersonalCargoUpgradeItemId, 'stackable',
     2, 2, 0, 0, 0, 0, 0, 0, '{}');";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@newCharacterId", NewCharacterId);
                    command.Parameters.AddWithValue("@startingCera", StartingCera);
                    command.Parameters.AddWithValue("@ticketSlot", TicketSlot);
                    command.Parameters.AddWithValue("@maxedTicketSlot", MaxedTicketSlot);
                    command.Parameters.AddWithValue("@nonCargoSlot", NonCargoSlot);
                    command.Parameters.AddWithValue("@legacyPersonalCargoToolSlot", LegacyPersonalCargoToolSlot);
                    command.Parameters.AddWithValue("@ticketItemId", CargoUpgradeTicketItemId);
                    command.Parameters.AddWithValue("@nonCargoItemId", NonCargoStackableItemId);
                    command.Parameters.AddWithValue("@legacyPersonalCargoUpgradeItemId", LegacyPersonalCargoUpgradeItemId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int LoadAccountCargoSelectionKey(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT selection_key FROM account_cargo_state WHERE account_id=@accountId;";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static int LoadAccountCera(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT cera FROM accounts WHERE account_id=@accountId;";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static void SetAccountCargoSelectionKey(string databasePath, int selectionKey)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE account_cargo_state SET selection_key=@selectionKey WHERE account_id=@accountId;";
                    command.Parameters.AddWithValue("@selectionKey", selectionKey);
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteAccountCargoState(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DELETE FROM account_cargo_state WHERE account_id=@accountId;";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int LoadCharacterGold(string databasePath)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COALESCE(stack_count, 0) FROM character_items WHERE character_id=@characterId AND list_type=0 AND slot_index=0;";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    var value = command.ExecuteScalar();
                    return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
                }
            }
        }

        private static void SetCharacterGold(string databasePath, int gold)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, 0, 0, 'unknown',
    @gold, @gold, 0, 0, 0, 0, 0, 0, '{}');";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@gold", gold);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SetAccountCera(string databasePath, int cera)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE accounts SET cera=@cera WHERE account_id=@accountId;";
                    command.Parameters.AddWithValue("@cera", cera);
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void InsertMainStackable(string databasePath, short slotIndex, int itemTemplateId, int stackCount)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, @slotIndex, @itemTemplateId, 'stackable',
    @stackCount, @stackCount, 0, 0, 0, 0, 0, 0, '{}');";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slotIndex", slotIndex);
                    command.Parameters.AddWithValue("@itemTemplateId", itemTemplateId);
                    command.Parameters.AddWithValue("@stackCount", stackCount);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int LoadPersonalCargoCapacity(string databasePath)
            => LoadPersonalCargoCapacity(databasePath, CharacterId);

        private static int LoadPersonalCargoCapacity(string databasePath, int characterId)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT list_param16 FROM character_container_state WHERE character_id=@characterId AND list_type=2;";
                    command.Parameters.AddWithValue("@characterId", characterId);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static void SetPersonalCargoCapacity(string databasePath, int capacity)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE character_container_state SET list_param16=@capacity WHERE character_id=@characterId AND list_type=2;";
                    command.Parameters.AddWithValue("@capacity", capacity);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int LoadStackCount(string databasePath, short slotIndex)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COALESCE(stack_count, 0) FROM character_items WHERE character_id=@characterId AND list_type=0 AND slot_index=@slotIndex;";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slotIndex", slotIndex);
                    var value = command.ExecuteScalar();
                    return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
                }
            }
        }

        private static string NormalizeTag(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value.Replace("`", string.Empty).Trim().Trim('[', ']').Trim();
        }

        private static void DeleteTempDatabase(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (File.Exists(path + "-wal"))
                    File.Delete(path + "-wal");
                if (File.Exists(path + "-shm"))
                    File.Delete(path + "-shm");
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
            Console.WriteLine($"=== PERSONAL_CARGO selftest result: pass={_pass}, fail={_fail} ===");
        }
    }
}
