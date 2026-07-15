using System;
using Microsoft.Data.Sqlite;
using DfoServer.Game.ExpertJob;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryEnchantStore
    {
        private readonly InventoryDbPrimitives _db;
        private readonly InventoryAuditLogger _auditLogger;

        internal InventoryEnchantStore(InventoryDbPrimitives db, InventoryAuditLogger auditLogger)
        {
            _db = db;
            _auditLogger = auditLogger;
        }

        internal bool TryEnchantByBead(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, EnchantByBeadCommand command, out EnchantByBeadResult result)
        {
            if (command == null)
            {
                result = EnchantByBeadResult.Error(null, EnchantByBeadResult.ErrorInvalidBead);
                return false;
            }

            result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidBead);

            // 从主背包取宝珠和目标装备；先只放开已确认的空间。
            if (command.BeadListType != InventoryListType.Main
                || (command.TargetListType != InventoryListType.Main
                    && command.TargetListType != InventoryListType.Pet))
            {
                FileLogger.Log($"  [EnchantByBead] REJECT: unsupported space bead={command.BeadListType} target={command.TargetListType}");
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorUnsupported);
                return false;
            }

            var bead = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, command.BeadSlotIndex);
            if (bead == null || bead.StackCount <= 0)
            {
                FileLogger.Log($"  [EnchantByBead] REJECT: invalid bead slot={command.BeadSlotIndex} itemKind={bead?.ItemKind ?? "null"}");
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidBead);
                return false;
            }

            if (command.TargetListType == InventoryListType.Pet)
                return TryEnchantPetCreatureByBead(connection, transaction, characterId, command, bead, out result);

            var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, command.TargetSlotIndex);
            if (target == null || target.ItemKind != "equipment")
            {
                FileLogger.Log($"  [EnchantByBead] REJECT: invalid target slot={command.TargetSlotIndex} itemKind={target?.ItemKind ?? "null"}");
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);
                return false;
            }

            var enchantUpgradeCount = ReadEnchantUpgradeCount(bead);
            if (!ItemMetadataResolver.TryValidateEnchantByBeadTarget(bead.ItemTemplateId, target.ItemTemplateId, enchantUpgradeCount, out var enchantCardItemId, out var rejectReason))
            {
                var errorCode = rejectReason != null && rejectReason.StartsWith("target", StringComparison.Ordinal)
                    ? EnchantByBeadResult.ErrorInvalidTarget
                    : EnchantByBeadResult.ErrorUnsupported;
                FileLogger.Log($"  [EnchantByBead] REJECT: bead=0x{bead.ItemTemplateId:X8} target=0x{target.ItemTemplateId:X8} upgrade={enchantUpgradeCount} reason={rejectReason}");
                result = EnchantByBeadResult.Error(command, errorCode);
                return false;
            }

            var targetView = InventoryItemView.ForCommon(target);
            targetView.EnchantCardId = enchantCardItemId;
            targetView.EnchantUpgradeCount = enchantUpgradeCount;
            _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);

            var remainingBeadCount = bead.StackCount - 1;
            if (remainingBeadCount > 0)
                _db.UpdateStackCount(connection, transaction, bead.ItemUid, remainingBeadCount);
            else
                _db.DeleteItem(connection, transaction, bead.ItemUid);

            _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, bead, 1);
            _auditLogger.WriteEnchantAuditLog(connection, transaction, characterId, bead, target, enchantCardItemId, enchantUpgradeCount);

            FileLogger.Log($"  [EnchantByBead] OK: beadSlot={command.BeadSlotIndex} targetSlot={command.TargetSlotIndex} enchantCard=0x{enchantCardItemId:X8} upgrade={enchantUpgradeCount} beadLeft={Math.Max(0, remainingBeadCount)}");
            result = EnchantByBeadResult.Ok(command, Math.Max(0, remainingBeadCount), enchantCardItemId);
            return true;
        }

        private bool TryEnchantPetCreatureByBead(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            EnchantByBeadCommand command,
            SqliteInventoryStore.ItemRecord bead,
            out EnchantByBeadResult result)
        {
            result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);

            var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Pet, command.TargetSlotIndex);
            if (target == null
                || target.ItemKind != "pet"
                || target.PetSerialOrHandle <= 0
                || !SqliteInventoryStore.IsCreatureItem(target.ItemTemplateId))
            {
                FileLogger.Log($"  [EnchantByBead] REJECT pet: invalid target slot={command.TargetSlotIndex} itemKind={target?.ItemKind ?? "null"} item=0x{target?.ItemTemplateId ?? 0:X8} serial={target?.PetSerialOrHandle ?? 0}");
                return false;
            }

            var enchantUpgradeCount = ReadEnchantUpgradeCount(bead);
            if (!ItemMetadataResolver.TryValidatePetEnchantByBeadTarget(bead.ItemTemplateId, target.ItemTemplateId, enchantUpgradeCount, out var enchantCardItemId, out var rejectReason))
            {
                var errorCode = rejectReason != null && rejectReason.StartsWith("target", StringComparison.Ordinal)
                    ? EnchantByBeadResult.ErrorInvalidTarget
                    : EnchantByBeadResult.ErrorUnsupported;
                FileLogger.Log($"  [EnchantByBead] REJECT pet: bead=0x{bead.ItemTemplateId:X8} target=0x{target.ItemTemplateId:X8} upgrade={enchantUpgradeCount} reason={rejectReason}");
                result = EnchantByBeadResult.Error(command, errorCode);
                return false;
            }

            target.ExtraJson = SqliteInventoryStore.SetPetCreatureEnchantExtraJson(
                target.ExtraJson,
                enchantCardItemId,
                enchantUpgradeCount);
            _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
            SqliteInventoryStore.PersistPetCreatureExtraJson(
                connection,
                transaction,
                characterId,
                target.PetSerialOrHandle,
                target.ExtraJson);

            var remainingBeadCount = bead.StackCount - 1;
            if (remainingBeadCount > 0)
                _db.UpdateStackCount(connection, transaction, bead.ItemUid, remainingBeadCount);
            else
                _db.DeleteItem(connection, transaction, bead.ItemUid);

            _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, bead, 1);
            _auditLogger.WriteEnchantAuditLog(connection, transaction, characterId, bead, target, enchantCardItemId, enchantUpgradeCount);

            FileLogger.Log($"  [EnchantByBead] OK pet: beadSlot={command.BeadSlotIndex} targetSlot={command.TargetSlotIndex} serial={target.PetSerialOrHandle} enchantCard=0x{enchantCardItemId:X8} upgrade={enchantUpgradeCount} beadLeft={Math.Max(0, remainingBeadCount)}");
            result = EnchantByBeadResult.Ok(command, Math.Max(0, remainingBeadCount), enchantCardItemId);
            return true;
        }

        private static byte ReadEnchantUpgradeCount(SqliteInventoryStore.ItemRecord bead)
        {
            // 86 附魔卡片升级次数跟随宝珠动态数据保存，写入装备时落到 common entry +0x12。
            return bead != null
                ? InventoryItemView.ForCommon(bead).EnchantUpgradeCount
                : (byte)0;
        }
    }
}
