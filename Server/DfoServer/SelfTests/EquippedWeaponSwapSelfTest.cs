using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class EquippedWeaponSwapSelfTest
    {
        private const int AccountId = 163023;
        private const int CharacterId = 163023;
        private const int FemaleSlayerJob = 11;
        private const int AwakenedVagabondGrowType = 4;
        private const int PrimaryWeaponItemId = 0x0021EFEA;
        private const int SupportWeaponItemId = 0x05FAEB78;
        private const byte PrimaryForging = 4;
        private const byte SupportForging = 9;

        public static int Run()
        {
            Console.WriteLine("=== EQUIPPED_WEAPON_SWAP selftest ===");
            var checks = 0;
            var failures = 0;
            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var databasePath = Path.Combine(tempDir, "equipped-weapon-swap.db");
            DeleteTempDatabase(databasePath);

            var store = new SqliteInventoryStore(databasePath, ServerPaths.SchemaFilePath);
            Seed(databasePath);

            Check("both fixtures resolve as PVF weapons",
                EquipmentTypeInfo.IsWeapon(EquipmentTypeInfo.ParseOrUnknown(ItemMetadataResolver.ResolveEquipmentType(PrimaryWeaponItemId)))
                && EquipmentTypeInfo.IsWeapon(EquipmentTypeInfo.ParseOrUnknown(ItemMetadataResolver.ResolveEquipmentType(SupportWeaponItemId))),
                ref checks,
                ref failures);

            Check("awakened female slayer can swap slot 11 and slot 23",
                TrySwap(store, (short)EquipmentType.Weapon, PrimaryWeaponItemId,
                    (short)EquipmentType.SupportWeapon, SupportWeaponItemId, out var firstResult)
                && firstResult != null
                && firstResult.Mutated
                && !firstResult.AckError,
                ref checks, ref failures);
            Check("swap reports primary weapon appearance and forging refresh",
                firstResult?.AffectedEquipmentSlot == (short)EquipmentType.Weapon
                && firstResult.Subtype0TailMutation?.ForgingChanged == true
                && firstResult.Subtype0TailMutation.Forging == SupportForging,
                ref checks, ref failures);

            var swappedPrimary = LoadEquipped(databasePath, (short)EquipmentType.Weapon);
            var swappedSupport = LoadEquipped(databasePath, (short)EquipmentType.SupportWeapon);
            Check("support weapon becomes the primary weapon",
                swappedPrimary.ItemId == SupportWeaponItemId
                && swappedPrimary.Raw?[0] == (byte)EquipmentType.Weapon,
                ref checks, ref failures);
            Check("primary weapon becomes the support weapon",
                swappedSupport.ItemId == PrimaryWeaponItemId
                && swappedSupport.Raw?[0] == (byte)EquipmentType.SupportWeapon,
                ref checks, ref failures);
            Check("both weapon instance attributes remain intact",
                FieldsMatch(swappedPrimary.Raw, 0x22222222u, 8, 33, SupportForging)
                && swappedPrimary.ExpireTime == 202
                && swappedPrimary.EquipmentLockId == 2
                && FieldsMatch(swappedSupport.Raw, 0x11111111u, 3, 48, PrimaryForging)
                && swappedSupport.ExpireTime == 101
                && swappedSupport.EquipmentLockId == 1,
                ref checks, ref failures);

            Check("mismatched request item id is rejected",
                !TrySwap(store, (short)EquipmentType.Weapon, PrimaryWeaponItemId,
                    (short)EquipmentType.SupportWeapon, PrimaryWeaponItemId, out var rejectedResult)
                && rejectedResult == null,
                ref checks, ref failures);
            Check("rejected swap leaves equipped state unchanged",
                LoadEquipped(databasePath, (short)EquipmentType.Weapon).ItemId == SupportWeaponItemId
                && LoadEquipped(databasePath, (short)EquipmentType.SupportWeapon).ItemId == PrimaryWeaponItemId,
                ref checks, ref failures);

            Check("slot 23 to slot 11 reverse swap succeeds",
                TrySwap(store, (short)EquipmentType.SupportWeapon, PrimaryWeaponItemId,
                    (short)EquipmentType.Weapon, SupportWeaponItemId, out var reverseResult)
                && reverseResult?.Subtype0TailMutation?.Forging == PrimaryForging,
                ref checks, ref failures);
            Check("reverse swap restores original equipped state",
                LoadEquipped(databasePath, (short)EquipmentType.Weapon).ItemId == PrimaryWeaponItemId
                && LoadEquipped(databasePath, (short)EquipmentType.SupportWeapon).ItemId == SupportWeaponItemId,
                ref checks, ref failures);

            SetCharacterJob(databasePath, 0);
            Check("non-female-slayer request is rejected without mutation",
                !TrySwap(store, (short)EquipmentType.Weapon, PrimaryWeaponItemId,
                    (short)EquipmentType.SupportWeapon, SupportWeaponItemId, out var wrongJobResult)
                && wrongJobResult == null
                && LoadEquipped(databasePath, (short)EquipmentType.Weapon).ItemId == PrimaryWeaponItemId
                && LoadEquipped(databasePath, (short)EquipmentType.SupportWeapon).ItemId == SupportWeaponItemId,
                ref checks,
                ref failures);

            DeleteTempDatabase(databasePath);
            Console.WriteLine($"=== EQUIPPED_WEAPON_SWAP selftest result: pass={checks - failures}, fail={failures} ===");
            return failures == 0 ? 0 : 1;
        }

        private static bool TrySwap(
            SqliteInventoryStore store,
            short sourceSlot,
            int sourceItemId,
            short destinationSlot,
            int destinationItemId,
            out InventoryMoveResult result)
        {
            return store.TryMoveItem(CharacterId, AccountId, new InventoryMoveRequest
            {
                SourceListType = InventoryListType.Equipment,
                SourceSlotIndex = sourceSlot,
                SourceInstanceValue = sourceItemId,
                MoveCount = 0,
                DestinationListType = InventoryListType.Equipment,
                DestinationSlotIndex = destinationSlot,
                DestinationInstanceValue = destinationItemId,
            }, out result);
        }

        private static bool FieldsMatch(byte[] raw, uint instanceValue, byte reinforce, ushort durability, byte forging)
        {
            if (raw == null)
                return false;

            var fields = MakeEquipListCodec.ParseDisplayFields(raw);
            return fields.InstanceValue == instanceValue
                && fields.Reinforce == reinforce
                && fields.Durability == durability
                && fields.Forging == forging;
        }

        private static void Seed(string databasePath)
        {
            var primaryRaw = MakeEquipListCodec.BuildEntryFromDisplayFields(
                (short)EquipmentType.Weapon,
                PrimaryWeaponItemId,
                new MakeEquipListCodec.DisplayFields
                {
                    InstanceValue = 0x11111111u,
                    Reinforce = 3,
                    Durability = 48,
                    Forging = PrimaryForging,
                });
            var supportRaw = MakeEquipListCodec.BuildEntryFromDisplayFields(
                (short)EquipmentType.SupportWeapon,
                SupportWeaponItemId,
                new MakeEquipListCodec.DisplayFields
                {
                    InstanceValue = 0x22222222u,
                    Reinforce = 8,
                    Durability = 33,
                    Forging = SupportForging,
                });

            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@accountId, 'equipped-weapon-swap-selftest', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name, job, grow_type)
VALUES (@characterId, @accountId, 'equipped-weapon-swap-selftest', @job, @growType);
INSERT OR REPLACE INTO character_equipped_entries
    (character_id, slot, item_id, expire_time, equipment_lock_id, raw_entry)
VALUES
    (@characterId, @primarySlot, @primaryItemId, 101, 1, @primaryRaw),
    (@characterId, @supportSlot, @supportItemId, 202, 2, @supportRaw);";
                    command.Parameters.AddWithValue("@accountId", AccountId);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@job", FemaleSlayerJob);
                    command.Parameters.AddWithValue("@growType", AwakenedVagabondGrowType);
                    command.Parameters.AddWithValue("@primarySlot", (short)EquipmentType.Weapon);
                    command.Parameters.AddWithValue("@supportSlot", (short)EquipmentType.SupportWeapon);
                    command.Parameters.AddWithValue("@primaryItemId", PrimaryWeaponItemId);
                    command.Parameters.AddWithValue("@supportItemId", SupportWeaponItemId);
                    command.Parameters.AddWithValue("@primaryRaw", primaryRaw);
                    command.Parameters.AddWithValue("@supportRaw", supportRaw);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SetCharacterJob(string databasePath, int job)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE characters SET job=@job WHERE character_id=@characterId;";
                    command.Parameters.AddWithValue("@job", job);
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static EquippedRow LoadEquipped(string databasePath, short slot)
        {
            using (var connection = new SqliteConnection(SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT item_id, expire_time, equipment_lock_id, raw_entry
FROM character_equipped_entries
WHERE character_id=@characterId AND slot=@slot;";
                    command.Parameters.AddWithValue("@characterId", CharacterId);
                    command.Parameters.AddWithValue("@slot", slot);
                    using (var reader = command.ExecuteReader())
                    {
                        return reader.Read()
                            ? new EquippedRow
                            {
                                ItemId = reader.GetInt32(0),
                                ExpireTime = reader.GetInt32(1),
                                EquipmentLockId = Convert.ToByte(reader.GetInt32(2)),
                                Raw = (byte[])reader.GetValue(3),
                            }
                            : new EquippedRow();
                    }
                }
            }
        }

        private static void Check(string label, bool ok, ref int checks, ref int failures)
        {
            checks++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok)
                failures++;
        }

        private static void DeleteTempDatabase(string path)
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                try { if (File.Exists(candidate)) File.Delete(candidate); }
                catch { }
            }
        }

        private sealed class EquippedRow
        {
            public int ItemId { get; set; }
            public int ExpireTime { get; set; }
            public byte EquipmentLockId { get; set; }
            public byte[] Raw { get; set; }
        }
    }
}
