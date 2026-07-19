using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class DeathTowerInventorySelfTest
    {
        private const int DeathTowerDungeonId = 11000;
        private const int TowerMaterialItemId = 6515;
        private const int TowerHastePotionItemId = 6518;
        private const int TowerHealthPotionItemId = 6521;
        private const int TowerManaPotionItemId = 6524;
        private const int QuestItemId = 10089292;
        private const int StackableWasteFixtureItemId = 2660671;

        public static int Run()
        {
            Console.WriteLine("=== DEATH_TOWER_INVENTORY selftest ===");
            var failures = 0;

            Check("6515 is PVF [material] family, not [waste]",
                IsExactType(TowerMaterialItemId, "[material]")
                    && !IsExactType(TowerMaterialItemId, "[waste]"),
                ref failures);
            Check("6518/6521/6524 are PVF [waste] family",
                IsExactType(TowerHastePotionItemId, "[waste]")
                    && IsExactType(TowerHealthPotionItemId, "[waste]")
                    && IsExactType(TowerManaPotionItemId, "[waste]"),
                ref failures);
            Check("quest fixture item is PVF [quest] family",
                IsExactType(QuestItemId, "[quest]"), ref failures);
            Check("synthetic stackable primary family requires the first bounded tag",
                SyntheticPrimaryFamilyBoundariesAreExact(), ref failures);
            Check("synthetic material and quest families use their shared slot ranges",
                SyntheticSlotRangesAreExact(), ref failures);
            Check("waste family accepts modifiers but rejects unrelated text and tags",
                SyntheticWasteBoundariesAreExact(), ref failures);
            Check("ordinary SQLite add shares the real PVF waste/material/quest classification",
                OrdinarySqliteClassificationIsShared(), ref failures);
            Check("null tower metadata gets its default slot range from ItemMetadata",
                NullTowerMetadataUsesSharedDefaultSlotRange(), ref failures);
            Check("ordinary SQLite preserves and merges an existing 6518 bag stack",
                OrdinarySqlitePreservesLegacyBagStack(), ref failures);
            Check("ordinary full quickslots overflow to 65 without affecting tower quickslots",
                OrdinaryOverflowStaysSeparateFromTower(), ref failures);
            Check("DeathTowerSession starts at stage zero without redundant snapshot/count APIs",
                SessionSurfaceIsMinimal(), ref failures);
            Check("tower inventory DTOs omit redundant slot fields",
                TowerInventoryDtosAreMinimal(), ref failures);
            Check("DeathTowerPacketBuilder retains BuildEmptyReward",
                typeof(DeathTowerPacketBuilder).GetMethod(
                    "BuildEmptyReward",
                    BindingFlags.Public | BindingFlags.Static) != null,
                ref failures);

            var pickup = FindOutMethod("TryPickupGroundItem", typeof(ushort));
            var use = FindOutMethod("TryUseItem", typeof(short), typeof(int));
            var move = FindOutMethod("TryMoveItem", typeof(short), typeof(short), typeof(int));
            Check("DeathTowerSession exposes isolated pickup/use/move APIs",
                pickup != null && use != null && move != null, ref failures);
            Check("DeathTowerHandler exposes dedicated 0x002B/0x002C/0x0013 routes",
                HasTowerProtocolMethod("TryHandleGetItem", 2)
                    && HasTowerProtocolMethod("TryHandleUseStackable", 3)
                    && HasTowerProtocolMethod("TryHandleMoveItem", 3),
                ref failures);

            if (pickup != null && use != null && move != null)
            {
                var tower = CreateClassificationTower();
                Check("tower pickups allocate waste 3-8, material 121, quest 177",
                    PickupClassificationIsCorrect(tower, pickup), ref failures);
                Check("stackable tower waste stacks before taking a new slot",
                    StackableWasteMerges(pickup), ref failures);
                Check("only exact tower [waste] can be consumed",
                    UseValidationIsCorrect(tower, use), ref failures);
                Check("tower move rejects material-to-quickslot and supports waste swap",
                    MoveValidationIsCorrect(tower, move), ref failures);
                Check("six full quickslots overflow to tower slot 65",
                    WasteOverflowStartsAt65(pickup), ref failures);
                Check("full tower bag retains the unpicked ground item",
                    FullBagRetainsGroundItem(pickup), ref failures);
                Check("occupied persistent 3-8 neither affects tower slot order nor changes SQLite",
                    PersistentQuickSlotsStayIsolated(pickup, use, move), ref failures);
                Check("persistent material occupancy is skipped while tower quickslots stay isolated",
                    PersistentMainSlotsAreReserved(pickup, move), ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static DeathTowerSession CreateClassificationTower()
        {
            var tower = NewTower();
            tower.BeginStage(0x31415926, new[]
            {
                StageItem(31, 91, TowerMaterialItemId, 1),
                StageItem(32, 91, TowerHastePotionItemId, 1),
                StageItem(33, 91, TowerHealthPotionItemId, 1),
                StageItem(34, 91, TowerManaPotionItemId, 1),
                StageItem(35, 91, QuestItemId, 1),
                StageItem(36, 91, TowerHastePotionItemId, 1),
            });
            tower.GenerateDropsForMonster(91);
            return tower;
        }

        private static bool SyntheticPrimaryFamilyBoundariesAreExact()
        {
            return HasPrimaryFamily(Stackable("` [MaTeRiAl instant item] 2 `"), "material")
                && HasPrimaryFamily(Stackable("[quest instant item]"), "quest")
                && HasPrimaryFamily(Stackable("[waste instant item]"), "waste")
                && !HasPrimaryFamily(Stackable("[materialize]"), "material")
                && !HasPrimaryFamily(Stackable("[questing]"), "quest")
                && !HasPrimaryFamily(Stackable("[wasteful]"), "waste")
                && !HasPrimaryFamily(Stackable("prefix [material]"), "material")
                && !HasPrimaryFamily(Stackable("ordinary waste text"), "waste")
                && !HasPrimaryFamily(Stackable("[material] [waste]"), "waste")
                && !HasPrimaryFamily(new ItemMetadata
                {
                    ItemKind = "equipment",
                    StackableType = "[waste]",
                }, "waste");
        }

        private static bool SyntheticSlotRangesAreExact()
        {
            return HasSlotRange(Stackable("[material]"), 121, 176)
                && HasSlotRange(Stackable("[material instant item] 2"), 121, 176)
                && HasSlotRange(Stackable("[material] 4"), 345, 359)
                && HasSlotRange(Stackable("[quest]"), 177, 232)
                && HasSlotRange(Stackable("[quest instant item]"), 177, 232)
                && HasSlotRange(Stackable("[material expert job]"), 233, 288)
                && HasSlotRange(Stackable("[avatar emblem]"), 289, 344)
                && HasSlotRange(Stackable("[materialize]"), 65, 120)
                && HasSlotRange(Stackable("[questing]"), 65, 120)
                && HasSlotRange(Stackable("prefix [material]"), 65, 120);
        }

        private static bool SyntheticWasteBoundariesAreExact()
        {
            return DeathTowerItemSlotPolicy.IsWaste(Stackable("[waste]"))
                && DeathTowerItemSlotPolicy.IsWaste(Stackable("`[waste instant item]`"))
                && !DeathTowerItemSlotPolicy.IsWaste(Stackable("[wasteful]"))
                && !DeathTowerItemSlotPolicy.IsWaste(Stackable("prefix [waste]"))
                && !DeathTowerItemSlotPolicy.IsWaste(Stackable("ordinary waste text"))
                && !DeathTowerItemSlotPolicy.IsWaste(Stackable("[material] waste"))
                && !DeathTowerItemSlotPolicy.IsWaste(Stackable("[material] [waste]"))
                && !DeathTowerItemSlotPolicy.IsWaste(new ItemMetadata
                {
                    ItemKind = "equipment",
                    StackableType = "[waste]",
                });
        }

        private static bool OrdinarySqliteClassificationIsShared()
        {
            var dbPath = Path.Combine(
                Path.GetTempPath(),
                $"death-tower-sqlite-family-{Guid.NewGuid():N}.db");
            try
            {
                SeedInventoryOwner(dbPath);
                var assetService = new SqliteAssetService(dbPath, ServerPaths.SchemaFilePath);
                using (var scope = assetService.OpenScope(990011, 990011))
                {
                    var wasteAdded = assetService.TryAddItem(
                        scope, TowerHastePotionItemId, 1, out var wasteSlot);
                    var materialAdded = assetService.TryAddItem(
                        scope, TowerMaterialItemId, 1, out var materialSlot);
                    var questAdded = assetService.TryAddItem(
                        scope, QuestItemId, 1, out var questSlot);
                    scope.Commit();
                    return wasteAdded && wasteSlot == SqliteInventoryStore.QuickSlotStart
                        && materialAdded && materialSlot == 121
                        && questAdded && questSlot == 177;
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
        }

        private static bool NullTowerMetadataUsesSharedDefaultSlotRange()
        {
            var factory = typeof(ItemMetadata).GetMethod(
                "CreateDefaultStackable",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (factory == null
                || factory.ReturnType != typeof(ItemMetadata)
                || factory.GetParameters().Length != 0)
            {
                return false;
            }

            var metadata = factory.Invoke(null, Array.Empty<object>()) as ItemMetadata;
            if (metadata == null || !metadata.IsStackable)
                return false;

            metadata.GetSlotRange(out var expectedStart, out var expectedEnd);
            return DeathTowerItemSlotPolicy.GetAllocationOrder(null).SequenceEqual(
                Enumerable.Range(expectedStart, expectedEnd - expectedStart + 1)
                    .Select(slot => (short)slot));
        }

        private static bool OrdinarySqlitePreservesLegacyBagStack()
        {
            var metadata = ItemMetadataResolver.Resolve(TowerHastePotionItemId);
            if (metadata.StackLimit == 1)
                return false;
            var initialCount = metadata.StackLimit > 1
                ? metadata.StackLimit - 1
                : 10;
            var dbPath = Path.Combine(
                Path.GetTempPath(),
                $"death-tower-sqlite-legacy-waste-{Guid.NewGuid():N}.db");
            try
            {
                var connStr = SeedInventoryOwner(dbPath);
                using (var connection = new SqliteConnection(connStr))
                {
                    connection.Open();
                    InsertPersistentStackable(
                        connection,
                        990011,
                        65,
                        TowerHastePotionItemId,
                        initialCount);
                }

                var assetService = new SqliteAssetService(dbPath, ServerPaths.SchemaFilePath);
                short assignedSlot;
                using (var scope = assetService.OpenScope(990011, 990011))
                {
                    if (!assetService.TryAddItem(
                        scope,
                        TowerHastePotionItemId,
                        1,
                        out assignedSlot))
                    {
                        return false;
                    }
                    scope.Commit();
                }

                using (var connection = new SqliteConnection(connStr))
                {
                    connection.Open();
                    return assignedSlot == 65
                        && ReadStackCount(connection, 990011, 65, TowerHastePotionItemId)
                            == initialCount + 1
                        && ReadItemCountAtSlot(connection, 990011, SqliteInventoryStore.QuickSlotStart)
                            == 0;
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
        }

        private static bool OrdinaryOverflowStaysSeparateFromTower()
        {
            var dbPath = Path.Combine(
                Path.GetTempPath(),
                $"death-tower-sqlite-overflow-{Guid.NewGuid():N}.db");
            try
            {
                var connStr = SeedInventoryOwner(dbPath);
                using (var connection = new SqliteConnection(connStr))
                {
                    connection.Open();
                    for (var slot = SqliteInventoryStore.QuickSlotStart;
                        slot <= SqliteInventoryStore.QuickSlotEnd;
                        slot++)
                    {
                        InsertPersistentStackable(
                            connection,
                            990011,
                            (short)slot,
                            StackableWasteFixtureItemId + slot,
                            10 + slot);
                    }
                }

                var assetService = new SqliteAssetService(dbPath, ServerPaths.SchemaFilePath);
                short ordinarySlot;
                using (var scope = assetService.OpenScope(990011, 990011))
                {
                    if (!assetService.TryAddItem(
                        scope,
                        TowerHastePotionItemId,
                        1,
                        out ordinarySlot))
                    {
                        return false;
                    }
                    scope.Commit();
                }

                var tower = NewTower();
                tower.BeginStage(0x13572468, new[]
                {
                    StageItem(501, 95, TowerHastePotionItemId, 1),
                });
                tower.GenerateDropsForMonster(95);
                return ordinarySlot == 65
                    && tower.TryPickupGroundItem(501, out var pickup)
                    && pickup.DestinationSlot == SqliteInventoryStore.QuickSlotStart;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
        }

        private static bool SessionSurfaceIsMinimal()
        {
            var constructor = typeof(DeathTowerSession).GetConstructors(
                BindingFlags.Public | BindingFlags.Instance).SingleOrDefault();
            if (constructor == null)
                return false;
            var parameters = constructor.GetParameters();
            var tower = NewTower();
            return parameters.Length == 1
                && parameters[0].ParameterType == typeof(DeathTowerData.TowerConfig)
                && tower.CurrentStage == 0
                && typeof(DeathTowerSession).GetProperty(
                    "CurrentStageItems",
                    BindingFlags.Public | BindingFlags.Instance) == null
                && typeof(DeathTowerSession).GetMethod(
                    "GetItemCount",
                    BindingFlags.Public | BindingFlags.Instance) == null;
        }

        private static bool TowerInventoryDtosAreMinimal()
        {
            return !HasPublicInstanceMember(typeof(TowerInventoryItem), "SlotIndex")
                && !HasPublicInstanceMember(typeof(TowerPickupResult), "SceneSlot")
                && !HasPublicInstanceMember(typeof(TowerInventoryMutation), "SlotIndex")
                && !HasPublicInstanceMember(typeof(TowerInventoryMoveResult), "SourceSlot")
                && !HasPublicInstanceMember(typeof(TowerInventoryMoveResult), "DestinationSlot");
        }

        private static bool PickupClassificationIsCorrect(DeathTowerSession tower, MethodInfo pickup)
        {
            var materialOk = InvokeOutBool(pickup, tower, new object[] { (ushort)31, null }, out var material);
            var hasteOk = InvokeOutBool(pickup, tower, new object[] { (ushort)32, null }, out var haste);
            var healthOk = InvokeOutBool(pickup, tower, new object[] { (ushort)33, null }, out var health);
            var manaOk = InvokeOutBool(pickup, tower, new object[] { (ushort)34, null }, out var mana);
            var questOk = InvokeOutBool(pickup, tower, new object[] { (ushort)35, null }, out var quest);
            var stackedOk = InvokeOutBool(pickup, tower, new object[] { (ushort)36, null }, out var stacked);
            return materialOk && ReadInt(material, "DestinationSlot") == 121
                && hasteOk && ReadInt(haste, "DestinationSlot") == 3
                && healthOk && ReadInt(health, "DestinationSlot") == 4
                && manaOk && ReadInt(mana, "DestinationSlot") == 5
                && questOk && ReadInt(quest, "DestinationSlot") == 177
                && stackedOk && ReadInt(stacked, "DestinationSlot") == 3;
        }

        private static bool UseValidationIsCorrect(DeathTowerSession tower, MethodInfo use)
        {
            var materialBefore = ReadInventoryCount(tower, 121);
            var materialRejected = !InvokeOutBool(
                use, tower, new object[] { (short)121, TowerMaterialItemId, null }, out _);
            var wrongIdRejected = !InvokeOutBool(
                use, tower, new object[] { (short)3, TowerManaPotionItemId, null }, out _);
            var wasteUsed = InvokeOutBool(
                use, tower, new object[] { (short)3, TowerHastePotionItemId, null }, out var mutation);

            return materialRejected
                && wrongIdRejected
                && materialBefore == ReadInventoryCount(tower, 121)
                && wasteUsed
                && ReadInt(mutation, "RemainingCount") == 1
                && ReadInventoryCount(tower, 3) == 1;
        }

        private static bool MoveValidationIsCorrect(DeathTowerSession tower, MethodInfo move)
        {
            var materialRejected = !InvokeOutBool(
                move, tower, new object[] { (short)121, (short)3, 1, null }, out _);
            var moved = InvokeOutBool(
                move, tower, new object[] { (short)3, (short)6, 1, null }, out _);
            var swapped = InvokeOutBool(
                move, tower, new object[] { (short)6, (short)4, 1, null }, out _);

            return materialRejected
                && moved
                && swapped
                && ReadInventoryItemId(tower, 4) == TowerHastePotionItemId
                && ReadInventoryItemId(tower, 6) == TowerHealthPotionItemId;
        }

        private static bool StackableWasteMerges(MethodInfo pickup)
        {
            var metadata = ItemMetadataResolver.Resolve(StackableWasteFixtureItemId);
            if (!IsExactType(StackableWasteFixtureItemId, "[waste]")
                || (metadata.StackLimit > 0 && metadata.StackLimit < 2))
                return false;

            var tower = NewTower();
            tower.BeginStage(0x27182818, new[]
            {
                StageItem(81, 94, StackableWasteFixtureItemId, 1),
                StageItem(82, 94, StackableWasteFixtureItemId, 1),
            });
            tower.GenerateDropsForMonster(94);
            return InvokeOutBool(pickup, tower, new object[] { (ushort)81, null }, out var first)
                && InvokeOutBool(pickup, tower, new object[] { (ushort)82, null }, out var second)
                && ReadInt(first, "DestinationSlot") == 3
                && ReadInt(second, "DestinationSlot") == 3
                && ReadInventoryCount(tower, 3) == 2;
        }

        private static bool WasteOverflowStartsAt65(MethodInfo pickup)
        {
            var stackLimit = EffectiveStackLimit(TowerHastePotionItemId);
            var tower = NewTower();
            var items = new List<StageTowerItem>();
            for (var index = 0; index < 7; index++)
                items.Add(StageItem((ushort)(100 + index), 92, TowerHastePotionItemId, stackLimit));
            tower.BeginStage(0x12345678, items);
            tower.GenerateDropsForMonster(92);

            for (var index = 0; index < 7; index++)
            {
                if (!InvokeOutBool(
                    pickup,
                    tower,
                    new object[] { (ushort)(100 + index), null },
                    out var result))
                {
                    return false;
                }

                var expectedSlot = index < 6 ? 3 + index : 65;
                if (ReadInt(result, "DestinationSlot") != expectedSlot)
                    return false;
            }

            return true;
        }

        private static bool FullBagRetainsGroundItem(MethodInfo pickup)
        {
            var stackLimit = EffectiveStackLimit(TowerHastePotionItemId);
            var tower = NewTower();
            var items = new List<StageTowerItem>();
            for (var index = 0; index < 63; index++)
                items.Add(StageItem((ushort)(200 + index), 93, TowerHastePotionItemId, stackLimit));
            tower.BeginStage(0x87654321, items);
            tower.GenerateDropsForMonster(93);

            for (var index = 0; index < 62; index++)
            {
                if (!InvokeOutBool(
                    pickup,
                    tower,
                    new object[] { (ushort)(200 + index), null },
                    out _))
                {
                    return false;
                }
            }

            var lastSceneSlot = (ushort)(200 + 62);
            var rejected = !InvokeOutBool(
                pickup, tower, new object[] { lastSceneSlot, null }, out _);
            return rejected && HasGroundSceneSlot(tower, lastSceneSlot);
        }

        private static bool PersistentQuickSlotsStayIsolated(
            MethodInfo pickup,
            MethodInfo use,
            MethodInfo move)
        {
            var dbPath = Path.Combine(
                Path.GetTempPath(),
                $"death-tower-inventory-{Guid.NewGuid():N}.db");
            try
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
                using (var connection = new SqliteConnection(connStr))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (990001, 'death-tower-inventory', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (990001, 990001, 'death-tower-inventory');";
                        command.ExecuteNonQuery();
                    }

                    for (var slot = 3; slot <= 8; slot++)
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = @"
INSERT OR REPLACE INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', 990001, 990001, 0, @slot, @itemId, 'stackable',
    @count, @count, 0, 0, 0, 0, 0,
    0, '{}');";
                            command.Parameters.AddWithValue("@slot", slot);
                            command.Parameters.AddWithValue("@itemId", StackableWasteFixtureItemId + slot);
                            command.Parameters.AddWithValue("@count", 10 + slot);
                            command.ExecuteNonQuery();
                        }
                    }
                }

                var before = ReadPersistentQuickSlots(connStr);
                var tower = CreateClassificationTower();
                var picked = InvokeOutBool(
                    pickup,
                    tower,
                    new object[] { (ushort)32, null },
                    out var pickupResult);
                var used = InvokeOutBool(
                    use,
                    tower,
                    new object[] { (short)3, TowerHastePotionItemId, null },
                    out _);
                var moved = InvokeOutBool(
                    move,
                    tower,
                    new object[] { (short)3, (short)4, 1, null },
                    out _);
                var after = ReadPersistentQuickSlots(connStr);

                return picked
                    && ReadInt(pickupResult, "DestinationSlot") == 3
                    && used
                    && !moved
                    && string.Equals(before, after, StringComparison.Ordinal);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
        }

        private static string ReadPersistentQuickSlots(string connStr)
        {
            var rows = new List<string>();
            using (var connection = new SqliteConnection(connStr))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT slot_index, item_template_id, stack_count
FROM character_items
WHERE character_id=990001 AND list_type=0 AND slot_index BETWEEN 3 AND 8
ORDER BY slot_index;";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            rows.Add($"{reader.GetInt32(0)}:{reader.GetInt32(1)}:{reader.GetInt32(2)}");
                    }
                }
            }
            return string.Join("|", rows);
        }

        private static bool PersistentMainSlotsAreReserved(MethodInfo pickup, MethodInfo move)
        {
            var reserve = typeof(DeathTowerSession).GetMethod(
                "SetPersistentMainSlotOccupancy",
                BindingFlags.Public | BindingFlags.Instance);
            if (reserve == null)
                return false;

            var occupied = Enumerable.Range(3, 6)
                .Concat(Enumerable.Range(121, 53))
                .Select(slot => (short)slot)
                .ToArray();
            var tower = CreateClassificationTower();
            reserve.Invoke(tower, new object[] { occupied });

            var materialPicked = InvokeOutBool(
                pickup,
                tower,
                new object[] { (ushort)31, null },
                out var materialResult);
            var wastePicked = InvokeOutBool(
                pickup,
                tower,
                new object[] { (ushort)32, null },
                out var wasteResult);
            var moveIntoPersistentSlotRejected = !InvokeOutBool(
                move,
                tower,
                new object[] { (short)174, (short)121, 1, null },
                out _);

            return materialPicked
                && ReadInt(materialResult, "DestinationSlot") == 174
                && wastePicked
                && ReadInt(wasteResult, "DestinationSlot") == 3
                && moveIntoPersistentSlotRejected;
        }

        private static DeathTowerSession NewTower()
            => new DeathTowerSession(DeathTowerData.GetConfig(DeathTowerDungeonId));

        private static ItemMetadata Stackable(string stackableType)
            => new ItemMetadata
            {
                ItemKind = "stackable",
                StackableType = stackableType,
                StackLimit = 100,
            };

        private static bool HasPrimaryFamily(ItemMetadata metadata, string family)
            => metadata.IsPrimaryStackableFamily(family);

        private static bool HasSlotRange(ItemMetadata metadata, int expectedStart, int expectedEnd)
        {
            metadata.GetSlotRange(out var actualStart, out var actualEnd);
            return actualStart == expectedStart && actualEnd == expectedEnd;
        }

        private static bool HasPublicInstanceMember(Type type, string name)
            => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) != null
                || type.GetField(name, BindingFlags.Public | BindingFlags.Instance) != null;

        private static string SeedInventoryOwner(string dbPath)
        {
            var connStr = SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
            using (var connection = new SqliteConnection(connStr))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (990011, 'death-tower-family', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (990011, 990011, 'death-tower-family');";
                    command.ExecuteNonQuery();
                }
            }
            return connStr;
        }

        private static void InsertPersistentStackable(
            SqliteConnection connection,
            int characterId,
            short slot,
            int itemId,
            int count)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @characterId, @characterId, 0, @slot, @itemId, 'stackable',
    @count, @count, 0, 0, 0, 0, 0,
    0, '{}');";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@slot", slot);
                command.Parameters.AddWithValue("@itemId", itemId);
                command.Parameters.AddWithValue("@count", count);
                command.ExecuteNonQuery();
            }
        }

        private static int ReadStackCount(
            SqliteConnection connection,
            int characterId,
            short slot,
            int itemId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT stack_count
FROM character_items
WHERE character_id=@characterId
  AND list_type=0
  AND slot_index=@slot
  AND item_template_id=@itemId
LIMIT 1;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@slot", slot);
                command.Parameters.AddWithValue("@itemId", itemId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? 0
                    : Convert.ToInt32(value);
            }
        }

        private static int ReadItemCountAtSlot(
            SqliteConnection connection,
            int characterId,
            short slot)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(*)
FROM character_items
WHERE character_id=@characterId
  AND list_type=0
  AND slot_index=@slot;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@slot", slot);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static StageTowerItem StageItem(
            ushort itemUniqueId,
            ushort monsterUniqueId,
            int itemId,
            int count)
        {
            return new StageTowerItem
            {
                SourceListIndex = 1,
                SourceMonsterUniqueId = monsterUniqueId,
                ItemUniqueId = itemUniqueId,
                ItemId = itemId,
                DropRate = 10000,
                StackCount = count,
            };
        }

        private static MethodInfo FindOutMethod(string name, params Type[] inputTypes)
        {
            return typeof(DeathTowerSession)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method =>
                {
                    if (method.Name != name)
                        return false;
                    var parameters = method.GetParameters();
                    if (parameters.Length != inputTypes.Length + 1 || !parameters[^1].IsOut)
                        return false;
                    for (var index = 0; index < inputTypes.Length; index++)
                    {
                        if (parameters[index].ParameterType != inputTypes[index])
                            return false;
                    }
                    return true;
                });
        }

        private static bool HasTowerProtocolMethod(string name, int parameterCount)
        {
            return typeof(DeathTowerHandler)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(method => method.Name == name
                    && method.GetParameters().Length == parameterCount);
        }

        private static bool InvokeOutBool(
            MethodInfo method,
            object target,
            object[] arguments,
            out object result)
        {
            result = null;
            try
            {
                var success = (bool)method.Invoke(target, arguments);
                result = arguments[^1];
                return success;
            }
            catch (TargetInvocationException ex)
            {
                Console.WriteLine($"[FAIL] {method.Name} threw: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }

        private static bool IsExactType(int itemId, string expected)
            => ItemMetadataResolver.Resolve(itemId).IsPrimaryStackableFamily(
                expected.Trim('[', ']', ' '));

        private static int EffectiveStackLimit(int itemId)
        {
            var metadata = ItemMetadataResolver.Resolve(itemId);
            if (!metadata.IsStackable)
                return 1;
            return metadata.StackLimit > 0 ? metadata.StackLimit : int.MaxValue;
        }

        private static int ReadInventoryCount(DeathTowerSession tower, short slot)
            => ReadInventoryItemValue(tower, slot, "Count");

        private static int ReadInventoryItemId(DeathTowerSession tower, short slot)
            => ReadInventoryItemValue(tower, slot, "ItemId");

        private static int ReadInventoryItemValue(DeathTowerSession tower, short slot, string name)
        {
            var item = FindDictionaryValue(tower, "InventoryItems", slot);
            return item == null ? 0 : ReadInt(item, name);
        }

        private static bool HasGroundSceneSlot(DeathTowerSession tower, ushort sceneSlot)
            => FindDictionaryValue(tower, "GroundItems", sceneSlot) != null;

        private static object FindDictionaryValue(object owner, string propertyName, object wantedKey)
        {
            var property = owner.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            var entries = property?.GetValue(owner) as IEnumerable;
            if (entries == null)
                return null;

            foreach (var entry in entries)
            {
                var key = entry.GetType().GetProperty("Key")?.GetValue(entry);
                if (Convert.ToInt64(key) == Convert.ToInt64(wantedKey))
                    return entry.GetType().GetProperty("Value")?.GetValue(entry);
            }
            return null;
        }

        private static int ReadInt(object value, string name)
        {
            if (value == null)
                return int.MinValue;
            var type = value.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
                return Convert.ToInt32(property.GetValue(value));
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field == null ? int.MinValue : Convert.ToInt32(field.GetValue(value));
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
