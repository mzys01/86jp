using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace DfoServer.Game.Inventory
{
    public sealed class PetCreatureSealRequest
    {
        public InventoryListType CapsuleListType { get; set; } = InventoryListType.Main;

        public short CapsuleSlotIndex { get; set; }

        public int ExpectedCreatureItemTemplateId { get; set; }

        public short CreatureSlotIndex { get; set; }
    }

    public sealed class PetCreatureSealResult
    {
        public byte ErrorCode { get; set; } = PetSealCapsule.InvalidItemErrorCode;

        public short CapsuleSlotIndex { get; set; }

        public int CapsuleItemTemplateId { get; set; }

        public int SealedItemTemplateId { get; set; }

        public short SealedCapsuleSlotIndex { get; set; }

        public int CreatureItemTemplateId { get; set; }

        public int SealedCreatureItemTemplateId { get; set; }

        public bool SourceWasCreatureEgg { get; set; }

        public short CreatureSlotIndex { get; set; }

        public int CreatureSerialOrHandle { get; set; }

        public bool SplitFromStack { get; set; }

        public bool ReplacedCapsuleSlotWithSealedProduct { get; set; }

        public byte CreatureSealRemainUseCountBefore { get; set; }

        public byte CreatureSealRemainUseCountAfter { get; set; }

        public bool HadCreatureSealRemainUseCount { get; set; }

        public IReadOnlyList<short> MainRefreshSlots { get; set; } = Array.Empty<short>();
    }

    internal static class PetSealCapsule
    {
        public const byte MissingCapsuleErrorCode = 4;
        public const byte InvalidItemErrorCode = 17;
        public const byte LimitReachedErrorCode = 18;
        public const byte MaterialNotEnoughErrorCode = 22;
    }

    public sealed partial class SqliteInventoryStore
    {
        private const byte DefaultPetSealCapsuleRemainUseCount = 0;
        private const byte SealedProductInitialTradeLimitCount = 1;
        private const byte OpenedPetSealCapsuleSatiety = 100;
        private const byte CommonAttrLowBitsMask = 0x1F;
        private const byte CommonAttrTradeLimitShift = 5;
        private const string SealedTradeLimitCountProperty = "sealedTradeLimitCount";

        private static readonly Lazy<PetSealCapsuleCatalog> PetSealCapsuleCatalogCache =
            new Lazy<PetSealCapsuleCatalog>(PetSealCapsuleCatalog.Load);

        public bool TrySealPetCreature(
            int characterId,
            int accountId,
            PetCreatureSealRequest request,
            out PetCreatureSealResult result)
        {
            result = null;
            if (!IsValidPetCreatureSealRequest(request))
                return FailSealPetCreature(out result, PetSealCapsule.InvalidItemErrorCode);

            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var capsule = _db.LoadItemRecord(
                        connection,
                        transaction,
                        characterId,
                        InventoryListType.Main,
                        request.CapsuleSlotIndex);
                    if (capsule == null
                        || !string.Equals(capsule.ItemKind, "stackable", StringComparison.Ordinal)
                        || !IsPetSealCapsuleItem(capsule.ItemTemplateId))
                    {
                        FileLogger.Log($"  [PetSealCapsule] seal failed: missing capsule cid={characterId} slot={request.CapsuleSlotIndex} item=0x{capsule?.ItemTemplateId ?? 0:X8}");
                        return FailSealPetCreature(out result, PetSealCapsule.MissingCapsuleErrorCode);
                    }

                    var capsuleCount = GetStackedRecordCount(capsule);
                    if (capsuleCount <= 0)
                    {
                        FileLogger.Log($"  [PetSealCapsule] seal failed: empty capsule cid={characterId} slot={capsule.SlotIndex}");
                        return FailSealPetCreature(out result, PetSealCapsule.MaterialNotEnoughErrorCode);
                    }

                    var creature = _db.LoadItemRecord(
                        connection,
                        transaction,
                        characterId,
                        InventoryListType.Pet,
                        request.CreatureSlotIndex);
                    if (creature == null
                        || !string.Equals(creature.ItemKind, "pet", StringComparison.Ordinal)
                        || !IsCreatureItem(creature.ItemTemplateId))
                    {
                        FileLogger.Log($"  [PetSealCapsule] seal failed: missing creature cid={characterId} slot={request.CreatureSlotIndex}");
                        return FailSealPetCreature(out result, PetSealCapsule.InvalidItemErrorCode);
                    }

                    if (request.ExpectedCreatureItemTemplateId > 0
                        && creature.ItemTemplateId != request.ExpectedCreatureItemTemplateId)
                    {
                        FileLogger.Log(
                            $"  [PetSealCapsule] seal failed: creature mismatch cid={characterId} " +
                            $"expected=0x{request.ExpectedCreatureItemTemplateId:X8} found=0x{creature.ItemTemplateId:X8}");
                        return FailSealPetCreature(out result, PetSealCapsule.InvalidItemErrorCode);
                    }

                    var creatureSerial = EnsurePetCreaturePersistentSerial(connection, transaction, characterId, creature);
                    if (creatureSerial <= 0)
                    {
                        FileLogger.Log($"  [PetSealCapsule] seal failed: missing serial cid={characterId} item=0x{creature.ItemTemplateId:X8}");
                        return FailSealPetCreature(out result, PetSealCapsule.InvalidItemErrorCode);
                    }

                    var sealedCreatureItemId = ResolveSealedCreatureItemTemplateId(creature.ItemTemplateId, out var sourceWasCreatureEgg);
                    if (sealedCreatureItemId <= 0 || !IsCreatureItem(sealedCreatureItemId))
                    {
                        FileLogger.Log($"  [PetSealCapsule] seal failed: unresolved sealed creature source=0x{creature.ItemTemplateId:X8} resolved=0x{sealedCreatureItemId:X8}");
                        return FailSealPetCreature(out result, PetSealCapsule.InvalidItemErrorCode);
                    }

                    creature.ExtraJson = ResolvePetCreatureInstanceExtraJson(
                        connection,
                        transaction,
                        characterId,
                        creatureSerial,
                        creature.ExtraJson);
                    var hadRemainUseCount = TryResolvePetCreatureSealRemainUseCount(
                        creature.ExtraJson,
                        out var remainUseCountBefore);
                    if (hadRemainUseCount && remainUseCountBefore <= 0)
                    {
                        FileLogger.Log($"  [PetSealCapsule] seal failed: remain exhausted cid={characterId} serial=0x{creatureSerial:X8}");
                        return FailSealPetCreature(out result, PetSealCapsule.LimitReachedErrorCode);
                    }

                    var remainUseCountAfter = hadRemainUseCount
                        ? PetCreatureExtraView.ClampByte(remainUseCountBefore - 1)
                        : DefaultPetSealCapsuleRemainUseCount;
                    var initializedCreatureExtra = BuildInitializedPetCreatureSealExtraJson(remainUseCountAfter);
                    var sealedItemId = ResolveSealedPetCreatureProductItemId(sealedCreatureItemId);
                    var sealedMetadata = ItemMetadataResolver.Resolve(sealedItemId);
                    var sealedExtraJson = BuildSealedPetCreatureCapsuleExtraJson(
                        capsule,
                        creature,
                        creatureSerial,
                        sealedCreatureItemId,
                        sealedItemId,
                        initializedCreatureExtra,
                        remainUseCountAfter);

                    UpsertPetCreatureExtraJson(connection, transaction, characterId, creatureSerial, initializedCreatureExtra);
                    ResetPetCreatureSealedState(connection, transaction, characterId, creatureSerial, initializedCreatureExtra);

                    _db.DeleteItem(connection, transaction, creature.ItemUid);
                    DeleteSortItemLock(characterId, connection, transaction, InventoryListType.Pet, creature.SlotIndex);

                    var refreshSlots = new List<short> { capsule.SlotIndex };
                    var splitFromStack = capsuleCount > 1;
                    short sealedSlot;
                    if (splitFromStack)
                    {
                        _db.UpdateStackCount(connection, transaction, capsule.ItemUid, capsuleCount - 1);
                        sealedSlot = FindSealedPetProductSlot(
                            connection,
                            transaction,
                            characterId,
                            sealedMetadata,
                            capsule.SlotIndex);
                        if (sealedSlot < 0)
                            return FailSealPetCreature(out result, PetSealCapsule.InvalidItemErrorCode);

                        InsertSealedPetProduct(
                            connection,
                            transaction,
                            characterId,
                            sealedSlot,
                            sealedItemId,
                            sealedMetadata,
                            creatureSerial,
                            sealedExtraJson);
                        refreshSlots.Add(sealedSlot);
                    }
                    else
                    {
                        sealedSlot = capsule.SlotIndex;
                        UpdateSealedPetProduct(
                            connection,
                            transaction,
                            capsule.ItemUid,
                            sealedSlot,
                            sealedItemId,
                            sealedMetadata,
                            creatureSerial,
                            sealedExtraJson);
                    }

                    _auditLogger.WriteAuditLog(
                        connection,
                        transaction,
                        characterId,
                        "seal_pet_creature",
                        creature,
                        InventoryListType.Main,
                        sealedSlot,
                        1);
                    transaction.Commit();

                        result = new PetCreatureSealResult
                    {
                        CapsuleSlotIndex = capsule.SlotIndex,
                        CapsuleItemTemplateId = capsule.ItemTemplateId,
                        SealedItemTemplateId = sealedItemId,
                        SealedCapsuleSlotIndex = sealedSlot,
                        CreatureItemTemplateId = creature.ItemTemplateId,
                        SealedCreatureItemTemplateId = sealedCreatureItemId,
                        SourceWasCreatureEgg = sourceWasCreatureEgg,
                        CreatureSlotIndex = creature.SlotIndex,
                        CreatureSerialOrHandle = creatureSerial,
                        SplitFromStack = splitFromStack,
                        ReplacedCapsuleSlotWithSealedProduct = !splitFromStack,
                        HadCreatureSealRemainUseCount = hadRemainUseCount,
                        CreatureSealRemainUseCountBefore = remainUseCountBefore,
                        CreatureSealRemainUseCountAfter = remainUseCountAfter,
                        MainRefreshSlots = refreshSlots,
                    };
                    return true;
                }
            }
        }

        public bool TryOpenSealedPetCreatureCapsule(
            int characterId,
            int accountId,
            short slotIndex,
            out BoosterUseResult result)
        {
            result = null;
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var source = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, slotIndex);
                    if (source == null || !TryReadSealedPetCreaturePayload(source, out var payload))
                        return false;

                    if (!IsCreatureItem(payload.CreatureItemTemplateId))
                    {
                        FileLogger.Log($"  [PetSealCapsule] open failed: invalid creature item=0x{payload.CreatureItemTemplateId:X8}");
                        return false;
                    }

                    var targetSlot = _db.FindEmptyPetCreatureInventorySlot(
                        connection,
                        transaction,
                        characterId);
                    if (targetSlot < 0)
                    {
                        FileLogger.Log($"  [PetSealCapsule] open failed: no pet slot cid={characterId} source=0x{source.ItemTemplateId:X8}@{slotIndex}");
                        return false;
                    }

                    var sourceCount = IsStackCountedRecord(source) ? GetStackedRecordCount(source) : 1;
                    if (sourceCount <= 0)
                        return false;

                    var creatureSerial = ResolveSealedPetCreatureSerialForOpen(
                        connection,
                        transaction,
                        characterId,
                        source.ItemUid,
                        payload.CreatureSerial);
                    var creatureExtraJson = BuildInitializedPetCreatureSealExtraJson(payload.RemainUseCount);

                    if (IsStackCountedRecord(source) && sourceCount > 1)
                        _db.UpdateStackCount(connection, transaction, source.ItemUid, sourceCount - 1);
                    else
                        _db.DeleteItem(connection, transaction, source.ItemUid);

                    _db.InsertCharacterItem(
                        connection,
                        transaction,
                        characterId,
                        InventoryListType.Pet,
                        (short)targetSlot,
                        payload.CreatureItemTemplateId,
                        "pet",
                        0,
                        0,
                        0,
                        0,
                        0,
                        payload.CreatureExpireTime,
                        0,
                        creatureSerial,
                        creatureExtraJson);
                    UpsertPetCreatureExtraJson(connection, transaction, characterId, creatureSerial, creatureExtraJson);
                    ResetPetCreatureSealedState(connection, transaction, characterId, creatureSerial, creatureExtraJson);

                    _auditLogger.WriteAuditLog(
                        connection,
                        transaction,
                        characterId,
                        "open_pet_seal_capsule",
                        source,
                        InventoryListType.Pet,
                        (short)targetSlot,
                        1);
                    transaction.Commit();

                    result = new BoosterUseResult
                    {
                        SourceSlotIndex = source.SlotIndex,
                        SourceItemTemplateId = source.ItemTemplateId,
                        SourceRemainingStackCount = Math.Max(0, sourceCount - 1),
                        SourceInstanceValue = source.InstanceValue,
                        ConsumedSourceCount = 1,
                    };
                    result.Rewards.Add(new BoosterRewardResult
                    {
                        ListType = InventoryListType.Pet,
                        SlotIndex = (short)targetSlot,
                        ItemTemplateId = payload.CreatureItemTemplateId,
                        StackCount = 0,
                        GrantedCount = 1,
                    });

                    FileLogger.Log(
                        $"  [PetSealCapsule] open ok cid={characterId} source=0x{source.ItemTemplateId:X8}@{slotIndex} " +
                        $"creature=0x{payload.CreatureItemTemplateId:X8}@{targetSlot} serial=0x{creatureSerial:X8} remain={result.SourceRemainingStackCount}");
                    return true;
                }
            }
        }

        private static bool IsValidPetCreatureSealRequest(PetCreatureSealRequest request)
        {
            return request != null
                && request.CapsuleListType == InventoryListType.Main
                && request.CapsuleSlotIndex >= 0
                && request.CreatureSlotIndex >= PetInventorySlotStart
                && request.CreatureSlotIndex <= PetInventorySlotEnd
                && request.ExpectedCreatureItemTemplateId > 0;
        }

        private static bool IsPetSealCapsuleItem(int itemTemplateId)
        {
            return PetSealCapsuleCatalogCache.Value.IsSourceCapsule(itemTemplateId);
        }

        private static bool FailSealPetCreature(out PetCreatureSealResult result, byte errorCode)
        {
            result = new PetCreatureSealResult { ErrorCode = errorCode };
            return false;
        }

        private static int ResolveSealedCreatureItemTemplateId(int sourceItemTemplateId, out bool sourceWasCreatureEgg)
        {
            sourceWasCreatureEgg = false;
            if (IsKnownPetCreatureBodyItem(sourceItemTemplateId))
                return sourceItemTemplateId;

            if (CreatureEggResolver.TryResolveHatchedCreatureItemId(sourceItemTemplateId, out var hatchedItemTemplateId)
                && hatchedItemTemplateId > 0
                && hatchedItemTemplateId != sourceItemTemplateId)
            {
                sourceWasCreatureEgg = true;
                return hatchedItemTemplateId;
            }

            return sourceItemTemplateId;
        }

        private static bool IsKnownPetCreatureBodyItem(int itemTemplateId)
        {
            return itemTemplateId > 0
                && PetCreatureEvolutionCatalogCache.Value.TryResolveByItemId(itemTemplateId, out _);
        }

        private static void ResetPetCreatureSealedState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int creatureSerial,
            string extraJson)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_creatures
SET progress_value = 0,
    field_after_value = 1,
    field04 = @satiety,
    mode_flag = 0,
    mode1_field0a = 0,
    mode1_field0b = 0,
    creature_text = NULL,
    tail_flag = 0,
    extra_json = @extra
WHERE character_id = @cid
  AND creature_key = @serial;";
                command.Parameters.AddWithValue("@satiety", OpenedPetSealCapsuleSatiety);
                command.Parameters.AddWithValue("@extra", NormalizePetCreatureExtraJson(extraJson));
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@serial", creatureSerial);
                command.ExecuteNonQuery();
            }
        }

        private short FindSealedPetProductSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ItemMetadata metadata,
            short sourceSlot)
        {
            metadata = metadata ?? new ItemMetadata { ItemKind = "stackable" };
            metadata.GetSlotRange(out var slotStart, out var slotEnd);
            var slot = _db.FindEmptySlotPreferOther(
                connection,
                transaction,
                characterId,
                InventoryListType.Main,
                slotStart,
                slotEnd,
                sourceSlot);
            return slot < 0 ? (short)-1 : (short)slot;
        }

        private void InsertSealedPetProduct(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short slot,
            int itemTemplateId,
            ItemMetadata metadata,
            int creatureSerial,
            string extraJson)
        {
            var row = BuildSealedPetProductRow(itemTemplateId, metadata);
            _db.InsertCharacterItem(
                connection,
                transaction,
                characterId,
                InventoryListType.Main,
                slot,
                itemTemplateId,
                row.ItemKind,
                row.StackCount,
                row.InstanceValue,
                row.Durability,
                row.SealFlag,
                0,
                0,
                row.Marker16,
                creatureSerial,
                extraJson);
        }

        private static void UpdateSealedPetProduct(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long itemUid,
            short slot,
            int itemTemplateId,
            ItemMetadata metadata,
            int creatureSerial,
            string extraJson)
        {
            var row = BuildSealedPetProductRow(itemTemplateId, metadata);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_items
SET item_template_id = @itemTemplateId,
    item_kind = @itemKind,
    stack_count = @stackCount,
    instance_value = @instanceValue,
    durability = @durability,
    seal_flag = @sealFlag,
    option_value = 0,
    expire_time = 0,
    marker_16 = @marker16,
    pet_serial_or_handle = @petSerial,
    equipment_lock_id = 0,
    extra_json = @extraJson,
    slot_index = @slot,
    updated_at = CURRENT_TIMESTAMP
WHERE item_uid = @itemUid;";
                command.Parameters.AddWithValue("@itemTemplateId", itemTemplateId);
                command.Parameters.AddWithValue("@itemKind", row.ItemKind);
                command.Parameters.AddWithValue("@stackCount", row.StackCount);
                command.Parameters.AddWithValue("@instanceValue", row.InstanceValue);
                command.Parameters.AddWithValue("@durability", row.Durability);
                command.Parameters.AddWithValue("@sealFlag", row.SealFlag);
                command.Parameters.AddWithValue("@marker16", row.Marker16);
                command.Parameters.AddWithValue("@petSerial", creatureSerial);
                command.Parameters.AddWithValue("@extraJson", string.IsNullOrWhiteSpace(extraJson) ? "{}" : extraJson);
                command.Parameters.AddWithValue("@slot", slot);
                command.Parameters.AddWithValue("@itemUid", itemUid);
                command.ExecuteNonQuery();
            }
        }

        private static SealedPetProductRow BuildSealedPetProductRow(int itemTemplateId, ItemMetadata metadata)
        {
            if (metadata != null && metadata.IsStackable)
            {
                return new SealedPetProductRow(
                    "stackable",
                    1,
                    1,
                    0,
                    0,
                    0);
            }

            var instanceValue = InventoryDbPrimitives.GenerateInstanceValue(itemTemplateId, 0);
            return new SealedPetProductRow(
                metadata != null && string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal)
                    ? "equipment"
                    : "special",
                instanceValue,
                instanceValue,
                metadata?.Durability ?? 0,
                metadata != null && metadata.IsSealed ? (byte)1 : (byte)0,
                -1);
        }

        private static string BuildSealedPetCreatureCapsuleExtraJson(
            ItemRecord capsule,
            ItemRecord creature,
            int creatureSerial,
            int sealedCreatureItemId,
            int sealedItemId,
            string sealedCreatureExtraJson,
            byte remainUseCount)
        {
            var tailData2F = new byte[37];
            var json = new JsonObject
            {
                ["extData0"] = EncodeTradeLimitAttr(0, SealedProductInitialTradeLimitCount),
                ["prefixData0E"] = "0000000000000000",
                ["middleData1A"] = "0000000000000000000000000000000000",
                ["tailData2F"] = InventoryItemViewBytes.ToHex(tailData2F),
                ["jewelSocket"] = "000000000000000000000000000000000000000000000000000000000000",
                ["sealedItemTemplateId"] = sealedItemId,
                ["sealedCreatureItemId"] = sealedCreatureItemId,
                ["sealedSourceCreatureItemId"] = creature.ItemTemplateId,
                ["sealedCreatureSerial"] = creatureSerial,
                ["sealedCreatureExpireTime"] = creature.ExpireTime,
                ["sealedCreatureExtraJson"] = sealedCreatureExtraJson,
                ["sealedCreatureRemainUseCount"] = remainUseCount,
                [SealedTradeLimitCountProperty] = SealedProductInitialTradeLimitCount,
                ["sourceCapsuleItemId"] = capsule.ItemTemplateId,
            };
            return json.ToJsonString();
        }

        private static bool TryReadSealedPetCreaturePayload(ItemRecord source, out SealedPetCreaturePayload payload)
        {
            payload = null;
            if (source == null || string.IsNullOrWhiteSpace(source.ExtraJson))
                return false;

            if (!PetCreatureExtraView.TryReadJsonObject(source.ExtraJson, out var json))
                return false;

            var creatureItemTemplateId = ReadJsonInt(json, "sealedCreatureItemId");
            if (creatureItemTemplateId <= 0)
                return false;

            payload = new SealedPetCreaturePayload
            {
                CreatureItemTemplateId = creatureItemTemplateId,
                CreatureSerial = ReadJsonInt(json, "sealedCreatureSerial"),
                CreatureExpireTime = ReadJsonInt(json, "sealedCreatureExpireTime"),
                RemainUseCount = ResolveSealedPayloadRemainUseCount(json),
            };
            return true;
        }

        private static byte ResolveSealedPayloadRemainUseCount(JsonObject json)
        {
            if (PetCreatureExtraView.TryReadJsonInt(json, "sealedCreatureRemainUseCount", out var direct))
                return PetCreatureExtraView.ClampByte(direct);

            var creatureExtraJson = ReadJsonString(json, "sealedCreatureExtraJson");
            return TryResolvePetCreatureSealRemainUseCount(creatureExtraJson, out var remainUseCount)
                ? remainUseCount
                : DefaultPetSealCapsuleRemainUseCount;
        }

        private static int ResolveSealedPetCreatureSerialForOpen(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long sourceItemUid,
            int sealedSerial)
        {
            if (IsPersistentPetCreatureSerial(sealedSerial)
                && !IsPetCreatureSerialUsedByOtherItem(connection, transaction, characterId, sourceItemUid, sealedSerial))
            {
                return sealedSerial;
            }

            var nextSerial = LoadMaxPersistentPetCreatureSerial(connection, transaction, characterId);
            var used = LoadUsedPetCreatureSerials(connection, transaction, characterId);
            var repaired = NextPersistentPetCreatureSerial(used, ref nextSerial);
            FileLogger.Log($"  [PetSealCapsule] open repaired serial cid={characterId} old=0x{sealedSerial:X8} new=0x{repaired:X8}");
            return repaired;
        }

        private static int ResolveSealedPetCreatureProductItemId(int creatureItemTemplateId)
        {
            var catalog = PetSealCapsuleCatalogCache.Value;
            if (TryResolveCreatureEquipmentName(creatureItemTemplateId, out var currentName)
                && catalog.TryResolveByPetName(currentName, out var productItemId))
                return productItemId;

            var rootItemId = ResolveRootPetCreatureItemTemplateId(creatureItemTemplateId);
            if (rootItemId > 0
                && rootItemId != creatureItemTemplateId
                && TryResolveCreatureEquipmentName(rootItemId, out var rootName)
                && catalog.TryResolveByPetName(rootName, out productItemId))
                return productItemId;

            FileLogger.Log($"  [PetSealCapsule] product fallback creature=0x{creatureItemTemplateId:X8} root=0x{rootItemId:X8}");
            return creatureItemTemplateId;
        }

        private static int ResolveRootPetCreatureItemTemplateId(int itemTemplateId)
        {
            var current = itemTemplateId;
            var visited = new HashSet<int>();
            var catalog = PetCreatureEvolutionCatalogCache.Value;
            while (current > 0 && visited.Add(current))
            {
                if (!catalog.TryResolvePreviousByEvolutionItemId(current, out var previous)
                    || previous.ItemTemplateId <= 0
                    || previous.ItemTemplateId == current)
                    return current;

                current = previous.ItemTemplateId;
            }

            return itemTemplateId;
        }

        private static bool TryResolveCreatureEquipmentName(int itemTemplateId, out string name)
        {
            name = null;
            if (!ItemMetadataResolver.TryLoadEquipmentFile(itemTemplateId, out var equipment) || equipment == null)
                return false;

            name = NormalizePetCapsuleName(equipment.Name);
            return !string.IsNullOrWhiteSpace(name);
        }

        private static string NormalizePetCapsuleName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            var chars = new List<char>(name.Length);
            foreach (var ch in name)
            {
                if (ch == '`' || char.IsWhiteSpace(ch))
                    continue;
                chars.Add(ch);
            }

            return new string(chars.ToArray()).Trim();
        }

        private static byte EncodeTradeLimitAttr(byte attr, byte remainTradeCount)
        {
            return (byte)((attr & CommonAttrLowBitsMask) | ((remainTradeCount & 0x07) << CommonAttrTradeLimitShift));
        }

        private static int ReadJsonInt(JsonObject json, string propertyName)
        {
            return PetCreatureExtraView.TryReadJsonInt(json, propertyName, out var value) ? value : 0;
        }

        private static string ReadJsonString(JsonObject json, string propertyName)
        {
            if (json == null || !json.TryGetPropertyValue(propertyName, out var node) || node == null)
                return null;

            try
            {
                return node.GetValue<string>();
            }
            catch
            {
                return node.ToString();
            }
        }

        private readonly struct SealedPetProductRow
        {
            public SealedPetProductRow(
                string itemKind,
                int stackCount,
                int instanceValue,
                ushort durability,
                byte sealFlag,
                int marker16)
            {
                ItemKind = itemKind;
                StackCount = stackCount;
                InstanceValue = instanceValue;
                Durability = durability;
                SealFlag = sealFlag;
                Marker16 = marker16;
            }

            public string ItemKind { get; }

            public int StackCount { get; }

            public int InstanceValue { get; }

            public ushort Durability { get; }

            public byte SealFlag { get; }

            public int Marker16 { get; }
        }

        private sealed class SealedPetCreaturePayload
        {
            public int CreatureItemTemplateId { get; set; }

            public int CreatureSerial { get; set; }

            public int CreatureExpireTime { get; set; }

            public byte RemainUseCount { get; set; }
        }

        private sealed class PetSealCapsuleCatalog
        {
            private readonly HashSet<int> _sourceItemIds;
            private readonly Dictionary<string, int> _itemIdByPetName;

            private PetSealCapsuleCatalog(HashSet<int> sourceItemIds, Dictionary<string, int> itemIdByPetName)
            {
                _sourceItemIds = sourceItemIds;
                _itemIdByPetName = itemIdByPetName;
            }

            public bool IsSourceCapsule(int itemTemplateId)
            {
                return itemTemplateId > 0 && _sourceItemIds.Contains(itemTemplateId);
            }

            public bool TryResolveByPetName(string petName, out int itemTemplateId)
            {
                itemTemplateId = 0;
                var key = NormalizePetCapsuleName(petName);
                return !string.IsNullOrWhiteSpace(key)
                    && _itemIdByPetName.TryGetValue(key, out itemTemplateId);
            }

            public static PetSealCapsuleCatalog Load()
            {
                var sourceItemIds = new HashSet<int>();
                var tradeLimitMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var fallbackMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var stackableList = LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst"));
                    foreach (var entry in stackableList.Entries)
                    {
                        if (entry == null || entry.Id <= 0 || string.IsNullOrWhiteSpace(entry.FilePath))
                            continue;

                        try
                        {
                            var stackable = StackableItemFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("stackable", entry.FilePath)));
                            if (IsSourcePetSealCapsule(stackable))
                            {
                                sourceItemIds.Add(entry.Id);
                                continue;
                            }

                            if (!TryExtractPetNameFromCapsuleName(stackable?.Name, out var petName))
                                continue;

                            var key = NormalizePetCapsuleName(petName);
                            if (string.IsNullOrWhiteSpace(key))
                                continue;

                            if (IsTradeLimitPetCapsule(stackable))
                                tradeLimitMap[key] = entry.Id;
                            else if (!fallbackMap.ContainsKey(key))
                                fallbackMap[key] = entry.Id;
                        }
                        catch
                        {
                        }
                    }

                    foreach (var pair in fallbackMap)
                        if (!tradeLimitMap.ContainsKey(pair.Key))
                            tradeLimitMap[pair.Key] = pair.Value;

                    FileLogger.Log($"  [PetSealCapsule] loaded source capsules={sourceItemIds.Count} product mappings={tradeLimitMap.Count}");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"  [PetSealCapsule] catalog load failed: {ex.Message}");
                }

                return new PetSealCapsuleCatalog(sourceItemIds, tradeLimitMap);
            }

            private static bool IsSourcePetSealCapsule(StackableItemFile stackable)
            {
                const string PetText = "\u5ba0\u7269";
                const string CapsuleText = "\u80f6\u56ca";
                const string PetSealCapsuleText = "\u5ba0\u7269\u5c01\u5370\u80f6\u56ca";
                const string SealUpText = "\u5c01\u5370\u8d77\u6765";
                const string SealText = "\u5bc6\u5c01";
                const string PetEggText = "\u5ba0\u7269\u86cb";
                const string HatchedPetText = "\u5df2\u5b75\u5316\u7684\u5ba0\u7269";
                const string PetCapsuleText = "\u5ba0\u7269\u80f6\u56ca";

                if (stackable == null
                    || IsTradeLimitPetCapsule(stackable)
                    || IsUsableCeraPackageWithPayload(stackable))
                    return false;

                var name = stackable.Name ?? string.Empty;
                var explain = stackable.Explain ?? string.Empty;
                var flavor = stackable.FlavorText ?? string.Empty;
                var text = string.Concat(name, "\n", explain, "\n", flavor);
                if (text.IndexOf(PetText, StringComparison.Ordinal) < 0
                    || text.IndexOf(CapsuleText, StringComparison.Ordinal) < 0)
                    return false;

                if (name.IndexOf(PetSealCapsuleText, StringComparison.Ordinal) >= 0
                    || flavor.IndexOf(SealUpText, StringComparison.Ordinal) >= 0
                    || flavor.IndexOf(SealText, StringComparison.Ordinal) >= 0)
                    return true;

                return explain.IndexOf(PetEggText, StringComparison.Ordinal) >= 0
                    && explain.IndexOf(HatchedPetText, StringComparison.Ordinal) >= 0
                    && explain.IndexOf(PetCapsuleText, StringComparison.Ordinal) >= 0;
            }

            private static bool IsTradeLimitPetCapsule(StackableItemFile stackable)
            {
                return stackable?.AttachType != null
                    && stackable.AttachType.IndexOf("trade limit", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private static bool IsUsableCeraPackageWithPayload(StackableItemFile stackable)
            {
                if (stackable?.StackableType == null
                    || stackable.StackableType.IndexOf("usable cera package", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

                return !string.IsNullOrWhiteSpace(stackable.PackageData)
                    || (stackable.PackageRewards != null && stackable.PackageRewards.Count > 0);
            }

            private static bool TryExtractPetNameFromCapsuleName(string capsuleName, out string petName)
            {
                const string PetCapsuleText = "\u5ba0\u7269\u80f6\u56ca";
                const string PetEggText = "\u5ba0\u7269\u86cb";
                const char FullWidthOpenParenthesis = '\uFF08';
                const char FullWidthCloseParenthesis = '\uFF09';

                petName = null;
                if (string.IsNullOrWhiteSpace(capsuleName)
                    || capsuleName.IndexOf(PetCapsuleText, StringComparison.Ordinal) < 0)
                    return false;

                var startRound = capsuleName.IndexOf('(');
                var startFull = capsuleName.IndexOf(FullWidthOpenParenthesis);
                var start = startRound >= 0 && startFull >= 0
                    ? Math.Min(startRound, startFull)
                    : Math.Max(startRound, startFull);
                var end = Math.Max(capsuleName.LastIndexOf(')'), capsuleName.LastIndexOf(FullWidthCloseParenthesis));
                if (start >= 0 && end > start)
                {
                    petName = capsuleName.Substring(start + 1, end - start - 1);
                    return !string.IsNullOrWhiteSpace(NormalizePetCapsuleName(petName));
                }

                petName = capsuleName
                    .Replace(PetCapsuleText, string.Empty)
                    .Replace(PetEggText, string.Empty)
                    .Trim(' ', '\t', '(', ')', FullWidthOpenParenthesis, FullWidthCloseParenthesis);
                return !string.IsNullOrWhiteSpace(NormalizePetCapsuleName(petName));
            }
        }
    }
}
