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

                var targetExtra = ItemExtraView.Parse(target.ExtraJson);
                var targetExtraBuilder = ItemExtraViewBuilder.FromView(targetExtra);
                var tailData2F = NormalizeBytes(targetExtra.Equipment.TailData2F, 37);
                var jewelSocket = NormalizeBytes(targetExtra.Equipment.JewelSocket, 30);
                NormalizeEquipmentSocketLayout(jewelSocket, tailData2F);
                RepairJewelSocketTypes(jewelSocket, targetItemTemplateId);
                var currentOpenCount = CountOpenJewelSockets(jewelSocket);
                if (currentOpenCount > 0)
                {
                    EnsureVisibleSocketCount(tailData2F, currentOpenCount);
                    targetExtraBuilder.Equipment.TailData2F = tailData2F;
                    targetExtraBuilder.Equipment.JewelSocket = jewelSocket;
                    var updatedExtra = targetExtraBuilder.Build();
                    _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, updatedExtra.Serialize());
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

                SetEquipmentSocketOpenFields(target.ItemTemplateId, tailData2F, out jewelSocket);
                targetExtraBuilder.Equipment.TailData2F = tailData2F;
                targetExtraBuilder.Equipment.JewelSocket = jewelSocket;
                var openedExtra = targetExtraBuilder.Build();
                _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, openedExtra.Serialize());

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

                var targetExtra = ItemExtraView.Parse(target.ExtraJson);
                var targetExtraBuilder = ItemExtraViewBuilder.FromView(targetExtra);
                var tailData2F = NormalizeBytes(targetExtra.Equipment.TailData2F, 37);
                var jewelSocket = NormalizeBytes(targetExtra.Equipment.JewelSocket, 30);
                NormalizeEquipmentSocketLayout(jewelSocket, tailData2F);
                RepairJewelSocketTypes(jewelSocket, targetItemTemplateId);

                var openCount = CountOpenJewelSockets(jewelSocket);
                if (openCount <= 0 && tailData2F[0] > 0)
                {
                    var rebuiltCount = Math.Min(GetEquipmentSocketOpenCount(targetItemTemplateId), (int)tailData2F[0]);
                    jewelSocket = BuildJewelSocketData(targetItemTemplateId);
                    EnsureVisibleSocketCount(tailData2F, rebuiltCount);
                    openCount = CountOpenJewelSockets(jewelSocket);
                    FileLogger.Log($"  [EmblemAttach] repaired missing jewelSocket targetSlot={targetSlotIndex} item=0x{targetItemTemplateId:X8} count={openCount}");
                }

                if (openCount <= 0)
                {
                    FileLogger.Log($"  [EmblemAttach] REJECT: no open sockets targetSlot={targetSlotIndex} item=0x{targetItemTemplateId:X8} tailCount={tailData2F[0]} jewel={BitConverter.ToString(jewelSocket)}");
                    return false;
                }

                EnsureVisibleSocketCount(tailData2F, openCount);

                var consumed = new List<InventoryMutationResult>();
                foreach (var request in emblems)
                {
                    if (!TryResolveEquipmentSocketRequest(targetItemTemplateId, openCount, request.SocketIndex, out var logicalSocketIndex, out var physicalSocketIndex))
                        return false;

                    var socketType = GetJewelSocketType(jewelSocket, physicalSocketIndex);
                    var emblemType = ItemMetadataResolver.ResolveEmblemSocketType(request.EmblemItemTemplateId);
                    if (!CanAttachEmblemToJewelSocket(socketType, emblemType))
                    {
                        FileLogger.Log($"  [EmblemAttach] REJECT: socketType=0x{socketType:X2} emblemType=0x{emblemType:X2} emblem=0x{request.EmblemItemTemplateId:X8}");
                        return false;
                    }

                    var emblem = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.EmblemSlot);
                    if (emblem == null || emblem.ItemTemplateId != request.EmblemItemTemplateId || emblem.StackCount <= 0)
                        return false;

                    WriteEmblemToTail(tailData2F, logicalSocketIndex, request.EmblemItemTemplateId);
                    WriteEmblemToJewelSocket(jewelSocket, physicalSocketIndex, request.EmblemItemTemplateId);

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

                targetExtraBuilder.Equipment.TailData2F = tailData2F;
                targetExtraBuilder.Equipment.JewelSocket = jewelSocket;
                var updatedExtra = targetExtraBuilder.Build();
                _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, updatedExtra.Serialize());
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

            var fields = MakeEquipListCodec.ParseDisplayFields(entry.Raw);
            var openCount = fields.Emblem != null && fields.Emblem.Length > 0 ? fields.Emblem[0] : 0;
            if (openCount <= 0)
            {
                FileLogger.Log($"  [EmblemAttach] REJECT equipped: no open sockets equipSlot={targetSlotIndex} item=0x{targetItemTemplateId:X8}");
                return false;
            }

            var socketType = ResolveJewelSocketType(targetItemTemplateId);
            var consumed = new List<InventoryMutationResult>();
            foreach (var request in emblems)
            {
                if (!TryResolveEquipmentSocketRequest(targetItemTemplateId, openCount, request.SocketIndex, out var logicalSocketIndex, out _))
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

                WriteEmblemToEquippedFields(ref fields.Emblem, logicalSocketIndex, request.EmblemItemTemplateId);

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

            entry.Raw = MakeEquipListCodec.BuildEntryFromDisplayFields(targetSlotIndex, targetItemTemplateId, fields);
            UpdateEquippedEntryRaw(connection, transaction, characterId, targetSlotIndex, targetItemTemplateId, entry.Raw);
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

                var targetExtra = ItemExtraView.Parse(target.ExtraJson);
                var targetExtraBuilder = ItemExtraViewBuilder.FromAvatarView(targetExtra);
                var reserved2 = NormalizeBytes(targetExtra.Avatar.Reserved2, 30);
                var expectedSocketTypes = ItemMetadataResolver.ResolveAvatarSocketTypes(targetItemTemplateId);
                var currentOpenCount = CountOpenAvatarSockets(reserved2);
                if (currentOpenCount > 0)
                {
                    if (!AvatarSocketLayoutMatches(reserved2, expectedSocketTypes))
                    {
                        reserved2 = BuildAvatarSocketData(expectedSocketTypes);
                        FileLogger.Log($"  [AvatarSocket] repaired socket layout item=0x{targetItemTemplateId:X8} count={Math.Min(5, expectedSocketTypes != null ? expectedSocketTypes.Count : 0)}");
                    }

                    targetExtraBuilder.Avatar.Reserved2 = reserved2;
                    var updatedExtra = targetExtraBuilder.Build();
                    _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, updatedExtra.Serialize());
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

                if (!TrySetAvatarSocketOpenFields(expectedSocketTypes, out reserved2))
                {
                    FileLogger.Log($"  [AvatarSocket] REJECT: avatar item=0x{targetItemTemplateId:X8} has no socket definition in [avatar type select]");
                    return false;
                }

                targetExtraBuilder.Avatar.Reserved2 = reserved2;
                var openedExtra = targetExtraBuilder.Build();
                _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, openedExtra.Serialize());

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

                var targetExtra = ItemExtraView.Parse(target.ExtraJson);
                var targetExtraBuilder = ItemExtraViewBuilder.FromAvatarView(targetExtra);
                var reserved2 = NormalizeBytes(targetExtra.Avatar.Reserved2, 30);
                var openCount = CountOpenAvatarSockets(reserved2);
                if (openCount <= 0)
                    return false;

                var consumed = new List<InventoryMutationResult>();
                foreach (var request in emblems)
                {
                    if (request.SocketIndex >= openCount || request.SocketIndex >= 5)
                        return false;

                    var socketType = GetAvatarSocketType(targetItemTemplateId, request.SocketIndex);
                    var emblemType = ItemMetadataResolver.ResolveEmblemSocketType(request.EmblemItemTemplateId);
                    if (socketType != 0 && emblemType != 0 && socketType != 0x10 && socketType != 0xEF && socketType != emblemType)
                    {
                        FileLogger.Log($"  [AvatarEmblemAttach] REJECT: socketType=0x{socketType:X2} emblemType=0x{emblemType:X2} emblem=0x{request.EmblemItemTemplateId:X8}");
                        return false;
                    }

                    var emblem = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.EmblemSlot);
                    if (emblem == null || emblem.ItemTemplateId != request.EmblemItemTemplateId || emblem.StackCount <= 0)
                        return false;

                    WriteEmblemToAvatarSocket(reserved2, request.SocketIndex, request.EmblemItemTemplateId);

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

                targetExtraBuilder.Avatar.Reserved2 = reserved2;
                var updatedExtra = targetExtraBuilder.Build();
                _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, updatedExtra.Serialize());
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

            InvenItem item;
            try
            {
                item = InvenItem.Parse(entry.Raw);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [AvatarEmblemAttach] REJECT equipped: parse failed slot={targetSlotIndex} item=0x{targetItemTemplateId:X8} {ex.Message}");
                return false;
            }

            if (item.Slot > 10)
                return false;

            item.JewelSocket = NormalizeBytes(item.JewelSocket, 30);
            var openCount = CountOpenEquippedAvatarSockets(item.JewelSocket);
            if (openCount <= 0)
            {
                FileLogger.Log($"  [AvatarEmblemAttach] REJECT equipped: no open sockets slot={targetSlotIndex} item=0x{targetItemTemplateId:X8}");
                return false;
            }

            var socketTypes = ItemMetadataResolver.ResolveAvatarSocketTypes(targetItemTemplateId);
            var consumed = new List<InventoryMutationResult>();
            foreach (var request in emblems)
            {
                if (request.SocketIndex >= openCount || request.SocketIndex >= 5)
                    return false;

                var socketType = GetEquippedAvatarSocketType(item.JewelSocket, request.SocketIndex, socketTypes);
                var emblemType = ItemMetadataResolver.ResolveEmblemSocketType(request.EmblemItemTemplateId);
                if (!CanAttachEmblemToJewelSocket(socketType, emblemType))
                {
                    FileLogger.Log($"  [AvatarEmblemAttach] REJECT equipped: socketType=0x{socketType:X2} emblemType=0x{emblemType:X2} emblem=0x{request.EmblemItemTemplateId:X8}");
                    return false;
                }

                var emblem = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.EmblemSlot);
                if (emblem == null || emblem.ItemTemplateId != request.EmblemItemTemplateId || emblem.StackCount <= 0)
                    return false;

                WriteEmblemToEquippedAvatarJewelSocket(item.JewelSocket, request.SocketIndex, request.EmblemItemTemplateId);

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

            entry.Raw = item.ToBytes();
            UpdateEquippedEntryRaw(connection, transaction, characterId, targetSlotIndex, targetItemTemplateId, entry.Raw);
            FileLogger.Log($"  [AvatarEmblemAttach] equipped OK slot={targetSlotIndex} item=0x{targetItemTemplateId:X8} emblems={emblems.Count}");
            transaction.Commit();

            result = new AvatarEmblemMutationResult
            {
                TargetEquipped = true,
            };
            result.ConsumedEmblems.AddRange(consumed);
            return true;
        }

        private static void SetEquipmentSocketOpenFields(int itemTemplateId, byte[] tailData2F, out byte[] jewelSocket)
        {
            jewelSocket = BuildJewelSocketData(itemTemplateId);
            EnsureVisibleSocketCount(tailData2F, GetEquipmentSocketOpenCount(itemTemplateId));
        }

        private static bool TrySetAvatarSocketOpenFields(IReadOnlyList<byte> socketTypes, out byte[] reserved2)
        {
            reserved2 = null;

            if (socketTypes == null || socketTypes.Count == 0)
                return false;

            reserved2 = BuildAvatarSocketData(socketTypes);
            return true;
        }

        private static byte[] BuildAvatarSocketData(IReadOnlyList<byte> socketTypes)
        {
            var data = new byte[30];
            if (socketTypes == null)
                return data;

            var count = Math.Min(5, socketTypes.Count);
            for (var i = 0; i < count; i++)
            {
                var offset = i * 6;
                data[offset] = 0;
                data[offset + 1] = socketTypes[i];
                data[offset + 2] = socketTypes[i] == 0xEF ? (byte)0xFF : (byte)0;
            }
            return data;
        }

        internal static byte[] AvatarReservedToEquippedJewel(byte[] reserved2)
        {
            var data = NormalizeBytes(reserved2, 30);
            var equipped = new byte[30];
            for (var i = 0; i < 5; i++)
            {
                var offset = i * 6;
                equipped[offset] = data[offset + 1];
                equipped[offset + 1] = data[offset + 2];
                Buffer.BlockCopy(data, offset + 3, equipped, offset + 2, 3);
            }
            return equipped;
        }

        internal static byte[] EquippedJewelToAvatarReserved(byte[] jewelSocket)
        {
            var data = NormalizeBytes(jewelSocket, 30);
            var reserved = new byte[30];
            for (var i = 0; i < 5; i++)
            {
                var offset = i * 6;
                reserved[offset] = 0;
                reserved[offset + 1] = data[offset];
                reserved[offset + 2] = data[offset + 1];
                Buffer.BlockCopy(data, offset + 2, reserved, offset + 3, 3);
            }
            return reserved;
        }

        private static byte[] BuildJewelSocketData(int itemTemplateId)
        {
            var data = new byte[30];
            var socketType = ResolveJewelSocketType(itemTemplateId);
            var socketCount = GetEquipmentSocketOpenCount(itemTemplateId);
            for (var i = 0; i < socketCount; i++)
            {
                var offset = GetEquipmentSocketPhysicalIndex(itemTemplateId, (byte)i) * 6;
                data[offset] = socketType;
                data[offset + 1] = 0;
            }
            return data;
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

        private static byte GetEquipmentSocketPhysicalIndex(int itemTemplateId, byte logicalSocketIndex)
        {
            return IsSingleMiddleEquipmentSocket(itemTemplateId) ? (byte)1 : logicalSocketIndex;
        }

        private static bool TryResolveEquipmentSocketRequest(int itemTemplateId, int openCount, byte requestSocketIndex, out byte logicalSocketIndex, out byte physicalSocketIndex)
        {
            logicalSocketIndex = 0;
            physicalSocketIndex = 0;

            if (requestSocketIndex >= 5 || openCount <= 0)
                return false;

            if (IsSingleMiddleEquipmentSocket(itemTemplateId))
            {
                if (requestSocketIndex > 1)
                    return false;

                physicalSocketIndex = 1;
                return true;
            }

            if (requestSocketIndex >= openCount)
                return false;

            logicalSocketIndex = requestSocketIndex;
            physicalSocketIndex = requestSocketIndex;
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

        private static int CountOpenJewelSockets(byte[] jewelSocket)
        {
            var data = jewelSocket;
            if (data == null || data.Length < 8)
                return 0;

            var count = 0;
            for (var i = 0; i < 2; i++)
            {
                var offset = i * 6;
                if (offset < data.Length && data[offset] != 0)
                    count++;
            }
            return count;
        }

        private static int CountOpenAvatarSockets(byte[] reserved2)
        {
            var data = reserved2;
            if (data == null || data.Length < 6)
                return 0;

            var count = 0;
            for (var i = 0; i < 5; i++)
            {
                var offset = i * 6;
                if (IsAvatarSocketOpen(data, offset))
                    count++;
            }
            return count;
        }

        private static byte GetJewelSocketType(byte[] jewelSocket, byte socketIndex)
        {
            var data = NormalizeBytes(jewelSocket, 30);
            var offset = socketIndex * 6;
            if (data == null || offset >= data.Length)
                return 0;
            return data[offset];
        }

        private static bool CanAttachEmblemToJewelSocket(byte socketType, byte emblemType)
        {
            if (socketType == 0 || emblemType == 0)
                return true;

            return (socketType & emblemType) != 0;
        }

        private static byte GetAvatarSocketType(int itemTemplateId, byte socketIndex)
        {
            var socketTypes = ItemMetadataResolver.ResolveAvatarSocketTypes(itemTemplateId);
            return socketTypes != null && socketIndex < socketTypes.Count ? socketTypes[socketIndex] : (byte)0;
        }

        private static bool IsAvatarSocketOpen(byte[] data, int offset)
        {
            return data != null
                && offset >= 0
                && offset + 5 < data.Length
                && data[offset] == 0
                && data[offset + 1] != 0
                && (data[offset + 1] != 0xEF || data[offset + 2] == 0xFF);
        }

        private static bool IsEquippedAvatarSocketOpen(byte[] data, int offset)
        {
            return data != null
                && offset >= 0
                && offset + 5 < data.Length
                && data[offset] != 0
                && (data[offset] != 0xEF || data[offset + 1] == 0xFF);
        }

        private static int CountOpenEquippedAvatarSockets(byte[] data)
        {
            if (data == null || data.Length < 6)
                return 0;

            var count = 0;
            for (var i = 0; i < 5; i++)
            {
                var offset = i * 6;
                if (IsEquippedAvatarSocketOpen(data, offset))
                    count++;
            }
            return count;
        }

        private static byte GetEquippedAvatarSocketType(byte[] jewelSocket, byte socketIndex, IReadOnlyList<byte> fallbackTypes)
        {
            var data = NormalizeBytes(jewelSocket, 30);
            var offset = socketIndex * 6;
            if (offset < data.Length && data[offset] != 0)
                return data[offset];

            return fallbackTypes != null && socketIndex < fallbackTypes.Count ? fallbackTypes[socketIndex] : (byte)0;
        }

        private static bool AvatarSocketLayoutMatches(byte[] data, IReadOnlyList<byte> socketTypes)
        {
            if (socketTypes == null || socketTypes.Count == 0)
                return false;

            data = NormalizeBytes(data, 30);
            var expectedCount = Math.Min(5, socketTypes.Count);
            for (var i = 0; i < expectedCount; i++)
            {
                var offset = i * 6;
                if (data[offset] != 0 || data[offset + 1] != socketTypes[i])
                    return false;

                if (socketTypes[i] == 0xEF && data[offset + 2] != 0xFF)
                    return false;
            }

            for (var i = expectedCount; i < 5; i++)
            {
                var offset = i * 6;
                if (IsAvatarSocketOpen(data, offset))
                    return false;
            }

            return true;
        }

        private static void WriteEmblemToTail(byte[] tailData2F, byte socketIndex, int emblemItemTemplateId)
        {
            if (tailData2F == null || tailData2F.Length < 37)
                return;

            tailData2F[0] = Math.Max(tailData2F[0], (byte)(socketIndex + 1));
            var offset = 1 + socketIndex * 4;
            if (offset + 4 <= tailData2F.Length)
                BitConverter.GetBytes(emblemItemTemplateId).CopyTo(tailData2F, offset);
        }

        private static void WriteEmblemToEquippedFields(ref byte[] emblemData, byte socketIndex, int emblemItemTemplateId)
        {
            var requiredLength = 1 + (socketIndex + 1) * 4;
            if (emblemData == null || emblemData.Length < requiredLength)
            {
                var resized = new byte[Math.Max(requiredLength, emblemData != null && emblemData.Length > 0 ? emblemData.Length : 1)];
                if (emblemData != null)
                    Buffer.BlockCopy(emblemData, 0, resized, 0, Math.Min(emblemData.Length, resized.Length));
                emblemData = resized;
            }

            emblemData[0] = Math.Max(emblemData[0], (byte)(socketIndex + 1));
            BitConverter.GetBytes(emblemItemTemplateId).CopyTo(emblemData, 1 + socketIndex * 4);
        }

        private static void WriteEmblemToEquippedAvatarJewelSocket(byte[] jewelSocket, byte socketIndex, int emblemItemTemplateId)
        {
            if (jewelSocket == null || jewelSocket.Length < 30)
                return;

            var offset = socketIndex * 6 + 2;
            if (offset + 3 <= jewelSocket.Length)
            {
                var bytes = BitConverter.GetBytes(emblemItemTemplateId);
                Buffer.BlockCopy(bytes, 0, jewelSocket, offset, 3);
            }
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

        private static void EnsureVisibleSocketCount(byte[] tailData2F, int openCount)
        {
            if (tailData2F == null || openCount <= 0)
                return;

            tailData2F[0] = (byte)Math.Max(tailData2F[0], Math.Min(openCount, 2));
            for (var i = 0; i < openCount && i < 2; i++)
            {
                var offset = 1 + i * 4;
                if (offset + 4 <= tailData2F.Length && BitConverter.ToInt32(tailData2F, offset) == 0)
                    BitConverter.GetBytes(-1).CopyTo(tailData2F, offset);
            }
        }

        private static void NormalizeEquipmentSocketLayout(byte[] jewelSocket, byte[] tailData2F)
        {
            if (jewelSocket == null)
                return;

            var changed = false;
            for (var i = 0; i < 2; i++)
            {
                var offset = i * 6;
                if (jewelSocket[offset] == 0 && IsKnownJewelSocketType(jewelSocket[offset + 1]))
                {
                    jewelSocket[offset] = jewelSocket[offset + 1];
                    jewelSocket[offset + 1] = 0;
                    changed = true;
                }
                else if (jewelSocket[offset] == 0x02 && IsKnownJewelSocketType(jewelSocket[offset + 1]))
                {
                    jewelSocket[offset] = jewelSocket[offset + 1];
                    jewelSocket[offset + 1] = 0;
                    changed = true;
                }
            }

            if (changed)
                EnsureVisibleSocketCount(tailData2F, CountOpenJewelSockets(jewelSocket));
        }

        private static bool RepairJewelSocketTypes(byte[] jewelSocket, int itemTemplateId)
        {
            if (jewelSocket == null)
                return false;

            var expectedType = ResolveJewelSocketType(itemTemplateId);
            if (IsSingleMiddleEquipmentSocket(itemTemplateId))
                return RepairSingleMiddleJewelSocket(jewelSocket, expectedType);

            var changed = false;
            for (var i = 0; i < 2; i++)
            {
                var offset = i * 6;
                if (offset >= jewelSocket.Length || jewelSocket[offset] == 0)
                    continue;

                if (jewelSocket[offset] != expectedType)
                {
                    jewelSocket[offset] = expectedType;
                    jewelSocket[offset + 1] = 0;
                    changed = true;
                }
            }

            return changed;
        }

        private static bool RepairSingleMiddleJewelSocket(byte[] jewelSocket, byte expectedType)
        {
            var sourceOffset = jewelSocket.Length > 6 && jewelSocket[6] != 0
                ? 6
                : FindFirstOpenJewelSocketOffset(jewelSocket, 3);

            if (sourceOffset == 6 && jewelSocket[sourceOffset] == expectedType && !HasOpenEquipmentSocketOutsideMiddle(jewelSocket))
                return false;

            if (sourceOffset < 0)
                return false;

            var emblemBytes = new byte[4];
            if (sourceOffset + 6 <= jewelSocket.Length)
                Buffer.BlockCopy(jewelSocket, sourceOffset + 2, emblemBytes, 0, emblemBytes.Length);

            Array.Clear(jewelSocket, 0, jewelSocket.Length);
            jewelSocket[6] = expectedType;
            jewelSocket[7] = 0;
            Buffer.BlockCopy(emblemBytes, 0, jewelSocket, 8, emblemBytes.Length);
            return true;
        }

        private static int FindFirstOpenJewelSocketOffset(byte[] jewelSocket, int maxSlots)
        {
            if (jewelSocket == null)
                return -1;

            for (var i = 0; i < maxSlots; i++)
            {
                var offset = i * 6;
                if (offset < jewelSocket.Length && jewelSocket[offset] != 0)
                    return offset;
            }

            return -1;
        }

        private static bool HasOpenEquipmentSocketOutsideMiddle(byte[] jewelSocket)
        {
            return jewelSocket != null
                && ((jewelSocket.Length > 0 && jewelSocket[0] != 0)
                    || (jewelSocket.Length > 12 && jewelSocket[12] != 0));
        }

        private static bool IsKnownJewelSocketType(byte socketType)
        {
            return socketType == 0x01
                || socketType == 0x02
                || socketType == 0x04
                || socketType == 0x08
                || socketType == 0x10;
        }

        private static void WriteEmblemToJewelSocket(byte[] jewelSocket, byte socketIndex, int emblemItemTemplateId)
        {
            if (jewelSocket == null || jewelSocket.Length < 30)
                return;

            var offset = socketIndex * 6 + 2;
            if (offset + 4 <= jewelSocket.Length)
                BitConverter.GetBytes(emblemItemTemplateId).CopyTo(jewelSocket, offset);
        }

        private static void WriteEmblemToAvatarSocket(byte[] reserved2, byte socketIndex, int emblemItemTemplateId)
        {
            if (reserved2 == null || reserved2.Length < 30)
                return;

            var offset = socketIndex * 6 + 3;
            if (offset + 3 <= reserved2.Length)
            {
                var bytes = BitConverter.GetBytes(emblemItemTemplateId);
                Buffer.BlockCopy(bytes, 0, reserved2, offset, 3);
            }
        }

        private static byte[] NormalizeBytes(byte[] source, int expectedLength)
        {
            var buffer = new byte[expectedLength];
            if (source != null && source.Length > 0)
                Buffer.BlockCopy(source, 0, buffer, 0, Math.Min(source.Length, expectedLength));
            return buffer;
        }
    }
}
