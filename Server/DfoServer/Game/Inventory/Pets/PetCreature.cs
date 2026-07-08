using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        private const short PetCreatureEquipSlot = 24;
        private const short PetCreatureEquippedStorageSlot = PetCreatureEquipSlot + 216;
        private const int MaxPersistentPetCreatureSerial = 0x000FFFFF;
        private const short PetArtifactRedEquipSlot = 25;
        private const short PetArtifactBlueEquipSlot = 26;
        private const short PetArtifactGreenEquipSlot = 27;
        private const int MinCreatureExpireUnixTime = 946684800; // 2000-01-01; filters out pet serials stored in CreatureExtra.
        private const byte DefaultCreatureField1 = 133;
        private const byte DefaultCreatureField2 = 47;
        private const byte DefaultCreatureField3 = 143;
        private const byte DefaultCreatureField4 = 105;

        internal static bool IsPetServerStorageSlot(int slot)
        {
            return slot == PetCreatureEquippedStorageSlot
                || slot == ToPetEquipmentStorageSlot(PetArtifactRedEquipSlot)
                || slot == ToPetEquipmentStorageSlot(PetArtifactBlueEquipSlot)
                || slot == ToPetEquipmentStorageSlot(PetArtifactGreenEquipSlot);
        }

        private bool TryHandlePetCreatureEquipMove(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryMoveRequest request,
            ItemRecord source,
            ItemRecord destination,
            out InventoryMoveResult result)
        {
            result = null;

            if (request.SourceListType == InventoryListType.Pet
                && request.DestinationListType == InventoryListType.Equipment
                && request.DestinationSlotIndex == PetCreatureEquipSlot)
            {
                if (request.SourceInstanceValue == 0
                    && request.SourceSlotIndex >= PetInventorySlotStart
                    && request.SourceSlotIndex <= PetInventorySlotEnd)
                    return TryHandleReversePetCreatureUnequipMove(
                        connection,
                        transaction,
                        characterId,
                        request,
                        out result);

                if (source == null
                    || !string.Equals(source.ItemKind, "pet", StringComparison.Ordinal)
                    || source.SlotIndex < PetInventorySlotStart
                    || source.SlotIndex > PetInventorySlotEnd
                    || !IsCreatureItem(source.ItemTemplateId))
                    return false;

                if (!PetCreatureMoveValuesMatchSource(request, source))
                {
                    FileLogger.Log($"  [PetCreatureMove] EQUIP blocked: template mismatch slot={source.SlotIndex} srcIV=0x{request.SourceInstanceValue:X8} dstIV=0x{request.DestinationInstanceValue:X8} found=0x{source.ItemTemplateId:X8}");
                    return false;
                }

                var petSerial = EnsurePetCreaturePersistentSerial(connection, transaction, characterId, source);
                if (petSerial == 0)
                {
                    FileLogger.Log($"  [PetCreatureMove] EQUIP blocked: missing handle slot={source.SlotIndex} item=0x{source.ItemTemplateId:X8}");
                    return false;
                }

                var currentlyEquipped = LoadPetCreatureEquippedEntry(connection, transaction, characterId);
                _db.DeleteItem(connection, transaction, source.ItemUid);
                if (currentlyEquipped.HasValue)
                    InsertPetCreatureContainerItem(
                        connection,
                        transaction,
                        characterId,
                        source.SlotIndex,
                        currentlyEquipped.Value.ItemId,
                        currentlyEquipped.Value.Serial);

                UpsertPetCreatureRuntimeState(
                    connection,
                    transaction,
                    characterId,
                    source.ItemTemplateId,
                    petSerial);
                UpsertPetEquippedEntry(
                    connection,
                    transaction,
                    characterId,
                    PetCreatureEquipSlot,
                    source.ItemTemplateId,
                    petSerial,
                    source.ExpireTime,
                    source.ExpireTime);
                CleanupLegacyPetCreatureStorage(
                    connection,
                    transaction,
                    characterId,
                    source.ItemUid);

                _auditLogger.WriteAuditLog(connection, transaction, characterId, "move_pet_creature_equip", source, InventoryListType.Pet, source.SlotIndex, 1);
                FileLogger.Log($"  [PetCreatureMove] EQUIP: pet 0x{source.ItemTemplateId:X8} serial=0x{petSerial:X8} Pet slot {source.SlotIndex} -> equip slot {PetCreatureEquipSlot}");

                result = CreatePetCreatureMoveResult(request, request.MoveCount);
                result.PetCreatureStateChanged = true;
                result.PetItemStateChanged = true;
                AddPetCreatureRefreshSlot(result, source.SlotIndex);
                AddEquipmentRefreshSlot(result, PetCreatureEquipSlot);
                return true;
            }

            if (request.SourceListType == InventoryListType.Pet
                && request.DestinationListType == InventoryListType.Equipment
                && IsPetArtifactEquipSlot(request.DestinationSlotIndex))
            {
                if (request.SourceInstanceValue == 0
                    && request.SourceSlotIndex >= PetEquipmentSlotStart
                    && request.SourceSlotIndex <= PetEquipmentSlotEnd)
                    return TryHandleReversePetArtifactUnequipMove(
                        connection,
                        transaction,
                        characterId,
                        request,
                        out result);

                if (source == null
                    || !string.Equals(source.ItemKind, "pet", StringComparison.Ordinal)
                    || source.SlotIndex < PetEquipmentSlotStart
                    || source.SlotIndex > PetEquipmentSlotEnd
                    || !IsPetArtifactItem(source.ItemTemplateId))
                    return false;

                if (!PetArtifactMoveValuesMatchSource(request, source))
                {
                    FileLogger.Log($"  [PetArtifactMove] EQUIP blocked: template mismatch slot={source.SlotIndex} request=0x{request.SourceInstanceValue:X8} found=0x{source.ItemTemplateId:X8}");
                    result = CreatePetItemRefreshResult(request, request.MoveCount, false, source.SlotIndex, ToPetEquipmentStorageSlot(request.DestinationSlotIndex));
                    AddEquipmentRefreshSlot(result, request.DestinationSlotIndex);
                    return true;
                }

                var expectedSlot = ResolvePetArtifactEquipSlot(source.ItemTemplateId);
                if (expectedSlot < 0 || expectedSlot != request.DestinationSlotIndex)
                {
                    FileLogger.Log($"  [PetArtifactMove] EQUIP blocked: wrong equip slot dst={request.DestinationSlotIndex} expected={expectedSlot} tmpl=0x{source.ItemTemplateId:X8}");
                    result = CreatePetItemRefreshResult(request, request.MoveCount, false, source.SlotIndex, ToPetEquipmentStorageSlot(request.DestinationSlotIndex));
                    AddEquipmentRefreshSlot(result, request.DestinationSlotIndex);
                    return true;
                }

                var storageSlot = ToPetEquipmentStorageSlot(request.DestinationSlotIndex);
                var currentlyEquipped = LoadPetEquippedEntry(connection, transaction, characterId, request.DestinationSlotIndex);
                _db.DeleteItem(connection, transaction, source.ItemUid);
                DeleteSortItemLock(characterId, connection, transaction, InventoryListType.Pet, source.SlotIndex);

                if (currentlyEquipped.HasValue)
                {
                    InsertPetItemContainerItem(
                        connection,
                        transaction,
                        characterId,
                        source.SlotIndex,
                        currentlyEquipped.Value.ItemId,
                        currentlyEquipped.Value.Serial);
                    FileLogger.Log($"  [PetArtifactMove] REPLACE: old artifact 0x{currentlyEquipped.Value.ItemId:X8} -> Pet slot {source.SlotIndex}");
                }

                CleanupLegacyPetArtifactStorage(connection, transaction, characterId, storageSlot);

                _auditLogger.WriteAuditLog(connection, transaction, characterId, "move_pet_artifact_equip", source, InventoryListType.Pet, source.SlotIndex, 1);
                FileLogger.Log($"  [PetArtifactMove] EQUIP: artifact 0x{source.ItemTemplateId:X8} Pet slot {source.SlotIndex} -> equip slot {request.DestinationSlotIndex}");

                UpsertPetEquippedEntry(
                    connection,
                    transaction,
                    characterId,
                    request.DestinationSlotIndex,
                    source.ItemTemplateId,
                    ResolvePetSerial(request, source));

                result = CreatePetArtifactEquipResult(request, request.MoveCount, true, storageSlot, source.SlotIndex);
                AddEquipmentRefreshSlot(result, request.DestinationSlotIndex);
                return true;
            }

            if (request.SourceListType == InventoryListType.Equipment
                && IsPetArtifactEquipSlot(request.SourceSlotIndex)
                && request.DestinationListType == InventoryListType.Pet)
            {
                return TryHandlePetArtifactUnequipMove(
                    connection,
                    transaction,
                    characterId,
                    request,
                    request.DestinationSlotIndex,
                    out result);
            }

            if (request.SourceListType == InventoryListType.Equipment
                && request.SourceSlotIndex == PetCreatureEquipSlot
                && request.DestinationListType == InventoryListType.Pet)
            {
                var equipped = LoadPetCreatureEquippedEntry(connection, transaction, characterId);
                if (!equipped.HasValue)
                {
                    FileLogger.Log("  [PetCreatureMove] UNEQUIP: slot 24 already empty");
                    ClearPetEquippedEntry(connection, transaction, characterId, PetCreatureEquipSlot);
                    result = CreatePetCreatureMoveResult(
                        request,
                        request.MoveCount,
                        mutated: false);
                    result.PetCreatureStateChanged = true;
                    AddPetCreatureRefreshSlot(result, PetCreatureEquippedStorageSlot);
                    AddPetCreatureRefreshSlot(result, ResolvePetUnequipSlotForRefresh(request.DestinationSlotIndex));
                    AddEquipmentRefreshSlot(result, PetCreatureEquipSlot);
                    return true;
                }

                var targetSlot = ResolvePetUnequipSlot(connection, transaction, characterId, request.DestinationSlotIndex);
                if (targetSlot < 0)
                {
                    FileLogger.Log($"  [PetCreatureMove] UNEQUIP blocked: no empty pet slot for item=0x{equipped.Value.ItemId:X8}");
                    return false;
                }

                InsertPetCreatureContainerItem(
                    connection,
                    transaction,
                    characterId,
                    (short)targetSlot,
                    equipped.Value.ItemId,
                    equipped.Value.Serial);
                ClearPetCreatureRuntimeState(connection, transaction, characterId);
                CleanupLegacyPetCreatureStorage(connection, transaction, characterId, null);

                ClearPetEquippedEntry(connection, transaction, characterId, PetCreatureEquipSlot);
                result = CreatePetCreatureMoveResult(request, request.MoveCount);
                result.PetCreatureStateChanged = true;
                result.PetItemStateChanged = true;
                AddPetCreatureRefreshSlot(result, PetCreatureEquippedStorageSlot);
                AddPetCreatureRefreshSlot(result, (short)targetSlot);
                AddEquipmentRefreshSlot(result, PetCreatureEquipSlot);
                FileLogger.Log($"  [PetCreatureMove] UNEQUIP: pet 0x{equipped.Value.ItemId:X8} serial=0x{equipped.Value.Serial:X8} equip slot {PetCreatureEquipSlot} -> Pet slot {targetSlot}");
                return true;
            }

            return false;
        }

        private bool TryHandleReversePetCreatureUnequipMove(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryMoveRequest request,
            out InventoryMoveResult result)
        {
            result = null;

            var equipped = LoadPetCreatureEquippedEntry(connection, transaction, characterId);
            if (!equipped.HasValue)
            {
                FileLogger.Log($"  [PetCreatureMove] UNEQUIP(reverse): slot {PetCreatureEquipSlot} already empty");
                ClearPetCreatureRuntimeState(connection, transaction, characterId);
                result = CreatePetCreatureMoveResult(request, request.MoveCount);
                result.PetCreatureStateChanged = true;
                AddPetCreatureRefreshSlot(result, PetCreatureEquippedStorageSlot);
                AddPetCreatureRefreshSlot(result, request.SourceSlotIndex);
                AddEquipmentRefreshSlot(result, PetCreatureEquipSlot);
                return true;
            }

            var targetSlot = ResolvePetUnequipSlot(connection, transaction, characterId, request.SourceSlotIndex);
            if (targetSlot < 0)
            {
                FileLogger.Log($"  [PetCreatureMove] UNEQUIP(reverse) blocked: no empty pet slot for item=0x{equipped.Value.ItemId:X8}");
                return false;
            }

            InsertPetCreatureContainerItem(
                connection,
                transaction,
                characterId,
                (short)targetSlot,
                equipped.Value.ItemId,
                equipped.Value.Serial);
            ClearPetCreatureRuntimeState(connection, transaction, characterId);
            CleanupLegacyPetCreatureStorage(connection, transaction, characterId, null);
            ClearPetEquippedEntry(connection, transaction, characterId, PetCreatureEquipSlot);

            FileLogger.Log($"  [PetCreatureMove] UNEQUIP(reverse): pet 0x{equipped.Value.ItemId:X8} serial=0x{equipped.Value.Serial:X8} equip slot {PetCreatureEquipSlot} -> Pet slot {targetSlot}");

            result = CreatePetCreatureMoveResult(request, request.MoveCount);
            result.PetCreatureStateChanged = true;
            result.PetItemStateChanged = true;
            AddPetCreatureRefreshSlot(result, PetCreatureEquippedStorageSlot);
            AddPetCreatureRefreshSlot(result, targetSlot);
            AddEquipmentRefreshSlot(result, PetCreatureEquipSlot);
            return true;
        }

        private bool TryHandleReversePetArtifactUnequipMove(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryMoveRequest request,
            out InventoryMoveResult result)
        {
            return TryHandlePetArtifactUnequipMove(
                connection,
                transaction,
                characterId,
                request,
                request.SourceSlotIndex,
                out result);
        }

        private bool TryHandlePetArtifactUnequipMove(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryMoveRequest request,
            short requestedPetSlot,
            out InventoryMoveResult result)
        {
            result = null;

            var storageSlot = ToPetEquipmentStorageSlot(
                request.SourceListType == InventoryListType.Equipment
                    ? request.SourceSlotIndex
                    : request.DestinationSlotIndex);
            if (storageSlot < 0)
                return false;

            var equipped = LoadPetEquippedEntry(
                connection,
                transaction,
                characterId,
                request.SourceListType == InventoryListType.Equipment
                    ? request.SourceSlotIndex
                    : request.DestinationSlotIndex);
            if (!equipped.HasValue)
            {
                FileLogger.Log($"  [PetArtifactMove] UNEQUIP: equip slot already empty storage={storageSlot}");
                CleanupLegacyPetArtifactStorage(connection, transaction, characterId, storageSlot);
                result = CreatePetArtifactUnequipResult(
                    request,
                    request.MoveCount,
                    request.SourceListType == InventoryListType.Equipment
                        ? request.SourceSlotIndex
                        : request.DestinationSlotIndex,
                    requestedPetSlot,
                    true);
                AddEquipmentRefreshSlot(
                    result,
                    request.SourceListType == InventoryListType.Equipment
                        ? request.SourceSlotIndex
                        : request.DestinationSlotIndex);
                return true;
            }

            var targetSlot = ResolvePetArtifactUnequipSlot(connection, transaction, characterId, requestedPetSlot);
            if (targetSlot < 0)
            {
                FileLogger.Log($"  [PetArtifactMove] UNEQUIP blocked: no empty pet artifact slot for item=0x{equipped.Value.ItemId:X8}");
                return false;
            }

            InsertPetItemContainerItem(
                connection,
                transaction,
                characterId,
                (short)targetSlot,
                equipped.Value.ItemId,
                equipped.Value.Serial);
            CleanupLegacyPetArtifactStorage(connection, transaction, characterId, storageSlot);
            ClearPetEquippedEntry(
                connection,
                transaction,
                characterId,
                request.SourceListType == InventoryListType.Equipment
                    ? request.SourceSlotIndex
                    : request.DestinationSlotIndex);

            FileLogger.Log($"  [PetArtifactMove] UNEQUIP: artifact 0x{equipped.Value.ItemId:X8} equip slot -> Pet slot {targetSlot}");

            result = CreatePetArtifactUnequipResult(
                request,
                request.MoveCount,
                request.SourceListType == InventoryListType.Equipment
                    ? request.SourceSlotIndex
                    : request.DestinationSlotIndex,
                targetSlot,
                true);
            AddPetCreatureRefreshSlot(result, targetSlot);
            AddEquipmentRefreshSlot(
                result,
                request.SourceListType == InventoryListType.Equipment
                    ? request.SourceSlotIndex
                    : request.DestinationSlotIndex);
            return true;
        }

        private static InventoryMoveResult CreatePetArtifactEquipResult(InventoryMoveRequest request, int moveValue32, bool mutated = true, params int[] slots)
        {
            var result = CreateMoveResult(request, moveValue32, mutated);
            result.PetItemStateChanged = true;
            foreach (var slot in slots)
                AddPetCreatureRefreshSlot(result, slot);
            return result;
        }

        private static InventoryMoveResult CreatePetArtifactUnequipResult(
            InventoryMoveRequest request,
            int moveValue32,
            int equipSlot,
            int targetPetSlot,
            bool mutated = true)
        {
            var result = CreateMoveResult(request, moveValue32, mutated);
            result.PetItemStateChanged = true;
            return result;
        }

        private static InventoryMoveResult CreatePetItemRefreshResult(InventoryMoveRequest request, int moveValue32, bool mutated = true, params int[] slots)
        {
            var result = CreatePetInventoryAckResult(request, moveValue32, ResolvePetAckSlot(request), mutated);
            result.PetItemStateChanged = true;
            foreach (var slot in slots)
                AddPetCreatureRefreshSlot(result, slot);
            return result;
        }

        private static InventoryMoveResult CreatePetCreatureMoveResult(InventoryMoveRequest request, int moveValue32, bool mutated = true)
        {
            // Pet body moves must preserve the client's original Pet <-> Equipment
            // direction. Rewriting the ACK to Pet -> Pet makes unequip look like a
            // newly acquired creature entry on the client.
            return CreateMoveResult(request, moveValue32, mutated);
        }

        private static InventoryMoveResult CreatePetInventoryAckResult(InventoryMoveRequest request, int moveValue32, int ackSlot, bool mutated = true)
        {
            var result = CreateMoveResult(request, moveValue32, mutated);
            UsePetInventoryAck(result, ackSlot);
            return result;
        }

        private static void UsePetInventoryAck(InventoryMoveResult result, int ackSlot)
        {
            result.SourceListType = InventoryListType.Pet;
            result.DestinationListType = InventoryListType.Pet;
            if (ackSlot >= 0)
            {
                result.SourceSlotIndex = (short)ackSlot;
                result.DestinationSlotIndex = (short)ackSlot;
            }
        }

        private static int ResolvePetAckSlot(InventoryMoveRequest request)
        {
            if (request.SourceListType == InventoryListType.Pet)
                return request.SourceSlotIndex;
            if (request.DestinationListType == InventoryListType.Pet)
                return request.DestinationSlotIndex;
            return -1;
        }

        private static void AddPetCreatureRefreshSlot(InventoryMoveResult result, int slot)
        {
            if (result == null || slot < 0)
                return;
            if (IsPetServerStorageSlot(slot))
                return;

            var slot16 = (short)slot;
            if (!result.PetCreatureRefreshSlots.Contains(slot16))
                result.PetCreatureRefreshSlots.Add(slot16);
        }

        private static void AddEquipmentRefreshSlot(InventoryMoveResult result, int slot)
        {
            if (result == null || slot < 0)
                return;

            var slot16 = (short)slot;
            if (!result.EquipmentRefreshSlots.Contains(slot16))
                result.EquipmentRefreshSlots.Add(slot16);
        }

        private static int ResolvePetUnequipSlotForRefresh(short requestedSlot)
        {
            return requestedSlot >= PetInventorySlotStart && requestedSlot <= PetInventorySlotEnd
                ? requestedSlot
                : -1;
        }

        private int ResolvePetUnequipSlot(SqliteConnection connection, SqliteTransaction transaction, int characterId, short requestedSlot)
        {
            if (requestedSlot >= PetInventorySlotStart
                && requestedSlot <= PetInventorySlotEnd
                && IsPetInventorySlotEmpty(connection, transaction, characterId, requestedSlot))
                return requestedSlot;

            return _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Pet, PetInventorySlotStart, PetInventorySlotEnd);
        }

        private int ResolvePetArtifactUnequipSlot(SqliteConnection connection, SqliteTransaction transaction, int characterId, short requestedSlot)
        {
            if (requestedSlot >= PetEquipmentSlotStart
                && requestedSlot <= PetEquipmentSlotEnd
                && IsPetInventorySlotEmpty(connection, transaction, characterId, requestedSlot))
                return requestedSlot;

            return _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Pet, PetEquipmentSlotStart, PetEquipmentSlotEnd);
        }

        private static bool IsPetInventorySlotEmpty(SqliteConnection connection, SqliteTransaction transaction, int characterId, short slot)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT COUNT(1)
FROM character_items
WHERE character_id = @cid
  AND list_type = @lt
  AND slot_index = @slot;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lt", (int)InventoryListType.Pet);
                cmd.Parameters.AddWithValue("@slot", (int)slot);
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0;
            }
        }

        private static int ResolvePetSerial(InventoryMoveRequest request, ItemRecord source)
        {
            if (source.PetSerialOrHandle != 0)
                return source.PetSerialOrHandle;
            if (source.InstanceValue != 0)
                return source.InstanceValue;
            if (source.StackCount != 0)
                return source.StackCount;
            return unchecked((int)source.ItemUid);
        }

        private static bool PetCreatureMoveValuesMatchSource(InventoryMoveRequest request, ItemRecord source)
        {
            return request.SourceInstanceValue == source.ItemTemplateId
                || request.DestinationInstanceValue == source.ItemTemplateId
                || (source.PetSerialOrHandle != 0
                    && (request.SourceInstanceValue == source.PetSerialOrHandle
                        || request.DestinationInstanceValue == source.PetSerialOrHandle))
                || (source.PetSerialOrHandle == 0
                    && (request.SourceInstanceValue != 0 || request.DestinationInstanceValue != 0))
                || (request.SourceInstanceValue == 0 && request.DestinationInstanceValue == 0);
        }

        private int EnsurePetCreaturePersistentSerial(SqliteConnection connection, SqliteTransaction transaction, int characterId, ItemRecord source)
        {
            var serial = ResolvePetSerial(new InventoryMoveRequest(), source);
            if (IsPersistentPetCreatureSerial(serial) && !IsPetCreatureSerialUsedByOtherItem(connection, transaction, characterId, source.ItemUid, serial))
            {
                if (source.PetSerialOrHandle != serial)
                {
                    UpdatePetRecordHandle(connection, transaction, source.ItemUid, serial);
                    source.PetSerialOrHandle = serial;
                }
                return serial;
            }

            var nextSerial = LoadMaxPersistentPetCreatureSerial(connection, transaction, characterId);
            var used = LoadUsedPetCreatureSerials(connection, transaction, characterId);
            var oldSerial = serial;
            serial = NextPersistentPetCreatureSerial(used, ref nextSerial);
            UpdatePetRecordHandle(connection, transaction, source.ItemUid, serial);
            source.PetSerialOrHandle = serial;
            FileLogger.Log($"  [PetCreatureMove] repair serial: uid={source.ItemUid} slot={source.SlotIndex} item=0x{source.ItemTemplateId:X8} old=0x{oldSerial:X8} new=0x{serial:X8}");
            return serial;
        }

        private void RepairPetCreatureState(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            try
            {
                CleanupLegacyPetCreatureStatState(connection, transaction, characterId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [PetCreatureMove] legacy pet stat cleanup skipped: {ex.Message}");
            }

            RepairOutOfRangePetStorage(connection, transaction, characterId);

            var equippedEntry = LoadPetCreatureEquippedEntry(connection, transaction, characterId);
            if (equippedEntry.HasValue)
            {
                if (!IsCreatureItem(equippedEntry.Value.ItemId) || !IsPersistentPetCreatureSerial(equippedEntry.Value.Serial))
                {
                    FileLogger.Log($"  [PetCreatureMove] repair: clear invalid slot24 entry tmpl=0x{equippedEntry.Value.ItemId:X8} serial=0x{equippedEntry.Value.Serial:X8}");
                    ClearPetEquippedEntry(connection, transaction, characterId, PetCreatureEquipSlot);
                    ClearPetCreatureRuntimeState(connection, transaction, characterId);
                    CleanupLegacyPetCreatureStorage(connection, transaction, characterId, null);
                }
                else
                {
                    UpsertPetCreatureRuntimeState(
                        connection,
                        transaction,
                        characterId,
                        equippedEntry.Value.ItemId,
                        equippedEntry.Value.Serial);
                    UpsertPetEquippedEntry(
                        connection,
                        transaction,
                        characterId,
                        PetCreatureEquipSlot,
                        equippedEntry.Value.ItemId,
                        equippedEntry.Value.Serial,
                        NormalizePetCreatureExtra(equippedEntry.Value.ExpireTime, equippedEntry.Value.CreatureExtra),
                        equippedEntry.Value.ExpireTime);
                    CleanupLegacyPetCreatureStorage(connection, transaction, characterId, null);
                }
            }
            else
            {
                var equipped = LoadPetCreatureStorageRecord(connection, transaction, characterId);
                if (equipped == null)
                {
                    ClearPetCreatureRuntimeState(connection, transaction, characterId);
                }
                else if (!string.Equals(equipped.ItemKind, "pet", StringComparison.Ordinal)
                    || !IsCreatureItem(equipped.ItemTemplateId))
                {
                    FileLogger.Log($"  [PetCreatureMove] repair: move invalid equipped pet storage uid={equipped.ItemUid} kind={equipped.ItemKind} tmpl=0x{equipped.ItemTemplateId:X8} serial=0x{equipped.PetSerialOrHandle:X8}");
                    MoveLegacyPetCreatureStorageToVisibleSlot(connection, transaction, characterId, equipped, null);
                    ClearPetCreatureRuntimeState(connection, transaction, characterId);
                }
                else
                {
                    var petSerial = EnsurePetCreaturePersistentSerial(connection, transaction, characterId, equipped);
                    if (petSerial != 0)
                    {
                        UpsertPetCreatureRuntimeState(
                            connection,
                            transaction,
                            characterId,
                            equipped.ItemTemplateId,
                            petSerial);
                        UpsertPetEquippedEntry(
                            connection,
                            transaction,
                            characterId,
                            PetCreatureEquipSlot,
                            equipped.ItemTemplateId,
                            petSerial,
                            equipped.ExpireTime,
                            equipped.ExpireTime);
                        MoveLegacyPetCreatureStorageToVisibleSlot(connection, transaction, characterId, equipped, petSerial);
                    }
                }
            }

            RepairPetArtifactEntries(connection, transaction, characterId);
        }

        private void RepairPetArtifactEntries(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            foreach (var equipSlot in new[] { PetArtifactRedEquipSlot, PetArtifactBlueEquipSlot, PetArtifactGreenEquipSlot })
            {
                var storageSlot = ToPetEquipmentStorageSlot(equipSlot);
                var equipped = LoadPetEquippedEntry(connection, transaction, characterId, equipSlot);
                if (equipped.HasValue)
                {
                    var expectedSlot = ResolvePetArtifactEquipSlot(equipped.Value.ItemId);
                    if (expectedSlot != equipSlot)
                    {
                        FileLogger.Log($"  [PetArtifactMove] repair: clear invalid equip slot={equipSlot} item=0x{equipped.Value.ItemId:X8}");
                        ClearPetEquippedEntry(connection, transaction, characterId, equipSlot);
                    }

                    CleanupLegacyPetArtifactStorage(connection, transaction, characterId, storageSlot);
                    continue;
                }

                var storage = LoadPetArtifactStorageRecord(connection, transaction, characterId, storageSlot);
                if (storage == null)
                    continue;

                var storageExpectedSlot = ResolvePetArtifactEquipSlot(storage.ItemTemplateId);
                if (storageExpectedSlot < 0)
                {
                    RepairInvalidPetArtifactStorage(connection, transaction, characterId, storageSlot, storage);
                    continue;
                }

                if (storageExpectedSlot == equipSlot)
                {
                    var value = ResolvePetSerial(new InventoryMoveRequest(), storage);
                    UpsertPetEquippedEntry(
                        connection,
                        transaction,
                        characterId,
                        equipSlot,
                        storage.ItemTemplateId,
                        value);
                    _db.DeleteItem(connection, transaction, storage.ItemUid);
                    FileLogger.Log($"  [PetArtifactMove] repair: legacy storage {storageSlot} -> equip slot {equipSlot} item=0x{storage.ItemTemplateId:X8}");
                    continue;
                }

                CleanupLegacyPetArtifactStorage(connection, transaction, characterId, storageSlot);
            }
        }

        private void RepairOutOfRangePetStorage(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var items = LoadOutOfRangePetItems(connection, transaction, characterId);
            foreach (var item in items)
            {
                if (IsPetServerStorageSlot(item.SlotIndex))
                    continue;

                var equippedSlot = ResolveEquippedPetSlot(item.ItemTemplateId);
                if (equippedSlot >= 0)
                {
                    var equipped = LoadPetEquippedEntry(connection, transaction, characterId, equippedSlot);
                    if (equipped.HasValue && equipped.Value.ItemId == item.ItemTemplateId)
                    {
                        _db.DeleteItem(connection, transaction, item.ItemUid);
                        FileLogger.Log($"  [PetCreatureMove] repair: removed out-of-range duplicate uid={item.ItemUid} slot={item.SlotIndex} item=0x{item.ItemTemplateId:X8}");
                        continue;
                    }

                    var targetSlot = FindEmptyPetSlotForItem(connection, transaction, characterId, item.ItemTemplateId);
                    if (targetSlot >= 0)
                    {
                        _db.UpdateItemPosition(connection, transaction, item.ItemUid, InventoryListType.Pet, (short)targetSlot);
                        FileLogger.Log($"  [PetCreatureMove] repair: moved out-of-range pet uid={item.ItemUid} slot={item.SlotIndex} item=0x{item.ItemTemplateId:X8} -> slot {targetSlot}");
                    }
                    else
                    {
                        FileLogger.Log($"  [PetCreatureMove] repair: out-of-range pet kept, no empty visible slot uid={item.ItemUid} slot={item.SlotIndex} item=0x{item.ItemTemplateId:X8}");
                    }
                    continue;
                }

                FileLogger.Log($"  [PetCreatureMove] repair: ignored unknown out-of-range pet uid={item.ItemUid} slot={item.SlotIndex} item=0x{item.ItemTemplateId:X8}");
            }
        }

        private List<ItemRecord> LoadOutOfRangePetItems(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var items = new List<ItemRecord>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT item_uid, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json
FROM character_items
WHERE character_id = @cid
  AND list_type = @lt
  AND item_kind = 'pet'
  AND slot_index > @maxVisible
ORDER BY slot_index, item_uid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lt", (int)InventoryListType.Pet);
                cmd.Parameters.AddWithValue("@maxVisible", PetConsumableSlotEnd);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        items.Add(ReadItemRecord(reader));
                }
            }

            return items;
        }

        private int FindEmptyPetSlotForItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, int itemTemplateId)
        {
            return TryGetPetVisibleSlotRange(itemTemplateId, out var start, out var end)
                ? _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Pet, start, end)
                : -1;
        }

        private static bool TryGetPetVisibleSlotRange(int itemTemplateId, out int start, out int end)
        {
            if (IsCreatureItem(itemTemplateId))
            {
                start = PetInventorySlotStart;
                end = PetInventorySlotEnd;
                return true;
            }

            if (IsPetArtifactItem(itemTemplateId))
            {
                start = PetEquipmentSlotStart;
                end = PetEquipmentSlotEnd;
                return true;
            }

            try
            {
                if (ItemMetadataResolver.IsPetConsumableItem(ItemMetadataResolver.Resolve(itemTemplateId)))
                {
                    start = PetConsumableSlotStart;
                    end = PetConsumableSlotEnd;
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [PetCreatureMove] repair: pet item type fallback item=0x{itemTemplateId:X8}: {ex.Message}");
            }

            start = -1;
            end = -1;
            return false;
        }

        private static int ResolveEquippedPetSlot(int itemTemplateId)
        {
            if (IsCreatureItem(itemTemplateId))
                return PetCreatureEquipSlot;

            return ResolvePetArtifactEquipSlot(itemTemplateId);
        }

        private static HashSet<int> LoadUsedPetCreatureSerials(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var used = new HashSet<int>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT pet_serial_or_handle
FROM character_items
WHERE character_id = @cid
  AND list_type = @lt
  AND item_kind = 'pet'
  AND ((slot_index BETWEEN @start AND @end) OR slot_index = @equipped)
  AND pet_serial_or_handle BETWEEN 1 AND @max;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lt", (int)InventoryListType.Pet);
                cmd.Parameters.AddWithValue("@start", PetInventorySlotStart);
                cmd.Parameters.AddWithValue("@end", PetInventorySlotEnd);
                cmd.Parameters.AddWithValue("@equipped", (int)PetCreatureEquippedStorageSlot);
                cmd.Parameters.AddWithValue("@max", MaxPersistentPetCreatureSerial);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        used.Add(reader.GetInt32(0));
                }
            }

            var equipped = LoadPetCreatureEquippedEntry(connection, transaction, characterId);
            if (equipped.HasValue && IsPersistentPetCreatureSerial(equipped.Value.Serial))
                used.Add(equipped.Value.Serial);

            return used;
        }

        private static bool IsPetCreatureSerialUsedByOtherItem(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long itemUid,
            int serial)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT COUNT(1)
FROM character_items
WHERE character_id = @cid
  AND item_uid <> @uid
  AND list_type = @lt
  AND item_kind = 'pet'
  AND ((slot_index BETWEEN @start AND @end) OR slot_index = @equipped)
  AND pet_serial_or_handle = @serial;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@uid", itemUid);
                cmd.Parameters.AddWithValue("@lt", (int)InventoryListType.Pet);
                cmd.Parameters.AddWithValue("@start", PetInventorySlotStart);
                cmd.Parameters.AddWithValue("@end", PetInventorySlotEnd);
                cmd.Parameters.AddWithValue("@equipped", (int)PetCreatureEquippedStorageSlot);
                cmd.Parameters.AddWithValue("@serial", serial);
                if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                    return true;
            }

            var equipped = LoadPetCreatureEquippedEntry(connection, transaction, characterId);
            if (equipped.HasValue && equipped.Value.Serial == serial)
            {
                var sourceSerial = LoadPetCreatureSerialByUid(connection, transaction, itemUid);
                return sourceSerial != serial;
            }

            return false;
        }

        private static int LoadMaxPersistentPetCreatureSerial(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT MAX(serial) FROM (
    SELECT pet_serial_or_handle AS serial
    FROM character_items
    WHERE character_id = @cid
      AND list_type = @lt
      AND item_kind = 'pet'
      AND ((slot_index BETWEEN @start AND @end) OR slot_index = @equipped)
      AND pet_serial_or_handle BETWEEN 1 AND @max
    UNION ALL
    SELECT creature_key AS serial
    FROM character_creatures
    WHERE character_id = @cid
      AND creature_key BETWEEN 1 AND @max
);";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lt", (int)InventoryListType.Pet);
                cmd.Parameters.AddWithValue("@start", PetInventorySlotStart);
                cmd.Parameters.AddWithValue("@end", PetInventorySlotEnd);
                cmd.Parameters.AddWithValue("@equipped", (int)PetCreatureEquippedStorageSlot);
                cmd.Parameters.AddWithValue("@max", MaxPersistentPetCreatureSerial);
                var value = cmd.ExecuteScalar();
                var max = value == null || value == DBNull.Value
                    ? 0
                    : Convert.ToInt32(value, CultureInfo.InvariantCulture);

                var equipped = LoadPetCreatureEquippedEntry(connection, transaction, characterId);
                if (equipped.HasValue && IsPersistentPetCreatureSerial(equipped.Value.Serial))
                    max = Math.Max(max, equipped.Value.Serial);

                return max;
            }
        }

        private static int NextPersistentPetCreatureSerial(HashSet<int> seen, ref int nextSerial)
        {
            do
            {
                nextSerial++;
                if (nextSerial <= 0 || nextSerial > MaxPersistentPetCreatureSerial)
                    nextSerial = 1;
            }
            while (seen.Contains(nextSerial));

            return nextSerial;
        }

        private static bool IsPersistentPetCreatureSerial(int value)
        {
            return value > 0 && value <= MaxPersistentPetCreatureSerial;
        }

        private static int LoadPetCreatureSerialByUid(SqliteConnection connection, SqliteTransaction transaction, long itemUid)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT pet_serial_or_handle FROM character_items WHERE item_uid = @uid AND item_kind = 'pet' LIMIT 1;";
                cmd.Parameters.AddWithValue("@uid", itemUid);
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? 0
                    : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        private static PetCreatureEquippedEntry? LoadPetCreatureEquippedEntry(SqliteConnection connection, SqliteTransaction transaction, int characterId)
            => LoadPetEquippedEntry(connection, transaction, characterId, PetCreatureEquipSlot);

        private static PetCreatureEquippedEntry? LoadPetEquippedEntry(SqliteConnection connection, SqliteTransaction transaction, int characterId, int slot)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT item_id, raw_entry, expire_time
FROM character_equipped_entries
WHERE character_id = @cid AND slot = @slot
LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", slot);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    var itemId = reader.GetInt32(0);
                    var raw = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);
                    var expireTime = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    if (raw == null)
                        return new PetCreatureEquippedEntry(itemId, 0, expireTime, 0);

                    try
                    {
                        var parsed = InvenItem.Parse(raw);
                        return new PetCreatureEquippedEntry(
                            parsed.ItemId,
                            unchecked((int)parsed.Value),
                            expireTime,
                            unchecked((int)parsed.CreatureExtra));
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"  [PetCreatureMove] equip raw parse failed slot={slot} item=0x{itemId:X8}: {ex.Message}");
                        var serial = raw.Length >= 9 ? BitConverter.ToInt32(raw, 5) : 0;
                        return new PetCreatureEquippedEntry(itemId, serial, expireTime, 0);
                    }
                }
            }
        }

        private ItemRecord LoadPetCreatureItemBySerial(SqliteConnection connection, SqliteTransaction transaction, int characterId, int petSerial)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT item_uid, list_type, slot_index, item_template_id, item_kind, stack_count, instance_value,
       durability, seal_flag, option_value, expire_time, marker_16, pet_serial_or_handle, extra_json
FROM character_items
WHERE character_id = @cid
  AND list_type = @lt
  AND item_kind = 'pet'
  AND pet_serial_or_handle = @serial
ORDER BY CASE
    WHEN slot_index BETWEEN @start AND @end THEN 0
    WHEN slot_index = @equipped THEN 1
    ELSE 2
END, slot_index
LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lt", (int)InventoryListType.Pet);
                cmd.Parameters.AddWithValue("@serial", petSerial);
                cmd.Parameters.AddWithValue("@start", PetInventorySlotStart);
                cmd.Parameters.AddWithValue("@end", PetInventorySlotEnd);
                cmd.Parameters.AddWithValue("@equipped", (int)PetCreatureEquippedStorageSlot);
                using (var reader = cmd.ExecuteReader())
                {
                    return reader.Read() ? ReadItemRecord(reader) : null;
                }
            }
        }

        private void CleanupLegacyPetCreatureStorage(SqliteConnection connection, SqliteTransaction transaction, int characterId, long? keepItemUid)
        {
            var equipped = LoadPetCreatureStorageRecord(connection, transaction, characterId);
            if (equipped == null || (keepItemUid.HasValue && equipped.ItemUid == keepItemUid.Value))
                return;

            var visible = LoadPetCreatureItemBySerial(connection, transaction, characterId, equipped.PetSerialOrHandle);
            if (visible != null
                && visible.ItemUid != equipped.ItemUid
                && visible.SlotIndex >= PetInventorySlotStart
                && visible.SlotIndex <= PetInventorySlotEnd)
            {
                _db.DeleteItem(connection, transaction, equipped.ItemUid);
                FileLogger.Log($"  [PetCreatureMove] legacy slot240 duplicate removed: uid={equipped.ItemUid} item=0x{equipped.ItemTemplateId:X8} serial=0x{equipped.PetSerialOrHandle:X8}");
                return;
            }

            MoveLegacyPetCreatureStorageToVisibleSlot(connection, transaction, characterId, equipped, equipped.PetSerialOrHandle);
        }

        private void CleanupLegacyPetArtifactStorage(SqliteConnection connection, SqliteTransaction transaction, int characterId, int storageSlot)
        {
            var item = LoadPetArtifactStorageRecord(connection, transaction, characterId, storageSlot);
            if (item == null)
                return;

            if (!IsPetArtifactItem(item.ItemTemplateId))
            {
                RepairInvalidPetArtifactStorage(connection, transaction, characterId, storageSlot, item);
                return;
            }

            var equipSlot = storageSlot - 216;
            var equipped = IsPetArtifactEquipSlot(equipSlot)
                ? LoadPetEquippedEntry(connection, transaction, characterId, equipSlot)
                : null;
            if (equipped.HasValue && equipped.Value.ItemId == item.ItemTemplateId)
            {
                _db.DeleteItem(connection, transaction, item.ItemUid);
                FileLogger.Log($"  [PetArtifactMove] legacy storage duplicate removed: storage={storageSlot} item=0x{item.ItemTemplateId:X8}");
                return;
            }

            var targetSlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Pet, PetEquipmentSlotStart, PetEquipmentSlotEnd);
            if (targetSlot < 0)
            {
                FileLogger.Log($"  [PetArtifactMove] legacy storage cleanup skipped: no empty artifact slot storage={storageSlot} item=0x{item.ItemTemplateId:X8}");
                return;
            }

            _db.UpdateItemPosition(connection, transaction, item.ItemUid, InventoryListType.Pet, (short)targetSlot);
            FileLogger.Log($"  [PetArtifactMove] legacy storage cleanup: storage={storageSlot} item=0x{item.ItemTemplateId:X8} -> Pet slot {targetSlot}");
        }

        private void RepairInvalidPetArtifactStorage(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int storageSlot,
            ItemRecord item)
        {
            if (item == null)
                return;

            var targetSlot = FindEmptyPetSlotForItem(connection, transaction, characterId, item.ItemTemplateId);
            if (targetSlot < 0)
            {
                FileLogger.Log($"  [PetArtifactMove] legacy storage ignored: storage={storageSlot} item=0x{item.ItemTemplateId:X8} has no visible pet range");
                return;
            }

            _db.UpdateItemPosition(connection, transaction, item.ItemUid, InventoryListType.Pet, (short)targetSlot);
            FileLogger.Log($"  [PetArtifactMove] legacy storage repaired: storage={storageSlot} item=0x{item.ItemTemplateId:X8} -> Pet slot {targetSlot}");
        }

        private void MoveLegacyPetCreatureStorageToVisibleSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ItemRecord item,
            int? petSerial)
        {
            if (item == null || item.SlotIndex != PetCreatureEquippedStorageSlot)
                return;

            var targetSlot = FindVisiblePetSlotForLegacyStorage(connection, transaction, characterId, petSerial ?? item.PetSerialOrHandle);
            if (targetSlot < 0)
            {
                FileLogger.Log($"  [PetCreatureMove] legacy slot240 cleanup skipped: no empty pet slot uid={item.ItemUid} item=0x{item.ItemTemplateId:X8}");
                return;
            }

            _db.UpdateItemPosition(connection, transaction, item.ItemUid, InventoryListType.Pet, (short)targetSlot);
            FileLogger.Log($"  [PetCreatureMove] legacy slot240 cleanup: uid={item.ItemUid} item=0x{item.ItemTemplateId:X8} -> Pet slot {targetSlot}");
        }

        private int FindVisiblePetSlotForLegacyStorage(SqliteConnection connection, SqliteTransaction transaction, int characterId, int petSerial)
        {
            if (IsPersistentPetCreatureSerial(petSerial))
            {
                var visible = LoadPetCreatureItemBySerial(connection, transaction, characterId, petSerial);
                if (visible != null && visible.SlotIndex >= PetInventorySlotStart && visible.SlotIndex <= PetInventorySlotEnd)
                    return -1;
            }

            return _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Pet, PetInventorySlotStart, PetInventorySlotEnd);
        }

        private void InsertPetCreatureContainerItem(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short slot,
            int itemTemplateId,
            int petSerial)
        {
            _db.InsertCharacterItem(
                connection,
                transaction,
                characterId,
                InventoryListType.Pet,
                slot,
                itemTemplateId,
                "pet",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                petSerial,
                SqliteInventoryStore.CreateDefaultPetExtraJson());
        }

        private void InsertPetItemContainerItem(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short slot,
            int itemTemplateId,
            int value)
        {
            _db.InsertCharacterItem(
                connection,
                transaction,
                characterId,
                InventoryListType.Pet,
                slot,
                itemTemplateId,
                "pet",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                value,
                SqliteInventoryStore.CreateDefaultPetExtraJson());
        }

        private static void DeduplicateCreatureListEntries(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
DELETE FROM character_creatures
WHERE character_id = @cid
  AND rowid NOT IN (
      SELECT MIN(rowid)
      FROM character_creatures
      WHERE character_id = @cid
      GROUP BY creature_key
  );";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        private ItemRecord LoadPetCreatureStorageRecord(SqliteConnection connection, SqliteTransaction transaction, int characterId)
            => _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Pet, PetCreatureEquippedStorageSlot);

        private ItemRecord LoadPetArtifactStorageRecord(SqliteConnection connection, SqliteTransaction transaction, int characterId, int storageSlot)
            => _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Pet, (short)storageSlot);

        private readonly struct PetCreatureEquippedEntry
        {
            public PetCreatureEquippedEntry(int itemId, int serial, int expireTime, int creatureExtra)
            {
                ItemId = itemId;
                Serial = serial;
                ExpireTime = expireTime;
                CreatureExtra = creatureExtra;
            }

            public int ItemId { get; }

            public int Serial { get; }

            public int ExpireTime { get; }

            public int CreatureExtra { get; }
        }

        private static bool IsPetArtifactEquipSlot(int slot)
            => slot == PetArtifactRedEquipSlot || slot == PetArtifactBlueEquipSlot || slot == PetArtifactGreenEquipSlot;

        private static int ToPetEquipmentStorageSlot(int equipSlot)
            => IsPetArtifactEquipSlot(equipSlot) ? equipSlot + 216 : -1;

        private static bool IsPetArtifactItem(int itemTemplateId)
            => ResolvePetArtifactEquipSlot(itemTemplateId) >= 0;

        private static int ResolvePetArtifactEquipSlot(int itemTemplateId)
        {
            var equipmentType = EquipmentTypeInfo.ParseOrUnknown(ItemMetadataResolver.ResolveEquipmentType(itemTemplateId));
            switch (equipmentType)
            {
                case EquipmentType.ArtifactRed:
                    return PetArtifactRedEquipSlot;
                case EquipmentType.ArtifactBlue:
                    return PetArtifactBlueEquipSlot;
                case EquipmentType.ArtifactGreen:
                    return PetArtifactGreenEquipSlot;
                default:
                    return -1;
            }
        }

        private static void UpsertPetEquippedEntry(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int slot,
            int itemTemplateId,
            int value,
            int creatureExtra = 0,
            int expireTime = 0)
        {
            var raw = new InvenItem
            {
                Slot = (byte)slot,
                ItemId = itemTemplateId,
                Value = unchecked((uint)value),
                CreatureExtra = unchecked((uint)Math.Max(0, creatureExtra)),
            }.ToBytes();

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
INSERT INTO character_equipped_entries(character_id, slot, item_id, expire_time, raw_entry)
VALUES(@cid, @slot, @itemId, @expireTime, @raw)
ON CONFLICT(character_id, slot)
DO UPDATE SET item_id = @itemId,
              expire_time = @expireTime,
              raw_entry = @raw;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", slot);
                cmd.Parameters.AddWithValue("@itemId", itemTemplateId);
                cmd.Parameters.AddWithValue("@expireTime", Math.Max(0, expireTime));
                cmd.Parameters.AddWithValue("@raw", raw);
                cmd.ExecuteNonQuery();
            }
        }

        private static int NormalizePetCreatureExtra(int expireTime, int creatureExtra)
        {
            if (expireTime > 0)
                return expireTime;

            return creatureExtra >= MinCreatureExpireUnixTime ? creatureExtra : 0;
        }

        private static bool HasPetEquippedEntry(SqliteConnection connection, SqliteTransaction transaction, int characterId, int slot)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT COUNT(1) FROM character_equipped_entries WHERE character_id = @cid AND slot = @slot;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", slot);
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
        }

        private static void ClearPetEquippedEntry(SqliteConnection connection, SqliteTransaction transaction, int characterId, int slot)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM character_equipped_entries WHERE character_id = @cid AND slot = @slot;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", slot);
                cmd.ExecuteNonQuery();
            }
        }

        public CommonInventoryItem LoadEquipmentCommonItemForRefresh(int characterId, short slotIndex)
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                return LoadEquipmentCommonItemForRefresh(connection, transaction, characterId, slotIndex);
            }
        }

        private static CommonInventoryItem LoadEquipmentCommonItemForRefresh(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short slotIndex)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT item_id, raw_entry, expire_time, equipment_lock_id
FROM character_equipped_entries
WHERE character_id = @cid AND slot = @slot
LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", (int)slotIndex);

                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return BuildEquipmentCommonItem(
                        slotIndex,
                        reader.GetInt32(0),
                        (byte[])reader.GetValue(1),
                        reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        reader.IsDBNull(3) ? (byte)0 : Convert.ToByte(reader.GetInt32(3), CultureInfo.InvariantCulture));
                }
            }
        }

        private static CommonInventoryItem BuildEquipmentCommonItem(
            short slot,
            int itemId,
            byte[] raw,
            int expireTime,
            byte equipmentLockId)
        {
            var fields = raw != null && raw.Length >= 24
                ? MakeEquipListCodec.ParseDisplayFields(raw)
                : new MakeEquipListCodec.DisplayFields();

            var prefix = new byte[8];
            BitConverter.GetBytes(fields.Enchant).CopyTo(prefix, 0);
            prefix[4] = fields.EnchantUpgradeCount;
            prefix[5] = fields.AmplifyType;
            BitConverter.GetBytes(fields.AmplifyValue).CopyTo(prefix, 6);

            var tail = new byte[37];
            if (fields.Emblem != null && fields.Emblem.Length > 0)
                Buffer.BlockCopy(fields.Emblem, 0, tail, 0, Math.Min(fields.Emblem.Length, 9));
            BitConverter.GetBytes(fields.Rune).CopyTo(tail, 9);
            tail[27] = fields.Forging;
            if (fields.MagicSealCount > 0 && fields.MagicSealTypes != null)
            {
                tail[11] = fields.MagicSealCount;
                for (var index = 0; index < fields.MagicSealCount && index < 3; index++)
                {
                    tail[12 + index] = fields.MagicSealTypes[index];
                    tail[15 + index] = fields.MagicSealVal1s[index];
                    tail[18 + index] = fields.MagicSealVal2s[index];
                }

                if (fields.MagicSealTail != null)
                {
                    for (var index = 0; index < fields.MagicSealTail.Length && 21 + index < tail.Length; index++)
                        tail[21 + index] = fields.MagicSealTail[index];
                }
            }

            return new CommonInventoryItem
            {
                SlotIndex = slot,
                ItemTemplateId = itemId,
                CountOrInstanceValue = unchecked((int)fields.InstanceValue),
                ExtData0 = fields.Reinforce,
                Durability = fields.Durability,
                SealFlag = 0,
                PrefixData0E = prefix,
                Marker16 = -1,
                MiddleData1A = new byte[17],
                ExpireTime = expireTime,
                TailData2F = tail,
                JewelSocket = fields.JewelSocket ?? new byte[30],
                EquipmentLockId = equipmentLockId,
            };
        }

        private static AvatarInventoryItem LoadEquipmentItemForRefresh(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short slotIndex)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT item_id, raw_entry
FROM character_equipped_entries
WHERE character_id = @cid AND slot = @slot
LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", (int)slotIndex);

                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return TryBuildEquipmentItem(
                        slotIndex,
                        reader.GetInt32(0),
                        (byte[])reader.GetValue(1),
                        out var item)
                        ? item
                        : null;
                }
            }
        }

        private static bool TryBuildEquipmentItem(short slot, int itemId, byte[] raw, out AvatarInventoryItem item)
        {
            item = null;
            try
            {
                var parsed = InvenItem.Parse(raw);
                item = new AvatarInventoryItem
                {
                    SlotIndex = slot,
                    AvatarItemId = itemId,
                    Reserved0 = new byte[5],
                    OptionValue = (byte)Math.Min(parsed.Durability, byte.MaxValue),
                    Reserved1 = new byte[71],
                    UnknownFixed30 = DefaultAvatarUnknownFixed30,
                    Reserved2 = new byte[30],
                    UnknownFixed4 = DefaultAvatarUnknownFixed4,
                    TailData = new byte[7],
                };
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [PetCreatureMove] equipment refresh fallback slot={slot} item=0x{itemId:X8}: {ex.Message}");
                return false;
            }
        }

        private static void UpdatePetRecordHandle(SqliteConnection connection, SqliteTransaction transaction, long itemUid, int petHandle)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE character_items
SET pet_serial_or_handle = @handle,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @uid AND item_kind = 'pet';";
                cmd.Parameters.AddWithValue("@handle", petHandle);
                cmd.Parameters.AddWithValue("@uid", itemUid);
                cmd.ExecuteNonQuery();
            }
        }

        private static void UpsertPetCreatureRuntimeState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int itemTemplateId,
            int petSerial)
        {
            var defaults = ResolveCreatureDefaults(connection, transaction, characterId, petSerial, itemTemplateId);
            var runtime = LoadSubtype0PetRuntime(connection, transaction, characterId);
            runtime.SetPetItemId(itemTemplateId);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
INSERT INTO character_subtype0_fields(character_id, creature_buffer, stamina, fatigue_penalty, pet_display_flag)
VALUES(@cid, @buf, @stamina, @fatiguePenalty, 0)
ON CONFLICT(character_id)
DO UPDATE SET creature_buffer = @buf,
              stamina = @stamina,
              fatigue_penalty = @fatiguePenalty,
              pet_display_flag = 0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@buf", runtime.CreatureBuffer);
                cmd.Parameters.AddWithValue("@stamina", (int)runtime.Stamina);
                cmd.Parameters.AddWithValue("@fatiguePenalty", (long)runtime.FatiguePenalty);
                cmd.ExecuteNonQuery();
            }
            EnsureSubtype0CreatureDefaults(connection, transaction, characterId);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
INSERT INTO character_subtype1_fields(character_id, equipped_creature_level)
VALUES(@cid, @level)
ON CONFLICT(character_id)
DO UPDATE SET equipped_creature_level = @level;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@level", defaults.Level);
                cmd.ExecuteNonQuery();
            }

            EnsureCreatureListEntry(connection, transaction, characterId, petSerial, defaults);
        }

        private static void EnsureSubtype0CreatureDefaults(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE character_subtype0_fields
SET creature_field1 = @field1,
    creature_field2 = @field2,
    creature_field3 = @field3,
    creature_field4 = @field4
WHERE character_id = @cid
  AND creature_field1 = 0
  AND creature_field2 = 0
  AND creature_field3 = 0
  AND creature_field4 = 0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@field1", (int)DefaultCreatureField1);
                cmd.Parameters.AddWithValue("@field2", (int)DefaultCreatureField2);
                cmd.Parameters.AddWithValue("@field3", (int)DefaultCreatureField3);
                cmd.Parameters.AddWithValue("@field4", (int)DefaultCreatureField4);
                cmd.ExecuteNonQuery();
            }
        }

        private static void ClearPetCreatureRuntimeState(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            var runtime = LoadSubtype0PetRuntime(connection, transaction, characterId);
            runtime.SetPetItemId(0);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
INSERT INTO character_subtype0_fields(character_id, creature_buffer, stamina, fatigue_penalty, pet_display_flag)
VALUES(@cid, @buf, @stamina, @fatiguePenalty, 0)
ON CONFLICT(character_id)
DO UPDATE SET creature_buffer = @buf,
              stamina = @stamina,
              fatigue_penalty = @fatiguePenalty,
              pet_display_flag = 0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@buf", runtime.CreatureBuffer);
                cmd.Parameters.AddWithValue("@stamina", (int)runtime.Stamina);
                cmd.Parameters.AddWithValue("@fatiguePenalty", (long)runtime.FatiguePenalty);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
INSERT INTO character_subtype1_fields(character_id, equipped_creature_level)
VALUES(@cid, 0)
ON CONFLICT(character_id)
DO UPDATE SET equipped_creature_level = 0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }

        }

        private static Subtype0PetRuntime LoadSubtype0PetRuntime(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT creature_buffer, stamina, fatigue_penalty
FROM character_subtype0_fields
WHERE character_id = @cid
LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        var buffer = reader.IsDBNull(0) ? null : (byte[])reader.GetValue(0);
                        return new Subtype0PetRuntime(
                            buffer,
                            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                            reader.IsDBNull(2) ? 0u : (uint)reader.GetInt64(2));
                    }
                }
            }

            return new Subtype0PetRuntime(null, 0, 0);
        }

        private struct Subtype0PetRuntime
        {
            public Subtype0PetRuntime(byte[] creatureBuffer, int stamina, uint fatiguePenalty)
            {
                CreatureBuffer = creatureBuffer != null && creatureBuffer.Length == 8
                    ? (byte[])creatureBuffer.Clone()
                    : new byte[8];
                Stamina = (byte)Math.Max(0, Math.Min(byte.MaxValue, stamina));
                FatiguePenalty = fatiguePenalty;
            }

            public byte[] CreatureBuffer { get; }

            public byte Stamina { get; private set; }

            public uint FatiguePenalty { get; private set; }

            public void SetPetItemId(int itemTemplateId)
            {
                var tail = new byte[13];
                Buffer.BlockCopy(CreatureBuffer, 0, tail, 0, Math.Min(8, CreatureBuffer.Length));
                tail[8] = Stamina;
                Buffer.BlockCopy(BitConverter.GetBytes(FatiguePenalty), 0, tail, 9, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(itemTemplateId), 0, tail, 6, 4);
                Buffer.BlockCopy(tail, 0, CreatureBuffer, 0, 8);
                Stamina = tail[8];
                FatiguePenalty = BitConverter.ToUInt32(tail, 9);
            }
        }

        private static void EnsureCreatureListEntry(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int petHandle,
            CreatureDefaults defaults)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT COUNT(1)
FROM character_creatures
WHERE character_id = @cid AND creature_key = @key;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@key", petHandle);
                if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                    return;
            }

            int sortOrder;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT COALESCE(MAX(sort_order), -1) + 1
FROM character_creatures
WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                sortOrder = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
INSERT INTO character_creatures(
    character_id, sort_order, creature_key, field04, mode_flag, progress_value,
    mode1_field0a, mode1_field0b, field_after_value, creature_text, tail_flag)
VALUES(
    @cid, @ord, @key, 100, 0, 0,
    0, 0, @level, @text, 0);";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@ord", sortOrder);
                cmd.Parameters.AddWithValue("@key", petHandle);
                cmd.Parameters.AddWithValue("@level", defaults.Level);
                cmd.Parameters.AddWithValue("@text", DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        private static CreatureDefaults ResolveCreatureDefaults(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int petSerial,
            int itemTemplateId)
        {
            return TryLoadCreatureDefaults(connection, transaction, characterId, petSerial, out var defaults)
                ? defaults
                : ResolveCreatureDefaults(itemTemplateId);
        }

        private static bool TryLoadCreatureDefaults(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int petSerial,
            out CreatureDefaults defaults)
        {
            defaults = default;
            if (petSerial <= 0)
                return false;

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT field_after_value, creature_text
FROM character_creatures
WHERE character_id = @cid AND creature_key = @key
LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@key", petSerial);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    var level = reader.GetInt32(0);
                    var nameBytes = reader.IsDBNull(1)
                        ? Array.Empty<byte>()
                        : (byte[])reader.GetValue(1);
                    defaults = new CreatureDefaults(level, nameBytes);
                    return true;
                }
            }
        }

        private static CreatureDefaults ResolveCreatureDefaults(int itemTemplateId)
        {
            try
            {
                var entry = ItemMetadataResolver.GetEquipmentEntry(itemTemplateId);
                if (entry != null && !string.IsNullOrWhiteSpace(entry.FilePath))
                {
                    var equipment = EquipmentFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("equipment", entry.FilePath)));
                    var level = equipment.MinimumLevel > 0 ? equipment.MinimumLevel : 1;
                    return new CreatureDefaults(level, Array.Empty<byte>());
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [PetCreatureMove] creature defaults fallback item=0x{itemTemplateId:X8}: {ex.Message}");
            }

            return new CreatureDefaults(1, Array.Empty<byte>());
        }

        private static void CleanupLegacyPetCreatureStatState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            if (!LegacyPetCreatureStatStateExists(connection, transaction))
                return;

            int[] stats;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT stat_hp_max, stat_mp_max, stat_physical_attack, stat_physical_defense,
       stat_magical_attack, stat_magical_defense, stat_fire_resistance, stat_water_resistance,
       stat_dark_resistance, stat_light_resistance, stat_move_speed, stat_attack_speed,
       stat_cast_speed, stat_hit_recovery, stat_jump_power
FROM character_pet_stat_state
WHERE character_id = @cid
LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return;

                    stats = new int[15];
                    for (var i = 0; i < stats.Length; i++)
                        stats[i] = Convert.ToInt32(reader.GetValue(i), CultureInfo.InvariantCulture);
                }
            }

            var hasDelta = false;
            foreach (var value in stats)
                hasDelta = hasDelta || value != 0;

            if (hasDelta)
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
UPDATE character_subtype1_fields
SET stat_hp_max = MAX(0, stat_hp_max - @hp),
    stat_mp_max = MAX(0, stat_mp_max - @mp),
    stat_physical_attack = stat_physical_attack - @pa,
    stat_physical_defense = stat_physical_defense - @pd,
    stat_magical_attack = stat_magical_attack - @ma,
    stat_magical_defense = stat_magical_defense - @md,
    stat_fire_resistance = stat_fire_resistance - @fr,
    stat_water_resistance = stat_water_resistance - @wr,
    stat_dark_resistance = stat_dark_resistance - @dr,
    stat_light_resistance = stat_light_resistance - @lr,
    stat_move_speed = MAX(0, stat_move_speed - @ms),
    stat_attack_speed = MAX(0, stat_attack_speed - @as),
    stat_cast_speed = MAX(0, stat_cast_speed - @cs),
    stat_hit_recovery = MAX(0, stat_hit_recovery - @hr),
    stat_jump_power = MAX(0, stat_jump_power - @jp)
WHERE character_id = @cid;";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@hp", stats[0]);
                    cmd.Parameters.AddWithValue("@mp", stats[1]);
                    cmd.Parameters.AddWithValue("@pa", stats[2]);
                    cmd.Parameters.AddWithValue("@pd", stats[3]);
                    cmd.Parameters.AddWithValue("@ma", stats[4]);
                    cmd.Parameters.AddWithValue("@md", stats[5]);
                    cmd.Parameters.AddWithValue("@fr", stats[6]);
                    cmd.Parameters.AddWithValue("@wr", stats[7]);
                    cmd.Parameters.AddWithValue("@dr", stats[8]);
                    cmd.Parameters.AddWithValue("@lr", stats[9]);
                    cmd.Parameters.AddWithValue("@ms", stats[10]);
                    cmd.Parameters.AddWithValue("@as", stats[11]);
                    cmd.Parameters.AddWithValue("@cs", stats[12]);
                    cmd.Parameters.AddWithValue("@hr", stats[13]);
                    cmd.Parameters.AddWithValue("@jp", stats[14]);
                    cmd.ExecuteNonQuery();
                }

                FileLogger.Log($"  [PetCreatureMove] legacy pet stat state reverted for character={characterId}");
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM character_pet_stat_state WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        private static bool LegacyPetCreatureStatStateExists(SqliteConnection connection, SqliteTransaction transaction)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'character_pet_stat_state';";
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
        }

        private readonly struct CreatureDefaults
        {
            public CreatureDefaults(int level, byte[] nameBytes)
            {
                Level = Math.Max(1, Math.Min(255, level));
                NameBytes = nameBytes ?? Array.Empty<byte>();
            }

            public int Level { get; }

            public byte[] NameBytes { get; }
        }
    }
}
