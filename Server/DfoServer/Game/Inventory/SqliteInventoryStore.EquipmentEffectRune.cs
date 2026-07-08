using DfoServer.Game.ItemUpgrade;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryUseEquipmentEffectRune(
            int characterId,
            int accountId,
            EquipmentEffectRuneUseRequest request,
            out EquipmentEffectRuneUseResult result)
        {
            result = CreateEquipmentEffectRuneResult(request);
            if (request == null || !IsSupportedEquipmentEffectSourceList(request.SourceListType))
                return false;

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var source = _db.LoadItemRecord(connection, transaction, characterId, request.SourceListType, request.SourceSlotIndex);
                if (source == null)
                {
                    if (!IsEquipmentEffectRuneItem(request.ExpectedSourceItemTemplateId, out _, out _))
                        return false;

                    result.Status = EquipmentEffectRuneStatus.MissingSource;
                    result.SourceItemTemplateId = request.ExpectedSourceItemTemplateId;
                    return true;
                }

                result.SourceItemTemplateId = source.ItemTemplateId;
                result.SourceInstanceValue = source.InstanceValue != 0 ? source.InstanceValue : request.SourceInstanceValue;
                if (request.ExpectedSourceItemTemplateId > 0 && source.ItemTemplateId != request.ExpectedSourceItemTemplateId)
                {
                    result.Status = EquipmentEffectRuneStatus.MissingSource;
                    return true;
                }

                if (!IsEquipmentEffectRuneItem(source.ItemTemplateId, out _, out var effectId))
                    return false;

                if (IsEquipmentItemLocked(connection, transaction, characterId, source))
                {
                    result.Status = EquipmentEffectRuneStatus.Locked;
                    return true;
                }

                if (source.StackCount <= 0)
                {
                    result.Status = EquipmentEffectRuneStatus.MissingSource;
                    return true;
                }

                if (!TryResolveTargetWeapon(connection, transaction, characterId, request, out var target))
                {
                    result.Status = EquipmentEffectRuneStatus.InvalidTarget;
                    return true;
                }

                if (target.SealFlag != 0 || IsEquipmentEffectTargetLocked(connection, transaction, characterId, target))
                {
                    result.Status = target.SealFlag != 0
                        ? EquipmentEffectRuneStatus.InvalidTarget
                        : EquipmentEffectRuneStatus.Locked;
                    result.TargetListType = target.ListType;
                    result.TargetSlotIndex = target.SlotIndex;
                    result.TargetItemTemplateId = target.ItemTemplateId;
                    return true;
                }

                if (!TryApplyEquipmentEffectRune(connection, transaction, characterId, target, effectId))
                {
                    result.Status = EquipmentEffectRuneStatus.InvalidTarget;
                    result.TargetListType = target.ListType;
                    result.TargetSlotIndex = target.SlotIndex;
                    result.TargetItemTemplateId = target.ItemTemplateId;
                    return true;
                }

                _db.ConsumePackageItem(connection, transaction, source);
                _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, source, 1);
                _auditLogger.WriteAuditLog(connection, transaction, characterId, "equipment_effect_rune", target.ToItemRecord(), target.ListType, target.SlotIndex, effectId);

                var remaining = Math.Max(0, source.StackCount - 1);
                transaction.Commit();

                result.Status = EquipmentEffectRuneStatus.Applied;
                result.SourceItemTemplateId = source.ItemTemplateId;
                result.SourceRemainingStackCount = remaining;
                result.TargetListType = target.ListType;
                result.TargetSlotIndex = target.SlotIndex;
                result.TargetItemTemplateId = target.ItemTemplateId;
                result.AppliedEffectId = effectId;
                return true;
            }
        }

        private static EquipmentEffectRuneUseResult CreateEquipmentEffectRuneResult(EquipmentEffectRuneUseRequest request)
        {
            return new EquipmentEffectRuneUseResult
            {
                SourceListType = request != null ? request.SourceListType : InventoryListType.Main,
                SourceSlotIndex = request != null ? request.SourceSlotIndex : (short)0,
                SourceInstanceValue = request != null ? request.SourceInstanceValue : 0,
                SourceItemTemplateId = request != null ? request.ExpectedSourceItemTemplateId : 0,
            };
        }

        private static bool IsSupportedEquipmentEffectSourceList(InventoryListType listType)
        {
            return listType == InventoryListType.Main || listType == InventoryListType.PersonalCargo;
        }

        private static bool IsSupportedEquipmentEffectTargetList(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment;
        }

        private static bool IsEquipmentEffectRuneItem(int itemTemplateId, out StackableItemFile stackable, out ushort effectId)
        {
            stackable = null;
            effectId = 0;
            if (itemTemplateId <= 0)
                return false;

            stackable = InventoryDbPrimitives.LoadStackableItem(itemTemplateId);
            if (stackable == null || stackable.StackableType == null)
                return false;

            if (stackable.StackableType.IndexOf("[equipment effect]", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return EquipmentEffectRuneUseRequest.TryParseEffectId(stackable.IntData, out effectId);
        }

        private bool TryResolveTargetWeapon(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            EquipmentEffectRuneUseRequest request,
            out EquipmentEffectTarget target)
        {
            target = null;

            var explicitCandidate = request != null && request.HasExplicitTarget
                ? new EquipmentEffectTargetCandidate
                {
                    ListType = request.TargetListType,
                    SlotIndex = request.TargetSlotIndex,
                    ExpectedItemTemplateId = request.ExpectedTargetItemTemplateId,
                }
                : null;

            return TryResolveTargetWeapon(
                connection,
                transaction,
                characterId,
                request != null ? request.RawBody : null,
                explicitCandidate,
                out target);
        }

        private bool TryResolveTargetWeapon(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte[] rawBody,
            EquipmentEffectTargetCandidate explicitCandidate,
            out EquipmentEffectTarget target)
        {
            target = null;

            if (explicitCandidate != null && TryResolveTargetCandidate(connection, transaction, characterId, explicitCandidate, out target))
                return true;

            foreach (var candidate in ParseTargetCandidates(rawBody))
            {
                if (TryResolveTargetCandidate(connection, transaction, characterId, candidate, out target))
                    return true;
            }

            return false;
        }

        private bool TryResolveTargetCandidate(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            EquipmentEffectTargetCandidate candidate,
            out EquipmentEffectTarget target)
        {
            target = null;
            if (candidate == null)
                return false;

            if (candidate.ListType == InventoryListType.Equipment)
                return TryResolveEquippedWeaponTarget(connection, transaction, characterId, candidate, out target);

            var record = _db.LoadItemRecord(connection, transaction, characterId, candidate.ListType, candidate.SlotIndex);
            if (record == null)
                return false;

            if (candidate.ExpectedItemTemplateId > 0 && record.ItemTemplateId != candidate.ExpectedItemTemplateId)
                return false;

            if (!string.Equals(record.ItemKind, "equipment", StringComparison.Ordinal))
                return false;

            if (!ItemMetadataResolver.TryLoadEquipmentFile(record.ItemTemplateId, out var equipment))
                return false;

            if (!EquipmentTypeInfo.IsWeapon(EquipmentTypeInfo.ParseOrUnknown(equipment.EquipmentType)))
                return false;

            if (equipment.Grade > 0 && equipment.Grade <= 2)
                return false;

            target = EquipmentEffectTarget.FromCharacterItem(record);
            return true;
        }

        private bool TryResolveEquippedWeaponTarget(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            EquipmentEffectTargetCandidate candidate,
            out EquipmentEffectTarget target)
        {
            target = null;

            var entry = LoadEquippedEntry(connection, transaction, characterId, candidate.SlotIndex);
            if (entry == null)
                return false;

            if (candidate.ExpectedItemTemplateId > 0 && entry.ItemId != candidate.ExpectedItemTemplateId)
                return false;

            if (!ItemMetadataResolver.TryLoadEquipmentFile(entry.ItemId, out var equipment))
                return false;

            if (!EquipmentTypeInfo.IsWeapon(EquipmentTypeInfo.ParseOrUnknown(equipment.EquipmentType)))
                return false;

            if (equipment.Grade > 0 && equipment.Grade <= 2)
                return false;

            target = EquipmentEffectTarget.FromEquippedEntry(entry);
            return true;
        }

        private static bool IsEquipmentEffectTargetLocked(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            EquipmentEffectTarget target)
        {
            return target != null
                && IsEquipmentLockIdActive(connection, transaction, characterId, target.EquipmentLockId);
        }

        private bool TryApplyEquipmentEffectRune(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            EquipmentEffectTarget target,
            ushort effectId)
        {
            if (target == null)
                return false;

            if (target.ListType == InventoryListType.Equipment)
            {
                if (target.EquippedEntry == null || target.EquippedEntry.Raw == null || target.EquippedEntry.Raw.Length == 0)
                    return false;

                InvenItem equipped;
                try
                {
                    equipped = InvenItem.Parse(target.EquippedEntry.Raw);
                }
                catch
                {
                    return false;
                }

                equipped.Rune = effectId;
                var rawEntry = equipped.ToBytes();
                UpdateEquippedEntryRaw(connection, transaction, characterId, target.SlotIndex, target.ItemTemplateId, rawEntry);
                return true;
            }

            if (target.ItemUid <= 0)
                return false;

            var extra = ItemExtraView.Parse(target.ExtraJson);
            var builder = ItemExtraViewBuilder.FromView(extra);
            builder.Equipment.Rune = effectId;
            var updated = builder.Build();
            target.ExtraJson = updated.Serialize();
            _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
            return true;
        }

        private static IReadOnlyList<EquipmentEffectTargetCandidate> ParseTargetCandidates(byte[] body)
        {
            var candidates = new List<EquipmentEffectTargetCandidate>();
            if (body == null || body.Length < 13)
                return candidates;

            for (var offset = 11; offset < body.Length; offset++)
            {
                if (offset + 3 <= body.Length)
                {
                    var slot = BitConverter.ToInt16(body, offset);
                    var listType = (InventoryListType)body[offset + 2];
                    if (IsSupportedEquipmentEffectTargetList(listType) && IsPlausibleInventorySlot(slot))
                    {
                        AddTargetCandidate(candidates, listType, slot, ReadPositiveItemId(body, offset + 3));
                        AddTargetCandidate(candidates, listType, slot, ReadPositiveItemId(body, offset + 7));
                    }
                }

                if (offset + 6 <= body.Length)
                {
                    var slot = BitConverter.ToInt16(body, offset);
                    if (IsPlausibleInventorySlot(slot))
                        AddTargetCandidate(candidates, InventoryListType.Main, slot, ReadPositiveItemId(body, offset + 2));
                }

                if (offset + 6 <= body.Length)
                {
                    var itemId = ReadPositiveItemId(body, offset);
                    var slot = BitConverter.ToInt16(body, offset + 4);
                    if (itemId > 0 && IsPlausibleInventorySlot(slot))
                        AddTargetCandidate(candidates, InventoryListType.Main, slot, itemId);
                }

                if (offset + 7 <= body.Length)
                {
                    var listType = (InventoryListType)body[offset];
                    var slot = BitConverter.ToInt16(body, offset + 1);
                    if (IsSupportedEquipmentEffectTargetList(listType) && IsPlausibleInventorySlot(slot))
                        AddTargetCandidate(candidates, listType, slot, ReadPositiveItemId(body, offset + 3));
                }
            }

            return candidates;
        }

        private static int ReadPositiveItemId(byte[] body, int offset)
        {
            if (body == null || offset < 0 || offset + 4 > body.Length)
                return 0;

            var value = BitConverter.ToInt32(body, offset);
            return value >= 1000 ? value : 0;
        }

        private static bool IsPlausibleInventorySlot(short slotIndex)
        {
            return slotIndex >= 0 && slotIndex <= 500;
        }

        private static void AddTargetCandidate(
            List<EquipmentEffectTargetCandidate> candidates,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId)
        {
            if (!IsSupportedEquipmentEffectTargetList(listType) || !IsPlausibleInventorySlot(slotIndex))
                return;

            foreach (var existing in candidates)
            {
                if (existing.ListType == listType
                    && existing.SlotIndex == slotIndex
                    && existing.ExpectedItemTemplateId == expectedItemTemplateId)
                    return;
            }

            candidates.Add(new EquipmentEffectTargetCandidate
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ExpectedItemTemplateId = expectedItemTemplateId,
            });
        }

        private sealed class EquipmentEffectTargetCandidate
        {
            public InventoryListType ListType { get; set; }

            public short SlotIndex { get; set; }

            public int ExpectedItemTemplateId { get; set; }
        }

        private sealed class EquipmentEffectTarget
        {
            public InventoryListType ListType { get; set; }

            public short SlotIndex { get; set; }

            public int ItemTemplateId { get; set; }

            public long ItemUid { get; set; }

            public byte SealFlag { get; set; }

            public byte EquipmentLockId { get; set; }

            public int StackCount { get; set; }

            public int InstanceValue { get; set; }

            public ushort Durability { get; set; }

            public string ExtraJson { get; set; }

            public MakeEquipListCodec.Entry EquippedEntry { get; set; }

            public static EquipmentEffectTarget FromCharacterItem(ItemRecord record)
            {
                return new EquipmentEffectTarget
                {
                    ListType = record.ListType,
                    SlotIndex = record.SlotIndex,
                    ItemTemplateId = record.ItemTemplateId,
                    ItemUid = record.ItemUid,
                    SealFlag = record.SealFlag,
                    EquipmentLockId = record.EquipmentLockId,
                    StackCount = record.StackCount,
                    InstanceValue = record.InstanceValue,
                    Durability = record.Durability,
                    ExtraJson = record.ExtraJson,
                };
            }

            public static EquipmentEffectTarget FromEquippedEntry(MakeEquipListCodec.Entry entry)
            {
                InvenItem item = null;
                try
                {
                    if (entry.Raw != null && entry.Raw.Length > 0)
                        item = InvenItem.Parse(entry.Raw);
                }
                catch
                {
                    item = null;
                }

                return new EquipmentEffectTarget
                {
                    ListType = InventoryListType.Equipment,
                    SlotIndex = (short)entry.Slot,
                    ItemTemplateId = entry.ItemId,
                    ItemUid = 0,
                    SealFlag = 0,
                    EquipmentLockId = entry.EquipmentLockId,
                    StackCount = item != null ? unchecked((int)item.Value) : 0,
                    InstanceValue = item != null ? unchecked((int)item.Value) : 0,
                    Durability = item != null ? item.Durability : (ushort)0,
                    EquippedEntry = entry,
                };
            }

            public ItemRecord ToItemRecord()
            {
                return new ItemRecord
                {
                    ItemUid = ItemUid,
                    ListType = ListType,
                    SlotIndex = SlotIndex,
                    ItemTemplateId = ItemTemplateId,
                    ItemKind = "equipment",
                    StackCount = StackCount,
                    InstanceValue = InstanceValue,
                    Durability = Durability,
                    SealFlag = SealFlag,
                    EquipmentLockId = EquipmentLockId,
                    ExtraJson = ExtraJson,
                };
            }
        }
    }
}
