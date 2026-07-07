using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class EquipmentEffectRuneSelfTest
    {
        private const int AccountId = 940402;
        private const int CharacterId = 940502;
        private const short RuneSlot = 86;
        private const short TargetWeaponSlot = 9;
        private const short EquippedWeaponSlot = 11;
        private const int PeachRuneItemId = 2682369;
        private const int ClearRuneItemId = 2682371;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== EQUIPMENT_EFFECT_RUNE selftest ===");

            Check("peach rune PVF is equipment effect", IsEquipmentEffectRune(PeachRuneItemId, expectedEffectId: 1));
            Check("clear rune PVF is equipment effect", IsEquipmentEffectRune(ClearRuneItemId, expectedEffectId: 0));

            var weaponItemId = FindSampleWeaponItemId();
            Check("sample weapon resolved from PVF", weaponItemId > 0);

            var tempDb = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests", "equipment-effect-rune.db");
            DeleteTempDatabase(tempDb);
            SeedCharacterAndItems(tempDb, weaponItemId);

            var store = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);
            var applyRequest = CreateRuneRequest(RuneSlot, PeachRuneItemId, TargetWeaponSlot, weaponItemId);
            Check("peach rune applies to weapon", store.TryUseEquipmentEffectRune(CharacterId, AccountId, applyRequest, out var applyResult));
            if (applyResult != null)
            {
                Check("peach rune success status", applyResult.Success);
                Check("peach rune effect id", applyResult.AppliedEffectId == 1);
                Check("peach rune consumes source stack", applyResult.SourceRemainingStackCount == 1);
                Check("peach rune target refresh item", applyResult.TargetItem != null && ReadRune(applyResult.TargetItem) == 1);
                Check("peach rune source refresh item", applyResult.SourceItem != null && applyResult.SourceItem.CountOrInstanceValue == 1);
            }

            var afterApply = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
            var targetAfterApply = afterApply.MainItems.Find(x => x.SlotIndex == TargetWeaponSlot);
            Check("snapshot weapon carries peach rune", targetAfterApply != null && ReadRune(targetAfterApply) == 1);

            var clearRequest = CreateRuneRequest((short)(RuneSlot + 1), ClearRuneItemId, TargetWeaponSlot, weaponItemId);
            Check("clear rune removes weapon rune", store.TryUseEquipmentEffectRune(CharacterId, AccountId, clearRequest, out var clearResult));
            if (clearResult != null)
            {
                Check("clear rune success status", clearResult.Success);
                Check("clear rune effect id", clearResult.AppliedEffectId == 0);
                Check("clear rune consumes source stack", clearResult.SourceRemainingStackCount == 0);
                Check("clear rune source refresh empty", clearResult.SourceItem != null && clearResult.SourceItem.ItemTemplateId < 0);
                Check("clear rune target refresh item", clearResult.TargetItem != null && ReadRune(clearResult.TargetItem) == 0);
            }

            var afterClear = store.LoadCharacterItemListSnapshot(CharacterId, AccountId);
            var targetAfterClear = afterClear.MainItems.Find(x => x.SlotIndex == TargetWeaponSlot);
            Check("snapshot weapon clears rune", targetAfterClear != null && ReadRune(targetAfterClear) == 0);

            Check("parse captured-style 0x0342 request",
                TryCreateAddEquipmentEffectRequest(RuneSlot, EquippedWeaponSlot, InventoryListType.Equipment, out var equippedApplyRequest));
            Check("peach rune applies to equipped weapon", store.TryUseEquipmentEffectRune(CharacterId, AccountId, equippedApplyRequest, out var equippedApplyResult));
            if (equippedApplyResult != null)
            {
                Check("equipped weapon success status", equippedApplyResult.Success);
                Check("equipped weapon target list", equippedApplyResult.TargetListType == InventoryListType.Equipment);
                Check("equipped weapon effect id", equippedApplyResult.AppliedEffectId == 1);
                Check("equipped weapon consumes last source stack", equippedApplyResult.SourceRemainingStackCount == 0);
                Check("equipped weapon source refresh empty", equippedApplyResult.SourceItem != null && equippedApplyResult.SourceItem.ItemTemplateId < 0);
                Check("equipped weapon target refresh item", equippedApplyResult.TargetItem != null && ReadRune(equippedApplyResult.TargetItem) == 1);
            }

            var equippedAfterApply = store.LoadEquipmentCommonItemForRefresh(CharacterId, EquippedWeaponSlot);
            Check("equipped weapon carries peach rune", equippedAfterApply != null && ReadRune(equippedAfterApply) == 1);

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static EquipmentEffectRuneUseRequest CreateRuneRequest(short sourceSlot, int runeItemId, short targetSlot, int targetItemId)
        {
            return CreateRuneRequest(sourceSlot, runeItemId, targetSlot, targetItemId, InventoryListType.Main);
        }

        private static EquipmentEffectRuneUseRequest CreateRuneRequest(short sourceSlot, int runeItemId, short targetSlot, int targetItemId, InventoryListType targetListType)
        {
            var body = new byte[18];
            BitConverter.GetBytes(sourceSlot).CopyTo(body, 0);
            body[2] = (byte)InventoryListType.Main;
            BitConverter.GetBytes(runeItemId).CopyTo(body, 3);
            BitConverter.GetBytes(runeItemId).CopyTo(body, 7);
            BitConverter.GetBytes(targetSlot).CopyTo(body, 11);
            body[13] = (byte)targetListType;
            BitConverter.GetBytes(targetItemId).CopyTo(body, 14);

            return new EquipmentEffectRuneUseRequest
            {
                SourceListType = InventoryListType.Main,
                SourceSlotIndex = sourceSlot,
                SourceInstanceValue = runeItemId,
                ExpectedSourceItemTemplateId = runeItemId,
                RawBody = body,
            };
        }

        private static bool TryCreateAddEquipmentEffectRequest(short sourceSlot, short targetSlot, InventoryListType targetListType, out EquipmentEffectRuneUseRequest request)
        {
            var body = new byte[21];
            BitConverter.GetBytes((int)sourceSlot).CopyTo(body, 8);
            body[12] = (byte)targetListType;
            BitConverter.GetBytes((int)targetSlot).CopyTo(body, 13);
            BitConverter.GetBytes((int)sourceSlot).CopyTo(body, 17);
            return EquipmentEffectRuneUseRequest.TryParseAddEquipmentEffectBody(body, out request);
        }

        private static bool IsEquipmentEffectRune(int itemTemplateId, int expectedEffectId)
        {
            if (!ItemMetadataResolver.TryLoadStackableFile(itemTemplateId, out var stackable))
                return false;

            return stackable.StackableType != null
                && stackable.StackableType.IndexOf("[equipment effect]", StringComparison.OrdinalIgnoreCase) >= 0
                && EquipmentEffectRuneUseRequest.TryParseEffectId(stackable.IntData, out var effectId)
                && effectId == expectedEffectId;
        }

        private static int FindSampleWeaponItemId()
        {
            var list = LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst"));
            foreach (var entry in list.Entries)
            {
                if (!ItemMetadataResolver.TryLoadEquipmentFile(entry.Id, out var equipment))
                    continue;

                var type = EquipmentTypeInfo.ParseOrUnknown(equipment.EquipmentType);
                if (EquipmentTypeInfo.IsWeapon(type) && equipment.Grade >= 3)
                    return entry.Id;
            }

            return 0;
        }

        private static void SeedCharacterAndItems(string databasePath, int weaponItemId)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'equipment-effect-rune-selftest', '');

INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@characterId, @accountId, 'equipment-effect-rune-selftest');

INSERT OR REPLACE INTO character_container_state (character_id, list_type, list_param16)
VALUES (@characterId, 0, 24);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, @targetWeaponSlot, @weaponItemId, 'equipment',
    @qualitySeed, @qualitySeed, 100, 0, 0, 0, -1,
    0, @weaponExtraJson);

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, @runeSlot, @peachRuneItemId, 'stackable',
    2, 2, 0, 0, 0, 0, 0,
    0, '{}');

INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, @clearRuneSlot, @clearRuneItemId, 'stackable',
    1, 1, 0, 0, 0, 0, 0,
    0, '{}');

INSERT OR REPLACE INTO character_equipped_entries (
    character_id, slot, item_id, expire_time, equipment_lock_id, raw_entry)
VALUES (
    @characterId, @equippedWeaponSlot, @weaponItemId, 0, 0, @equippedWeaponRaw);";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@targetWeaponSlot", TargetWeaponSlot);
                    command.Parameters.AddWithValue("@weaponItemId", weaponItemId);
                    command.Parameters.AddWithValue("@qualitySeed", unchecked((int)ItemQuality.TopQualitySeed));
                    command.Parameters.AddWithValue("@weaponExtraJson", InventoryItemCodec.SerializeCommon(CreateWeaponItem(weaponItemId)));
                    command.Parameters.AddWithValue("@runeSlot", RuneSlot);
                    command.Parameters.AddWithValue("@peachRuneItemId", PeachRuneItemId);
                    command.Parameters.AddWithValue("@clearRuneSlot", (short)(RuneSlot + 1));
                    command.Parameters.AddWithValue("@clearRuneItemId", ClearRuneItemId);
                    command.Parameters.AddWithValue("@equippedWeaponSlot", EquippedWeaponSlot);
                    command.Parameters.AddWithValue("@equippedWeaponRaw", CreateEquippedWeaponRaw(weaponItemId));
                    command.ExecuteNonQuery();
                }
            }
        }

        private static CommonInventoryItem CreateWeaponItem(int weaponItemId)
        {
            return new CommonInventoryItem
            {
                SlotIndex = TargetWeaponSlot,
                ItemTemplateId = weaponItemId,
                CountOrInstanceValue = unchecked((int)ItemQuality.TopQualitySeed),
                Durability = 100,
                Marker16 = -1,
                TailData2F = new byte[37],
                JewelSocket = new byte[30],
            };
        }

        private static byte[] CreateEquippedWeaponRaw(int weaponItemId)
        {
            return MakeEquipListCodec.BuildEntryFromDisplayFields(
                EquippedWeaponSlot,
                weaponItemId,
                new MakeEquipListCodec.DisplayFields
                {
                    InstanceValue = ItemQuality.TopQualitySeed,
                    Durability = 100,
                });
        }

        private static ushort ReadRune(CommonInventoryItem item)
        {
            return item != null && item.TailData2F != null && item.TailData2F.Length >= 11
                ? BitConverter.ToUInt16(item.TailData2F, 9)
                : (ushort)0;
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
