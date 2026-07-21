using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Currency;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryTakeDungeonDrop(
            int characterId,
            InventoryListType listType,
            short slotIndex,
            int dropCount,
            out DungeonInventoryDropPayload payload)
        {
            payload = null;
            if (listType != InventoryListType.Main || slotIndex < 0 || dropCount <= 0)
                return false;

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            if (slotIndex == 0)
            {
                if (dropCount > DungeonDropPolicy.MaxGoldPerDrop
                    || !CurrencyService.TrySpendGold(connection, transaction, characterId, dropCount))
                {
                    return false;
                }

                var wallet = _db.LoadWallet(connection, transaction, characterId);
                transaction.Commit();
                payload = new DungeonInventoryDropPayload
                {
                    IsGold = true,
                    ItemTemplateId = 0,
                    DroppedCount = dropCount,
                    RemainingCount = wallet.Gold,
                };
                return true;
            }

            var item = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, slotIndex);
            if (item == null || IsEquipmentItemLocked(connection, transaction, characterId, item))
                return false;

            var metadata = ItemMetadataResolver.Resolve(item.ItemTemplateId);
            if (!DungeonDropPolicy.CanDrop(item, metadata, out var rejectReason))
            {
                FileLogger.Log($"[DungeonDrop] REJECT: cid={characterId} slot={slotIndex} item={item.ItemTemplateId} reason={rejectReason}");
                return false;
            }

            var isStackable = string.Equals(metadata.ItemKind, "stackable", StringComparison.Ordinal)
                || IsStackCountedRecord(item);
            var availableCount = isStackable ? GetStackedRecordCount(item) : 1;
            if (dropCount > availableCount || (!isStackable && dropCount != 1))
                return false;

            var packetItem = InventoryProtocolMapper.ToCommonItem(item);
            packetItem.CountOrInstanceValue = isStackable ? dropCount : item.StackCount;
            packetItem.EquipmentLockId = item.EquipmentLockId;

            var remainingCount = isStackable ? availableCount - dropCount : 0;
            if (isStackable && remainingCount > 0)
                _db.UpdateStackCount(connection, transaction, item.ItemUid, remainingCount);
            else
                _db.DeleteItem(connection, transaction, item.ItemUid);

            _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, item, dropCount);
            transaction.Commit();

            payload = new DungeonInventoryDropPayload
            {
                IsStackable = isStackable,
                ItemTemplateId = item.ItemTemplateId,
                DroppedCount = dropCount,
                RemainingCount = remainingCount,
                PacketItem = packetItem,
                ItemKind = item.ItemKind,
                InstanceValue = item.InstanceValue,
                Durability = item.Durability,
                SealFlag = item.SealFlag,
                OptionValue = item.OptionValue,
                ExpireTime = item.ExpireTime,
                Marker16 = item.Marker16,
                PetSerialOrHandle = item.PetSerialOrHandle,
                EquipmentLockId = item.EquipmentLockId,
                ExtraJson = item.ExtraJson,
            };
            return true;
        }

        public bool TryRestoreDungeonDrop(
            int characterId,
            DungeonInventoryDropPayload payload,
            out short assignedSlot,
            out CommonInventoryItem restoredItem)
        {
            assignedSlot = -1;
            restoredItem = null;
            if (payload == null || payload.IsGold || payload.ItemTemplateId <= 0 || payload.DroppedCount <= 0)
                return false;

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var metadata = ItemMetadataResolver.Resolve(payload.ItemTemplateId);
            var placement = ItemIntake.ResolvePlacement(payload.ItemTemplateId, metadata);
            if (placement.ListType != InventoryListType.Main)
                return false;

            if (payload.IsStackable)
            {
                var existing = _db.FindStackableItemByTemplateIdAndExpireTime(
                    connection,
                    transaction,
                    characterId,
                    InventoryListType.Main,
                    payload.ItemTemplateId,
                    payload.ExpireTime,
                    metadata.StackLimit,
                    placement.SlotStart,
                    placement.SlotEnd,
                    payload.DroppedCount);
                if (existing != null)
                {
                    _db.UpdateStackCount(connection, transaction, existing.ItemUid, existing.StackCount + payload.DroppedCount);
                    var candidateSlot = existing.SlotIndex;
                    var candidateItem = _db.LoadCommonItem(
                        connection,
                        transaction,
                        characterId,
                        InventoryListType.Main,
                        candidateSlot);
                    if (candidateItem == null)
                        return false;

                    transaction.Commit();
                    assignedSlot = candidateSlot;
                    restoredItem = candidateItem;
                    return true;
                }
            }

            var slot = _db.FindEmptySlot(
                connection,
                transaction,
                characterId,
                InventoryListType.Main,
                placement.SlotStart,
                placement.SlotEnd);
            if (slot < 0)
                return false;

            var stackCount = payload.IsStackable
                ? payload.DroppedCount
                : payload.PacketItem?.CountOrInstanceValue ?? payload.InstanceValue;
            _db.InsertCharacterItemRecord(connection, transaction, characterId, new ItemRecord
            {
                ListType = InventoryListType.Main,
                SlotIndex = (short)slot,
                ItemTemplateId = payload.ItemTemplateId,
                ItemKind = payload.ItemKind,
                StackCount = stackCount,
                InstanceValue = payload.InstanceValue,
                Durability = payload.Durability,
                SealFlag = payload.SealFlag,
                OptionValue = payload.OptionValue,
                ExpireTime = payload.ExpireTime,
                Marker16 = payload.Marker16,
                PetSerialOrHandle = payload.PetSerialOrHandle,
                EquipmentLockId = payload.EquipmentLockId,
                ExtraJson = payload.ExtraJson,
            });
            var insertedSlot = (short)slot;
            var insertedItem = _db.LoadCommonItem(
                connection,
                transaction,
                characterId,
                InventoryListType.Main,
                insertedSlot);
            if (insertedItem == null)
                return false;

            transaction.Commit();
            assignedSlot = insertedSlot;
            restoredItem = insertedItem;
            return true;
        }

        private static class DungeonDropPolicy
        {
            internal const int MaxGoldPerDrop = 1000;

            internal static bool CanDrop(ItemRecord item, ItemMetadata metadata, out string rejectReason)
            {
                rejectReason = null;
                if (item == null || metadata == null || metadata.ItemKind == "special")
                {
                    rejectReason = "missing current-PVF metadata";
                    return false;
                }

                if (metadata.Rarity > 2)
                {
                    rejectReason = $"rarity {metadata.Rarity} exceeds 2";
                    return false;
                }

                var attachType = NormalizePvfToken(metadata.AttachType);
                var allowedAttachType = attachType == "free"
                    || attachType == "sealing trade"
                    || attachType == "trade limit"
                    || (attachType == "sealing"
                        && string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal));
                if (!allowedAttachType)
                {
                    rejectReason = $"attach type [{attachType}]";
                    return false;
                }

                if (InventoryItemView.ForCommon(item).Entry84.TradeRestriction != 0)
                {
                    rejectReason = "instance trade restriction";
                    return false;
                }

                if (EquipmentTypeInfo.ParseOrUnknown(metadata.EquipmentType) == EquipmentType.TitleName)
                {
                    rejectReason = "title equipment";
                    return false;
                }

                return true;
            }

            private static string NormalizePvfToken(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return string.Empty;

                var normalized = value.Trim().Trim('`').Trim();
                if (normalized.Length >= 2 && normalized[0] == '[' && normalized[normalized.Length - 1] == ']')
                    normalized = normalized.Substring(1, normalized.Length - 2);
                return normalized.Trim().ToLowerInvariant();
            }
        }
    }
}
