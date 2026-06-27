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
            if (command.BeadListType != InventoryListType.Main || command.TargetListType != InventoryListType.Main)
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

            var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, command.TargetSlotIndex);
            if (target == null || target.ItemKind != "equipment")
            {
                FileLogger.Log($"  [EnchantByBead] REJECT: invalid target slot={command.TargetSlotIndex} itemKind={target?.ItemKind ?? "null"}");
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);
                return false;
            }

            var enchantUpgradeCount = ReadEnchantUpgradeCount(bead.ExtraJson);
            if (!ItemMetadataResolver.TryValidateEnchantByBeadTarget(bead.ItemTemplateId, target.ItemTemplateId, enchantUpgradeCount, out var enchantCardItemId, out var rejectReason))
            {
                var errorCode = rejectReason != null && rejectReason.StartsWith("target", StringComparison.Ordinal)
                    ? EnchantByBeadResult.ErrorInvalidTarget
                    : EnchantByBeadResult.ErrorUnsupported;
                FileLogger.Log($"  [EnchantByBead] REJECT: bead=0x{bead.ItemTemplateId:X8} target=0x{target.ItemTemplateId:X8} upgrade={enchantUpgradeCount} reason={rejectReason}");
                result = EnchantByBeadResult.Error(command, errorCode);
                return false;
            }

            var targetCommon = _db.LoadCommonItem(connection, transaction, characterId, InventoryListType.Main, command.TargetSlotIndex);
            if (targetCommon == null)
            {
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);
                return false;
            }

            targetCommon.PrefixData0E = NormalizeBytes(targetCommon.PrefixData0E, 8);

            // 装备 common entry 的 +0x0E 前 4 字节承载附魔卡片 index，后 1 字节承载 86 卡片升级次数。
            BitConverter.GetBytes(enchantCardItemId).CopyTo(targetCommon.PrefixData0E, 0);
            targetCommon.PrefixData0E[4] = enchantUpgradeCount;
            _db.UpdateCommonExtraJson(connection, transaction, target.ItemUid, targetCommon);

            var remainingBeadCount = bead.StackCount - 1;
            CommonInventoryItem beadCommon;
            if (remainingBeadCount > 0)
            {
                _db.UpdateStackCount(connection, transaction, bead.ItemUid, remainingBeadCount);
                beadCommon = _db.LoadCommonItem(connection, transaction, characterId, InventoryListType.Main, command.BeadSlotIndex);
                if (beadCommon == null)
                    beadCommon = CreateEmptyCommonItem(command.BeadSlotIndex);
            }
            else
            {
                _db.DeleteItem(connection, transaction, bead.ItemUid);
                beadCommon = CreateEmptyCommonItem(command.BeadSlotIndex);
            }

            _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, bead, 1);
            _auditLogger.WriteEnchantAuditLog(connection, transaction, characterId, bead, target, enchantCardItemId, enchantUpgradeCount);

            FileLogger.Log($"  [EnchantByBead] OK: beadSlot={command.BeadSlotIndex} targetSlot={command.TargetSlotIndex} enchantCard=0x{enchantCardItemId:X8} upgrade={enchantUpgradeCount} beadLeft={Math.Max(0, remainingBeadCount)}");
            result = EnchantByBeadResult.Ok(command, targetCommon, beadCommon, enchantCardItemId);
            return true;
        }

        private static CommonInventoryItem CreateEmptyCommonItem(short slotIndex)
        {
            return new CommonInventoryItem
            {
                SlotIndex = slotIndex,
            };
        }

        private static byte[] NormalizeBytes(byte[] source, int expectedLength)
        {
            var buffer = new byte[expectedLength];
            if (source != null && source.Length > 0)
                Array.Copy(source, 0, buffer, 0, Math.Min(source.Length, expectedLength));
            return buffer;
        }

        private static byte ReadEnchantUpgradeCount(string extraJson)
        {
            // 86 附魔卡片升级次数跟随宝珠动态数据保存，写入装备时落到 common entry +0x12。
            var prefix = InventoryItemCodec.ReadHexValue(extraJson ?? string.Empty, "prefixData0E", 8);
            return prefix.Length >= 5 ? prefix[4] : (byte)0;
        }
    }
}
