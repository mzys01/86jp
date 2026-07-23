using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class ResetItemAttrSelfTest
    {
        private const int AccountId = 980501;
        private const int CharacterId = 980601;
        private const int TargetItemId = 100310007;
        private const int GoldJewelryBoxId = 2683897;
        private const int GoldArmorBoxId = 2683896;
        private const int StandardBoxId = 15;
        private const int LiberatedBoxId = 897;
        private const short TargetSlot = 13;
        private const short SecondTargetSlot = 14;
        private const short LockedTargetSlot = 15;
        private const short GoldSlot = 78;
        private const short StandardSlot = 79;
        private const short IncompatibleBoxSlot = 80;
        private const short ExpiredBoxSlot = 81;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== RESET_ITEM_ATTR selftest ===");

            var capturedBody = new byte[] { 0x0D, 0x00, 0xF7, 0x9B, 0xFA, 0x05, 0x4E, 0x00 };
            Check("parse captured 8-byte request", ResetItemAttrRequestParser.TryParse(capturedBody, out var parsed));
            Check("captured target slot", parsed != null && parsed.TargetSlotIndex == TargetSlot);
            Check("captured target template", parsed != null && parsed.TargetItemTemplateId == TargetItemId);
            Check("captured material slot", parsed != null && parsed.MaterialSlotIndex == GoldSlot);
            Check("plaintext compatible parser stays plaintext",
                ResetItemAttrRequestParser.TryParseCompatible(capturedBody, out var compatiblePlaintext, out var plaintextDecoded)
                && !plaintextDecoded
                && compatiblePlaintext.TargetSlotIndex == TargetSlot
                && compatiblePlaintext.TargetItemTemplateId == TargetItemId
                && compatiblePlaintext.MaterialSlotIndex == GoldSlot);

            var legacyCipherBody = new byte[] { 0xDC, 0x3C, 0x75, 0xE7, 0x5B, 0x5F, 0x0D, 0x0F };
            Check("legacy cipher request is decoded",
                ResetItemAttrRequestParser.TryParseCompatible(legacyCipherBody, out var decodedRequest, out var legacyDecoded)
                && legacyDecoded);
            Check("decoded request restores target locator", decodedRequest != null
                && decodedRequest.TargetSlotIndex == TargetSlot
                && decodedRequest.TargetItemTemplateId == TargetItemId
                && decodedRequest.MaterialSlotIndex == GoldSlot);
            Check("reject malformed request length", !ResetItemAttrRequestParser.TryParse(new byte[7], out _));

            Check("gold jewelry policy resolves", TryResolvePolicy(GoldJewelryBoxId, out var goldPolicy));
            Check("gold jewelry policy is highest", goldPolicy != null && goldPolicy.Mode == ResetItemAttrMode.Highest);
            Check("gold jewelry allows wrist", goldPolicy != null && goldPolicy.Allows(EquipmentType.Wrist));
            Check("gold jewelry rejects coat", goldPolicy != null && !goldPolicy.Allows(EquipmentType.Coat));
            Check("standard policy resolves", TryResolvePolicy(StandardBoxId, out var standardPolicy));
            Check("standard policy is random", standardPolicy != null && standardPolicy.Mode == ResetItemAttrMode.Random);
            Check("standard policy allows title base attributes", standardPolicy != null && standardPolicy.Allows(EquipmentType.TitleName));
            CheckPvfPolicyCoverage();

            var databasePath = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests", "reset-item-attr.db");
            DeleteTempDatabase(databasePath);
            SeedDatabase(databasePath);

            var store = new SqliteInventoryStore(databasePath, ServerPaths.SchemaFilePath);
            var goldRequest = decodedRequest;
            Check("gold box succeeds", store.TryResetItemAttr(CharacterId, AccountId, goldRequest, out var goldResult));
            Check("gold result mode", goldResult != null && goldResult.Mode == ResetItemAttrMode.Highest);
            Check("gold result seed is top", goldResult != null && goldResult.NewQualitySeed == unchecked((int)ItemQuality.TopQualitySeed));
            Check("gold material decremented", goldResult != null && goldResult.MaterialRemainingCount == 1 && ReadStack(databasePath, GoldSlot) == 1);
            Check("gold target persisted", ReadStack(databasePath, TargetSlot) == unchecked((int)ItemQuality.TopQualitySeed));
            Check("gold quality seed mirrors instance value", ReadInstanceValue(databasePath, TargetSlot) == unchecked((int)ItemQuality.TopQualitySeed));

            var standardRequest = new ResetItemAttrRequest
            {
                TargetSlotIndex = SecondTargetSlot,
                TargetItemTemplateId = TargetItemId,
                MaterialSlotIndex = StandardSlot,
            };
            var oldStandardSeed = ReadStack(databasePath, SecondTargetSlot);
            Check("standard box succeeds", store.TryResetItemAttr(CharacterId, AccountId, standardRequest, out var standardResult));
            Check("standard result mode", standardResult != null && standardResult.Mode == ResetItemAttrMode.Random);
            Check("standard seed is valid and changed", standardResult != null
                && standardResult.NewQualitySeed > 0
                && standardResult.NewQualitySeed < unchecked((int)ItemQuality.TopQualitySeed)
                && standardResult.NewQualitySeed != oldStandardSeed);
            Check("standard quality seed mirrors instance value", standardResult != null
                && ReadInstanceValue(databasePath, SecondTargetSlot) == standardResult.NewQualitySeed);
            Check("standard box is consumed", ReadStack(databasePath, StandardSlot) == -1);
            Check("final material removes sort lock", ReadSortLockCount(databasePath, StandardSlot) == 0);

            var incompatibleRequest = new ResetItemAttrRequest
            {
                TargetSlotIndex = TargetSlot,
                TargetItemTemplateId = TargetItemId,
                MaterialSlotIndex = IncompatibleBoxSlot,
            };
            var seedBeforeIncompatible = ReadStack(databasePath, TargetSlot);
            Check("incompatible PVF part is rejected", !store.TryResetItemAttr(CharacterId, AccountId, incompatibleRequest, out var incompatibleResult));
            Check("incompatible error code", incompatibleResult != null && incompatibleResult.ErrorCode == ResetItemAttrResult.ErrorUnsupported);
            Check("incompatible box is not consumed", ReadStack(databasePath, IncompatibleBoxSlot) == 1);
            Check("incompatible target is unchanged", ReadStack(databasePath, TargetSlot) == seedBeforeIncompatible);

            var staleRequest = new ResetItemAttrRequest
            {
                TargetSlotIndex = TargetSlot,
                TargetItemTemplateId = TargetItemId + 1,
                MaterialSlotIndex = GoldSlot,
            };
            Check("stale target template is rejected", !store.TryResetItemAttr(CharacterId, AccountId, staleRequest, out var staleResult));
            Check("stale target does not consume box", staleResult != null && staleResult.ErrorCode == ResetItemAttrResult.ErrorInvalidTarget && ReadStack(databasePath, GoldSlot) == 1);

            var lockedRequest = new ResetItemAttrRequest
            {
                TargetSlotIndex = LockedTargetSlot,
                TargetItemTemplateId = TargetItemId,
                MaterialSlotIndex = GoldSlot,
            };
            var lockedSeed = ReadStack(databasePath, LockedTargetSlot);
            Check("locked target is rejected", !store.TryResetItemAttr(CharacterId, AccountId, lockedRequest, out var lockedResult));
            Check("locked error and no mutation", lockedResult != null
                && lockedResult.ErrorCode == ResetItemAttrResult.ErrorLocked
                && ReadStack(databasePath, LockedTargetSlot) == lockedSeed
                && ReadStack(databasePath, GoldSlot) == 1);

            var expiredRequest = new ResetItemAttrRequest
            {
                TargetSlotIndex = TargetSlot,
                TargetItemTemplateId = TargetItemId,
                MaterialSlotIndex = ExpiredBoxSlot,
            };
            Check("expired box is rejected", !store.TryResetItemAttr(CharacterId, AccountId, expiredRequest, out var expiredResult));
            Check("expired box is not consumed", expiredResult != null
                && expiredResult.ErrorCode == ResetItemAttrResult.ErrorInvalidMaterial
                && ReadStack(databasePath, ExpiredBoxSlot) == 1);

            CheckAckBuilders(goldResult);
            Check("audit row written", ReadAuditCount(databasePath) >= 2);

            PrintSummary();
            DeleteTempDatabase(databasePath);
            return _fail == 0 ? 0 : 1;
        }

        private static bool TryResolvePolicy(int itemTemplateId, out ResetItemAttrPolicy policy)
        {
            var stackable = InventoryDbPrimitives.LoadStackableItem(itemTemplateId);
            return ResetItemAttrPolicyResolver.TryResolve(itemTemplateId, stackable, out policy);
        }

        private static void CheckAckBuilders(ResetItemAttrResult result)
        {
            var success = ResetItemAttrAckBuilder.BuildSuccess(result);
            Check("success ACK is 10-byte result", success.Length == ResetItemAttrAckBuilder.SuccessLength);
            Check("success ACK carries status", success[0] == 0x01);
            Check("success ACK carries target item", BitConverter.ToInt32(success, 1) == TargetItemId);
            Check("success ACK carries list type", success[5] == (byte)InventoryListType.Main);
            Check("success ACK carries target slot", BitConverter.ToInt32(success, 6) == TargetSlot);
            var secondTargetAck = ResetItemAttrAckBuilder.Build(TargetItemId + 1, InventoryListType.Main, SecondTargetSlot);
            Check("success ACK carries another target locator", secondTargetAck.Length == ResetItemAttrAckBuilder.SuccessLength
                && secondTargetAck[0] == 0x01
                && BitConverter.ToInt32(secondTargetAck, 1) == TargetItemId + 1
                && BitConverter.ToInt32(secondTargetAck, 6) == SecondTargetSlot);
            var error = ResetItemAttrAckBuilder.BuildError(ResetItemAttrResult.ErrorInvalidTarget);
            Check("error ACK carries status and code", error.Length == ResetItemAttrAckBuilder.ErrorLength
                && error[0] == 0x00
                && error[1] == ResetItemAttrResult.ErrorInvalidTarget);

            var ackEnvelope = GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.RESET_ITEM_ATTR,
                success);
            Check("success envelope is command direction", ackEnvelope[0] == 0x01);
            Check("success envelope type is RESET_ITEM_ATTR", BitConverter.ToUInt16(ackEnvelope, 1) == 0x0051);
            Check("success envelope body length matches", BitConverter.ToInt32(ackEnvelope, 3) == success.Length + 15);
            Check("success envelope carries status at framed offset", ackEnvelope[15] == 0x01);
            Check("success envelope carries target item at framed offset", BitConverter.ToInt32(ackEnvelope, 16) == TargetItemId);
            Check("success envelope carries list at framed offset", ackEnvelope[20] == (byte)InventoryListType.Main);
            Check("success envelope carries slot at framed offset", BitConverter.ToInt32(ackEnvelope, 21) == TargetSlot);

            var refreshBody = new byte[] { 0x13, 0x00 };
            var refreshEnvelope = GamePacketEnvelopeBuilder.Build(0x00, 0x000E, refreshBody);
            Check("refresh envelope is notification direction", refreshEnvelope[0] == 0x00
                && BitConverter.ToUInt16(refreshEnvelope, 1) == 0x000E);
            Check("reset flow does not frame a fake complete-display body", ResetItemAttrAckBuilder.SuccessLength == 10
                && ResetItemAttrAckBuilder.ErrorLength == 2);
        }

        private static void CheckPvfPolicyCoverage()
        {
            Check("liberated box policy resolves", TryResolvePolicy(LiberatedBoxId, out var liberatedPolicy));
            Check("liberated box is random and unrestricted", liberatedPolicy != null
                && liberatedPolicy.Mode == ResetItemAttrMode.Random
                && liberatedPolicy.Allows(EquipmentType.Weapon)
                && liberatedPolicy.Allows(EquipmentType.TitleName));

            CheckGoldPolicies("weapon", new[] { 2683895, 10004897, 10006368, 10007452 }, EquipmentType.Weapon, EquipmentType.Coat);
            CheckGoldPolicies("armor", new[] { 2683896, 10006369, 10007453 }, EquipmentType.Coat, EquipmentType.Wrist);
            CheckGoldPolicies("accessory", new[] { 2683897, 10006370 }, EquipmentType.Wrist, EquipmentType.Weapon);
            CheckGoldPolicies("special", new[] { 2683898 }, EquipmentType.MagicStone, EquipmentType.Ring);

            Check("all-equipment gold policy resolves", TryResolvePolicy(10007893, out var allEquipmentPolicy));
            Check("all-equipment gold policy covers every PVF group", allEquipmentPolicy != null
                && allEquipmentPolicy.Mode == ResetItemAttrMode.Highest
                && allEquipmentPolicy.Allows(EquipmentType.Weapon)
                && allEquipmentPolicy.Allows(EquipmentType.Coat)
                && allEquipmentPolicy.Allows(EquipmentType.Wrist)
                && allEquipmentPolicy.Allows(EquipmentType.MagicStone)
                && !allEquipmentPolicy.Allows(EquipmentType.TitleName));

            var packageIds = new[]
            {
                2660433, 2660665, 2660867, 2682801, 2683277, 2683289,
                2683893, 2683894, 10002477, 10002761, 10004701,
                10005193, 10005762, 10006013, 10006423, 10007378, 10007389,
            };
            foreach (var packageId in packageIds)
                Check($"package {packageId} is not a reset material", !TryResolvePolicy(packageId, out _));
        }

        private static void CheckGoldPolicies(
            string group,
            int[] itemIds,
            EquipmentType allowedType,
            EquipmentType rejectedType)
        {
            foreach (var itemId in itemIds)
            {
                Check($"gold {group} policy {itemId}", TryResolvePolicy(itemId, out var policy)
                    && policy.Mode == ResetItemAttrMode.Highest
                    && policy.Allows(allowedType)
                    && !policy.Allows(rejectedType));
            }
        }

        private static void SeedDatabase(string databasePath)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'reset-item-attr-selftest', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'reset-item-attr-selftest');
INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 0, 24);

INSERT OR REPLACE INTO character_items
    (owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
     stack_count, instance_value, durability, seal_flag, option_value, equipment_lock_id,
     expire_time, marker_16, pet_serial_or_handle, extra_json)
VALUES
    ('character', @characterId, @characterId, 0, @targetSlot, @targetItemId, 'equipment',
     218579802, 0, 100, 0, 0, 0, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 0, @secondTargetSlot, @targetItemId, 'equipment',
     218579801, 0, 100, 0, 0, 0, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 0, @lockedTargetSlot, @targetItemId, 'equipment',
     218579800, 0, 100, 0, 0, 7, 0, -1, 0, '{}'),
    ('character', @characterId, @characterId, 0, @goldSlot, @goldJewelryBoxId, 'stackable',
     2, 2, 0, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @standardSlot, @standardBoxId, 'stackable',
     1, 1, 0, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @incompatibleBoxSlot, @goldArmorBoxId, 'stackable',
     1, 1, 0, 0, 0, 0, 0, 0, 0, '{}'),
    ('character', @characterId, @characterId, 0, @expiredBoxSlot, @goldJewelryBoxId, 'stackable',
     1, 1, 0, 0, 0, 0, @expiredTime, 0, 0, '{}');

INSERT OR REPLACE INTO character_item_locks
    (character_id, equipment_lock_id, inventory_list_type, slot, state, remaining_seconds)
VALUES (@characterId, 7, 0, @lockedTargetSlot, 1, 0);";
                    command.CommandText += @"
INSERT OR REPLACE INTO character_sort_item_locks
    (character_id, sort_order, list_type, slot_index, state)
VALUES (@characterId, 1, 0, @standardSlot, 1);";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@targetItemId", TargetItemId);
                    command.Parameters.AddWithValue("@targetSlot", TargetSlot);
                    command.Parameters.AddWithValue("@secondTargetSlot", SecondTargetSlot);
                    command.Parameters.AddWithValue("@lockedTargetSlot", LockedTargetSlot);
                    command.Parameters.AddWithValue("@goldSlot", GoldSlot);
                    command.Parameters.AddWithValue("@standardSlot", StandardSlot);
                    command.Parameters.AddWithValue("@incompatibleBoxSlot", IncompatibleBoxSlot);
                    command.Parameters.AddWithValue("@expiredBoxSlot", ExpiredBoxSlot);
                    command.Parameters.AddWithValue("@expiredTime", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());
                    command.Parameters.AddWithValue("@goldJewelryBoxId", GoldJewelryBoxId);
                    command.Parameters.AddWithValue("@standardBoxId", StandardBoxId);
                    command.Parameters.AddWithValue("@goldArmorBoxId", GoldArmorBoxId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int ReadStack(string databasePath, short slot)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT stack_count FROM character_items WHERE character_id = @characterId AND list_type = 0 AND slot_index = @slot;";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slot", slot);
                    var value = command.ExecuteScalar();
                    return value == null || value == DBNull.Value ? -1 : Convert.ToInt32(value);
                }
            }
        }

        private static int ReadInstanceValue(string databasePath, short slot)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT instance_value FROM character_items WHERE character_id = @characterId AND list_type = 0 AND slot_index = @slot;";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slot", slot);
                    var value = command.ExecuteScalar();
                    return value == null || value == DBNull.Value ? -1 : Convert.ToInt32(value);
                }
            }
        }

        private static int ReadAuditCount(string databasePath)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM item_audit_log WHERE character_id = @characterId AND action_name = 'reset_item_attr';";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static int ReadSortLockCount(string databasePath, short slot)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM character_sort_item_locks WHERE character_id = @characterId AND list_type = 0 AND slot_index = @slot;";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slot", slot);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
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
