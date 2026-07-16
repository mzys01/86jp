using DfoServer.Infrastructure;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.ItemUpgrade;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryEnchantByBead(int characterId, int accountId, EnchantByBeadCommand command, out EnchantByBeadResult result)
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _enchantStore.TryEnchantByBead(connection, transaction, characterId, accountId, command, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        public bool TryUpgradeItem(int characterId, int accountId, ItemUpgradeCommand command, out ItemUpgradeResult result)
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _itemUpgradeStore.TryUpgradeItem(connection, transaction, characterId, accountId, command, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        public bool TryOpenEquipmentSocket(int characterId, short targetSlotIndex, int targetItemTemplateId, short materialSlotIndex, out EquipmentSocketMutationResult result)
        {
            result = null;
            if (targetItemTemplateId <= 0)
                return false;

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, targetSlotIndex);
                if (target == null || target.ItemKind != "equipment" || target.ItemTemplateId != targetItemTemplateId)
                    return false;

                if (IsEquipmentItemLocked(connection, transaction, characterId, target))
                {
                    FileLogger.Log($"  [EquipmentSocket] REJECT: locked item slot={targetSlotIndex} lockId={target.EquipmentLockId}");
                    return false;
                }

                var targetView = InventoryItemView.ForCommon(target);
                var currentOpenCount = GetEquipmentOpenCount(targetView.Entry84, targetItemTemplateId);
                if (currentOpenCount > 0)
                {
                    EnsureEquipmentSocketOpenFields(targetView.Entry84, targetItemTemplateId, currentOpenCount);
                    _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
                    _auditLogger.WriteAuditLog(connection, transaction, characterId, "repair_equipment_socket", target, target.ListType, target.SlotIndex, 0);
                    transaction.Commit();

                    result = new EquipmentSocketMutationResult
                    {
                        MaterialConsumed = false,
                    };
                    return true;
                }

                var material = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, materialSlotIndex);
                if (material == null || material.StackCount <= 0)
                    return false;

                EnsureEquipmentSocketOpenFields(targetView.Entry84, targetItemTemplateId, GetEquipmentSocketOpenCount(targetItemTemplateId));
                _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);

                var remaining = Math.Max(0, material.StackCount - 1);
                if (remaining > 0)
                    _db.UpdateStackCount(connection, transaction, material.ItemUid, remaining);
                else
                {
                    _db.DeleteItem(connection, transaction, material.ItemUid);
                    DeleteSortItemLock(characterId, connection, transaction, material.ListType, material.SlotIndex);
                }

                _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, material, 1);
                _auditLogger.WriteAuditLog(connection, transaction, characterId, "open_equipment_socket", target, target.ListType, target.SlotIndex, 0);
                transaction.Commit();

                result = new EquipmentSocketMutationResult
                {
                    MaterialItem = new InventoryMutationResult
                    {
                        ListType = material.ListType,
                        SlotIndex = material.SlotIndex,
                        ItemTemplateId = material.ItemTemplateId,
                        RemainingStackCount = remaining,
                        InstanceValue = remaining,
                        Durability = material.Durability,
                        RequestedCount = 1,
                        AppliedCount = 1,
                    },
                    MaterialConsumed = true,
                };
                return true;
            }
        }

        public bool TrySetEquipmentEmblems(int characterId, short targetSlotIndex, int targetItemTemplateId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out EquipmentEmblemMutationResult result)
        {
            result = null;
            if (emblems == null || emblems.Count == 0)
                return false;

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, targetSlotIndex);
                if (target == null || target.ItemKind != "equipment" || target.ItemTemplateId != targetItemTemplateId)
                    return TrySetEquippedEquipmentEmblems(characterId, connection, transaction, targetSlotIndex, targetItemTemplateId, emblems, out result);

                if (IsEquipmentItemLocked(connection, transaction, characterId, target))
                {
                    FileLogger.Log($"  [EmblemAttach] REJECT: locked equipment slot={targetSlotIndex} lockId={target.EquipmentLockId}");
                    return false;
                }

                var targetView = InventoryItemView.ForCommon(target);
                var openCount = targetView.Entry84.EmblemSocketCount;

                if (openCount <= 0)
                {
                    FileLogger.Log($"  [EmblemAttach] REJECT: no open sockets targetSlot={targetSlotIndex} item=0x{targetItemTemplateId:X8} openCount={targetView.Entry84.EmblemSocketCount}");
                    return false;
                }

                EnsureEquipmentSocketPlaceholders(targetView.Entry84, openCount);

                var socketType = ResolveJewelSocketType(targetItemTemplateId);
                var consumed = new List<InventoryMutationResult>();
                foreach (var request in emblems)
                {
                    if (!TryResolveEquipmentSocketRequest(targetItemTemplateId, openCount, request.SocketIndex, out var logicalSocketIndex))
                        return false;

                    var emblemType = ItemMetadataResolver.ResolveEmblemSocketType(request.EmblemItemTemplateId);
                    if (!CanAttachEmblemToJewelSocket(socketType, emblemType))
                    {
                        FileLogger.Log($"  [EmblemAttach] REJECT: socketType=0x{socketType:X2} emblemType=0x{emblemType:X2} emblem=0x{request.EmblemItemTemplateId:X8}");
                        return false;
                    }

                    var emblem = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.EmblemSlot);
                    if (emblem == null || emblem.ItemTemplateId != request.EmblemItemTemplateId || emblem.StackCount <= 0)
                        return false;

                    WriteEquipmentEmblem(targetView.Entry84, logicalSocketIndex, request.EmblemItemTemplateId);

                    var remaining = Math.Max(0, emblem.StackCount - 1);
                    if (remaining > 0)
                        _db.UpdateStackCount(connection, transaction, emblem.ItemUid, remaining);
                    else
                    {
                        _db.DeleteItem(connection, transaction, emblem.ItemUid);
                        DeleteSortItemLock(characterId, connection, transaction, emblem.ListType, emblem.SlotIndex);
                    }

                    _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, emblem, 1);
                    consumed.Add(new InventoryMutationResult
                    {
                        ListType = emblem.ListType,
                        SlotIndex = emblem.SlotIndex,
                        ItemTemplateId = emblem.ItemTemplateId,
                        RemainingStackCount = remaining,
                        InstanceValue = remaining,
                        Durability = emblem.Durability,
                        RequestedCount = 1,
                        AppliedCount = 1,
                    });
                }

                _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
                _auditLogger.WriteAuditLog(connection, transaction, characterId, "set_equipment_emblems", target, target.ListType, target.SlotIndex, emblems.Count);
                transaction.Commit();

                result = new EquipmentEmblemMutationResult
                {
                    TargetListType = target.ListType,
                    TargetSlotIndex = target.SlotIndex,
                };
                result.ConsumedEmblems.AddRange(consumed);
                return true;
            }
        }

        private bool TrySetEquippedEquipmentEmblems(int characterId, SqliteConnection connection, SqliteTransaction transaction, short targetSlotIndex, int targetItemTemplateId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out EquipmentEmblemMutationResult result)
        {
            result = null;
            var entry = LoadEquippedEntry(connection, transaction, characterId, targetSlotIndex);
            if (entry == null || entry.ItemId != targetItemTemplateId || entry.Raw == null || entry.Raw.Length == 0)
                return false;

            if (IsEquipmentLockIdActive(connection, transaction, characterId, entry.EquipmentLockId))
            {
                FileLogger.Log($"  [EmblemAttach] REJECT equipped: locked equipment slot={targetSlotIndex} lockId={entry.EquipmentLockId}");
                return false;
            }

            var targetView = EquippedItemView.FromRecord(entry);
            var openCount = targetView.Entry84.EmblemSocketCount;
            if (openCount <= 0)
            {
                FileLogger.Log($"  [EmblemAttach] REJECT equipped: no open sockets equipSlot={targetSlotIndex} item=0x{targetItemTemplateId:X8}");
                return false;
            }

            EnsureEquipmentSocketPlaceholders(targetView.Entry84, openCount);

            var socketType = ResolveJewelSocketType(targetItemTemplateId);
            var consumed = new List<InventoryMutationResult>();
            foreach (var request in emblems)
            {
                if (!TryResolveEquipmentSocketRequest(targetItemTemplateId, openCount, request.SocketIndex, out var logicalSocketIndex))
                    return false;

                var emblemType = ItemMetadataResolver.ResolveEmblemSocketType(request.EmblemItemTemplateId);
                if (!CanAttachEmblemToJewelSocket(socketType, emblemType))
                {
                    FileLogger.Log($"  [EmblemAttach] REJECT equipped: socketType=0x{socketType:X2} emblemType=0x{emblemType:X2} emblem=0x{request.EmblemItemTemplateId:X8}");
                    return false;
                }

                var emblem = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.EmblemSlot);
                if (emblem == null || emblem.ItemTemplateId != request.EmblemItemTemplateId || emblem.StackCount <= 0)
                    return false;

                WriteEquipmentEmblem(targetView.Entry84, logicalSocketIndex, request.EmblemItemTemplateId);

                var remaining = Math.Max(0, emblem.StackCount - 1);
                if (remaining > 0)
                    _db.UpdateStackCount(connection, transaction, emblem.ItemUid, remaining);
                else
                {
                    _db.DeleteItem(connection, transaction, emblem.ItemUid);
                    DeleteSortItemLock(characterId, connection, transaction, emblem.ListType, emblem.SlotIndex);
                }

                _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, emblem, 1);
                consumed.Add(new InventoryMutationResult
                {
                    ListType = emblem.ListType,
                    SlotIndex = emblem.SlotIndex,
                    ItemTemplateId = emblem.ItemTemplateId,
                    RemainingStackCount = remaining,
                    InstanceValue = remaining,
                    Durability = emblem.Durability,
                    RequestedCount = 1,
                    AppliedCount = 1,
                });
            }

            UpdateEquippedEntryRaw(connection, transaction, characterId, targetSlotIndex, targetItemTemplateId, targetView.Record.Raw);
            FileLogger.Log($"  [EmblemAttach] equipped OK slot={targetSlotIndex} item=0x{targetItemTemplateId:X8} emblems={emblems.Count}");
            transaction.Commit();

            result = new EquipmentEmblemMutationResult
            {
                TargetEquipped = true,
            };
            result.ConsumedEmblems.AddRange(consumed);
            return true;
        }

        public bool TryOpenAvatarSocket(int characterId, short targetSlotIndex, int targetItemTemplateId, short materialSlotIndex, out AvatarSocketMutationResult result)
        {
            result = null;
            if (targetItemTemplateId <= 0)
                return false;

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Avatar, targetSlotIndex);
                if (target == null || target.ItemKind != "avatar" || target.ItemTemplateId != targetItemTemplateId)
                    return false;

                if (IsEquipmentItemLocked(connection, transaction, characterId, target))
                {
                    FileLogger.Log($"  [AvatarSocket] REJECT: locked avatar slot={targetSlotIndex} lockId={target.EquipmentLockId}");
                    return false;
                }

                var targetView = InventoryItemView.ForAvatar(target);
                var expectedSocketTypes = ItemMetadataResolver.ResolveAvatarSocketTypes(targetItemTemplateId);
                var currentOpenCount = targetView.AvatarDetail.SocketCount;
                if (currentOpenCount > 0)
                {
                    if (!AvatarSocketLayoutMatches(targetView.AvatarDetail, expectedSocketTypes))
                    {
                        targetView.AvatarDetail.SetSocketTypes(expectedSocketTypes);
                        FileLogger.Log($"  [AvatarSocket] repaired socket layout item=0x{targetItemTemplateId:X8} count={Math.Min(5, expectedSocketTypes != null ? expectedSocketTypes.Count : 0)}");
                    }

                    _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
                    _auditLogger.WriteAuditLog(connection, transaction, characterId, "repair_avatar_socket", target, target.ListType, target.SlotIndex, 0);
                    transaction.Commit();

                    result = new AvatarSocketMutationResult
                    {
                        MaterialConsumed = false,
                    };
                    return true;
                }

                var material = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, materialSlotIndex);
                if (material == null || material.StackCount <= 0)
                    return false;

                if (!TrySetAvatarSocketOpenFields(targetView.AvatarDetail, expectedSocketTypes))
                {
                    FileLogger.Log($"  [AvatarSocket] REJECT: avatar item=0x{targetItemTemplateId:X8} has no socket definition in [avatar type select]");
                    return false;
                }

                _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);

                var remaining = Math.Max(0, material.StackCount - 1);
                if (remaining > 0)
                    _db.UpdateStackCount(connection, transaction, material.ItemUid, remaining);
                else
                {
                    _db.DeleteItem(connection, transaction, material.ItemUid);
                    DeleteSortItemLock(characterId, connection, transaction, material.ListType, material.SlotIndex);
                }

                _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, material, 1);
                _auditLogger.WriteAuditLog(connection, transaction, characterId, "open_avatar_socket", target, target.ListType, target.SlotIndex, 0);
                transaction.Commit();

                result = new AvatarSocketMutationResult
                {
                    MaterialItem = new InventoryMutationResult
                    {
                        ListType = material.ListType,
                        SlotIndex = material.SlotIndex,
                        ItemTemplateId = material.ItemTemplateId,
                        RemainingStackCount = remaining,
                        InstanceValue = remaining,
                        Durability = material.Durability,
                        RequestedCount = 1,
                        AppliedCount = 1,
                    },
                    MaterialConsumed = true,
                };
                return true;
            }
        }

        public bool TrySetAvatarEmblems(int characterId, short targetSlotIndex, int targetItemTemplateId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out AvatarEmblemMutationResult result)
        {
            result = null;
            if (emblems == null || emblems.Count == 0)
                return false;

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Avatar, targetSlotIndex);
                if (target == null || target.ItemKind != "avatar" || target.ItemTemplateId != targetItemTemplateId)
                    return TrySetEquippedAvatarEmblems(characterId, connection, transaction, targetSlotIndex, targetItemTemplateId, emblems, out result);

                if (IsEquipmentItemLocked(connection, transaction, characterId, target))
                {
                    FileLogger.Log($"  [AvatarEmblemAttach] REJECT: locked avatar slot={targetSlotIndex} lockId={target.EquipmentLockId}");
                    return false;
                }

                var targetView = InventoryItemView.ForAvatar(target);
                var openCount = targetView.AvatarDetail.SocketCount;
                if (openCount <= 0)
                    return false;

                var consumed = new List<InventoryMutationResult>();
                foreach (var request in emblems)
                {
                    if (request.SocketIndex >= 5)
                        return false;

                    var socketType = ToSocketTypeByte(targetView.AvatarDetail.Sockets[request.SocketIndex].SocketType);
                    if (socketType == 0)
                        return false;

                    var emblemType = ItemMetadataResolver.ResolveEmblemSocketType(request.EmblemItemTemplateId);
                    if (!CanAttachEmblemToJewelSocket(socketType, emblemType))
                    {
                        FileLogger.Log($"  [AvatarEmblemAttach] REJECT: socketType=0x{socketType:X2} emblemType=0x{emblemType:X2} emblem=0x{request.EmblemItemTemplateId:X8}");
                        return false;
                    }

                    var emblem = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.EmblemSlot);
                    if (emblem == null || emblem.ItemTemplateId != request.EmblemItemTemplateId || emblem.StackCount <= 0)
                        return false;

                    targetView.AvatarDetail.Sockets[request.SocketIndex].EmblemItemId = request.EmblemItemTemplateId;

                    var remaining = Math.Max(0, emblem.StackCount - 1);
                    if (remaining > 0)
                        _db.UpdateStackCount(connection, transaction, emblem.ItemUid, remaining);
                    else
                    {
                        _db.DeleteItem(connection, transaction, emblem.ItemUid);
                        DeleteSortItemLock(characterId, connection, transaction, emblem.ListType, emblem.SlotIndex);
                    }

                    _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, emblem, 1);
                    consumed.Add(new InventoryMutationResult
                    {
                        ListType = emblem.ListType,
                        SlotIndex = emblem.SlotIndex,
                        ItemTemplateId = emblem.ItemTemplateId,
                        RemainingStackCount = remaining,
                        InstanceValue = remaining,
                        Durability = emblem.Durability,
                        RequestedCount = 1,
                        AppliedCount = 1,
                    });
                }

                _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
                _auditLogger.WriteAuditLog(connection, transaction, characterId, "set_avatar_emblems", target, target.ListType, target.SlotIndex, emblems.Count);
                transaction.Commit();

                result = new AvatarEmblemMutationResult
                {
                    TargetListType = target.ListType,
                    TargetSlotIndex = target.SlotIndex,
                };
                result.ConsumedEmblems.AddRange(consumed);
                return true;
            }
        }

        private bool TrySetEquippedAvatarEmblems(int characterId, SqliteConnection connection, SqliteTransaction transaction, short targetSlotIndex, int targetItemTemplateId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, out AvatarEmblemMutationResult result)
        {
            result = null;
            var entry = LoadEquippedEntry(connection, transaction, characterId, targetSlotIndex);
            if (entry == null || entry.ItemId != targetItemTemplateId || entry.Raw == null || entry.Raw.Length == 0)
                return false;

            if (IsEquipmentLockIdActive(connection, transaction, characterId, entry.EquipmentLockId))
            {
                FileLogger.Log($"  [AvatarEmblemAttach] REJECT equipped: locked avatar slot={targetSlotIndex} lockId={entry.EquipmentLockId}");
                return false;
            }

            var targetView = EquippedItemView.FromRecord(entry);
            if (targetView.Slot > 10)
                return false;

            var openCount = targetView.AvatarSocketCount;
            if (openCount <= 0)
            {
                FileLogger.Log($"  [AvatarEmblemAttach] REJECT equipped: no open sockets slot={targetSlotIndex} item=0x{targetItemTemplateId:X8}");
                return false;
            }

            var consumed = new List<InventoryMutationResult>();
            foreach (var request in emblems)
            {
                if (request.SocketIndex >= 5)
                    return false;

                var socketType = ToSocketTypeByte(targetView.GetAvatarSocketType(request.SocketIndex));
                if (socketType == 0)
                    return false;

                var emblemType = ItemMetadataResolver.ResolveEmblemSocketType(request.EmblemItemTemplateId);
                if (!CanAttachEmblemToJewelSocket(socketType, emblemType))
                {
                    FileLogger.Log($"  [AvatarEmblemAttach] REJECT equipped: socketType=0x{socketType:X2} emblemType=0x{emblemType:X2} emblem=0x{request.EmblemItemTemplateId:X8}");
                    return false;
                }

                var emblem = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.EmblemSlot);
                if (emblem == null || emblem.ItemTemplateId != request.EmblemItemTemplateId || emblem.StackCount <= 0)
                    return false;

                targetView.SetAvatarSocketEmblemItemId(request.SocketIndex, request.EmblemItemTemplateId);

                var remaining = Math.Max(0, emblem.StackCount - 1);
                if (remaining > 0)
                    _db.UpdateStackCount(connection, transaction, emblem.ItemUid, remaining);
                else
                {
                    _db.DeleteItem(connection, transaction, emblem.ItemUid);
                    DeleteSortItemLock(characterId, connection, transaction, emblem.ListType, emblem.SlotIndex);
                }

                _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, emblem, 1);
                consumed.Add(new InventoryMutationResult
                {
                    ListType = emblem.ListType,
                    SlotIndex = emblem.SlotIndex,
                    ItemTemplateId = emblem.ItemTemplateId,
                    RemainingStackCount = remaining,
                    InstanceValue = remaining,
                    Durability = emblem.Durability,
                    RequestedCount = 1,
                    AppliedCount = 1,
                });
            }

            UpdateEquippedEntryRaw(connection, transaction, characterId, targetSlotIndex, targetItemTemplateId, targetView.Record.Raw);
            FileLogger.Log($"  [AvatarEmblemAttach] equipped OK slot={targetSlotIndex} item=0x{targetItemTemplateId:X8} emblems={emblems.Count}");
            transaction.Commit();

            result = new AvatarEmblemMutationResult
            {
                TargetEquipped = true,
            };
            result.ConsumedEmblems.AddRange(consumed);
            return true;
        }

        private static int GetEquipmentOpenCount(InventoryItemEntry84View entry, int itemTemplateId)
        {
            if (entry == null)
                return 0;

            return Math.Min(entry.EmblemSocketCount, GetEquipmentSocketOpenCount(itemTemplateId));
        }

        private static void EnsureEquipmentSocketOpenFields(InventoryItemEntry84View entry, int itemTemplateId, int openCount)
        {
            if (entry == null)
                return;

            var visibleCount = Math.Min(Math.Max(openCount, 0), GetEquipmentSocketOpenCount(itemTemplateId));
            if (entry.EmblemSocketCount != visibleCount)
                entry.EmblemSocketCount = (byte)visibleCount;

            if (visibleCount > 0 && entry.EmblemId1 == 0)
                entry.EmblemId1 = -1;
            if (visibleCount > 1 && entry.EmblemId2 == 0)
                entry.EmblemId2 = -1;
        }

        private static void EnsureEquipmentSocketPlaceholders(InventoryItemEntry84View entry, int openCount)
        {
            if (entry == null)
                return;

            var visibleCount = Math.Min(Math.Max(openCount, 0), 2);
            if (visibleCount > 0 && entry.EmblemId1 == 0)
                entry.EmblemId1 = -1;
            if (visibleCount > 1 && entry.EmblemId2 == 0)
                entry.EmblemId2 = -1;
        }

        private static void WriteEquipmentEmblem(InventoryItemEntry84View entry, byte socketIndex, int emblemItemTemplateId)
        {
            if (entry == null)
                return;

            if (socketIndex == 0)
                entry.EmblemId1 = emblemItemTemplateId;
            else if (socketIndex == 1)
                entry.EmblemId2 = emblemItemTemplateId;
        }

        private static bool TrySetAvatarSocketOpenFields(InventoryAvatarDetailView avatarDetail, IReadOnlyList<byte> socketTypes)
        {
            if (avatarDetail == null || socketTypes == null || socketTypes.Count == 0)
                return false;

            avatarDetail.SetSocketTypes(socketTypes);
            return true;
        }

        internal static byte[] AvatarReservedToEquippedJewel(byte[] reserved2)
        {
            return AvatarSocketDataCodec.Normalize(reserved2);
        }

        internal static byte[] EquippedJewelToAvatarReserved(byte[] jewelSocket)
        {
            return AvatarSocketDataCodec.Normalize(jewelSocket);
        }

        private static int GetEquipmentSocketOpenCount(int itemTemplateId)
        {
            return IsSingleMiddleEquipmentSocket(itemTemplateId) ? 1 : 2;
        }

        private static bool IsSingleMiddleEquipmentSocket(int itemTemplateId)
        {
            var equipmentType = ItemMetadataResolver.ResolveEquipmentType(itemTemplateId);
            return string.Equals(equipmentType, "[support]", StringComparison.OrdinalIgnoreCase)
                || string.Equals(equipmentType, "[magic stone]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolveEquipmentSocketRequest(int itemTemplateId, int openCount, byte requestSocketIndex, out byte logicalSocketIndex)
        {
            logicalSocketIndex = 0;

            var visibleOpenCount = Math.Min(openCount, 2);
            if (requestSocketIndex >= 5 || visibleOpenCount <= 0)
                return false;

            if (IsSingleMiddleEquipmentSocket(itemTemplateId))
            {
                if (requestSocketIndex > 1)
                    return false;

                return true;
            }

            if (requestSocketIndex >= visibleOpenCount)
                return false;

            logicalSocketIndex = requestSocketIndex;
            return true;
        }

        private static byte ResolveJewelSocketType(int itemTemplateId)
        {
            var equipmentType = ItemMetadataResolver.ResolveEquipmentType(itemTemplateId);
            if (string.IsNullOrWhiteSpace(equipmentType))
                return 0x10;

            switch (equipmentType)
            {
                case "[coat]":
                case "[pants]":
                    return 0x04;
                case "[shoulder]":
                case "[amulet]":
                    return 0x02;
                case "[belt]":
                case "[waist]":
                case "[ring]":
                    return 0x01;
                case "[shoes]":
                case "[wrist]":
                    return 0x08;
                default:
                    return 0x10;
            }
        }

        private static bool CanAttachEmblemToJewelSocket(byte socketType, byte emblemType)
        {
            if (socketType == 0 || emblemType == 0)
                return true;

            return (socketType & emblemType) != 0;
        }

        private static byte ToSocketTypeByte(ushort socketType)
        {
            return (byte)(socketType & 0xFF);
        }

        private static bool AvatarSocketLayoutMatches(InventoryAvatarDetailView avatarDetail, IReadOnlyList<byte> socketTypes)
        {
            if (avatarDetail == null || socketTypes == null || socketTypes.Count == 0)
                return false;

            var expectedCount = Math.Min(5, socketTypes.Count);
            for (var i = 0; i < expectedCount; i++)
            {
                if (ToSocketTypeByte(avatarDetail.Sockets[i].SocketType) != socketTypes[i])
                    return false;
            }

            for (var i = expectedCount; i < 5; i++)
            {
                if (avatarDetail.Sockets[i].SocketType != 0)
                    return false;
            }

            return true;
        }

        private static MakeEquipListCodec.Entry LoadEquippedEntry(SqliteConnection connection, SqliteTransaction transaction, int characterId, short slotIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT slot, item_id, expire_time, raw_entry, equipment_lock_id
FROM character_equipped_entries
WHERE character_id = @cid AND slot = @slot
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@slot", (int)slotIndex);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new MakeEquipListCodec.Entry
                    {
                        Slot = reader.GetInt32(0),
                        ItemId = reader.GetInt32(1),
                        ExpireTime = reader.GetInt32(2),
                        Raw = (byte[])reader.GetValue(3),
                        EquipmentLockId = Convert.ToByte(reader.GetInt32(4), CultureInfo.InvariantCulture),
                    };
                }
            }
        }

        private static void UpdateEquippedEntryRaw(SqliteConnection connection, SqliteTransaction transaction, int characterId, short slotIndex, int itemTemplateId, byte[] rawEntry)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_equipped_entries
SET raw_entry = @raw
WHERE character_id = @cid AND slot = @slot AND item_id = @itemId;";
                command.Parameters.AddWithValue("@raw", rawEntry);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@slot", (int)slotIndex);
                command.Parameters.AddWithValue("@itemId", itemTemplateId);
                command.ExecuteNonQuery();
            }

        }
    }
}
