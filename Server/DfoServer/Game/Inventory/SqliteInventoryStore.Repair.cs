using DfoServer.Game.Currency;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        // 修理装备入口。listType 由 inven_type 映射而来:
        //   Main(0)=背包/快捷栏(character_items), Equipment(3)=穿戴(character_equipped_entries)。
        // slotIndex=-1: 全部修理(仅穿戴装备); 否则修理指定槽。
        public bool TryRepairEquipment(int characterId, int accountId, InventoryListType listType, short slotIndex, bool quickRepair, bool freeRepair, out RepairEquipmentResult result)
        {
            result = null;
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var ok = TryRepairEquipmentCore(connection, transaction,
                        characterId, listType, slotIndex, quickRepair, freeRepair, out result);
                    if (ok) transaction.Commit();
                    return ok;
                }
            }
        }

        private bool TryRepairEquipmentCore(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId,
            InventoryListType listType, short slotIndex, bool quickRepair, bool freeRepair,
            out RepairEquipmentResult result)
        {
            result = null;
            var wallet = CurrencyService.LoadWallet(connection, transaction, characterId);
            FileLogger.Log($"[Repair] cid={characterId} listType={listType} slot={slotIndex} quick={quickRepair} free={freeRepair} walletGold={wallet.Gold}");

            if (slotIndex == -1)
                return TryRepairAll(connection, transaction, characterId, wallet, quickRepair, freeRepair, out result);

            // listType=Equipment(3): 穿戴装备 → character_equipped_entries
            // listType=Main(0): 背包装备 → character_items
            if (listType == InventoryListType.Equipment)
                return TryRepairSingleEquipped(connection, transaction, characterId, slotIndex, wallet, quickRepair, freeRepair, out result);

            return TryRepairSingle(connection, transaction, characterId, slotIndex, wallet, quickRepair, freeRepair, out result);
        }

        private bool TryRepairSingleEquipped(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId, short slotIndex,
            WalletSnapshot wallet, bool quickRepair, bool freeRepair, out RepairEquipmentResult result)
        {
            result = null;

            var entry = LoadEquippedEntry(connection, transaction, characterId, slotIndex);
            if (entry == null)
            {
                FileLogger.Log($"[Repair] Equipped slot={slotIndex} not found in character_equipped_entries");
                return false;
            }

            var item = InvenItem.Parse(entry.Raw);

            if (!ItemMetadataResolver.TryLoadEquipmentFile(entry.ItemId, out var equ))
            {
                FileLogger.Log($"[Repair] Equipped slot={slotIndex} itemId=0x{entry.ItemId:X8} not in equipment.lst");
                return false;
            }

            // 无 [durability] 词条(默认-1)=不可修理装备, 拒绝
            if (equ.Durability < 0)
            {
                FileLogger.Log($"[Repair] Equipped slot={slotIndex} itemId=0x{entry.ItemId:X8} no durability field, not repairable");
                return false;
            }
            var maxDura = equ.Durability;
            FileLogger.Log($"[Repair] Equipped slot={slotIndex} itemId=0x{entry.ItemId:X8} curDura={item.Durability} maxDura={maxDura}");
            if (item.Durability >= maxDura)
            {
                result = new RepairEquipmentResult { SlotIndex = slotIndex, UpdatedGold = wallet.Gold };
                return true;
            }

            var cost = freeRepair ? 0 : EquipmentRepairPriceProvider.CalcRepairCost(equ.RepairPrice, equ.Grade, maxDura, item.Durability, item.EnchantUpgradeCount, quickRepair);
            if (!CurrencyService.TrySpendGold(connection, transaction, characterId, cost))
                return false;

            var newGold = wallet.Gold - cost;
            // 原地只改耐久2字节, 保留装备强化/属性
            UpdateEquippedEntryRaw(connection, transaction, characterId, slotIndex, PatchDurabilityInPlace(entry.Raw, (ushort)maxDura));

            result = new RepairEquipmentResult { SlotIndex = slotIndex, UpdatedGold = newGold, Cost = cost };
            return true;
        }

        // 只覆盖 raw_entry 的耐久字段(offset 10-11, UInt16 LE), 其余字节原样保留。
        private static byte[] PatchDurabilityInPlace(byte[] raw, ushort newDurability)
        {
            var copy = (byte[])raw.Clone();
            copy[10] = (byte)(newDurability & 0xFF);
            copy[11] = (byte)((newDurability >> 8) & 0xFF);
            return copy;
        }

        private bool TryRepairSingle(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId, short slotIndex,
            WalletSnapshot wallet, bool quickRepair, bool freeRepair, out RepairEquipmentResult result)
        {
            result = null;

            // 单个修理: 装备在背包(character_items Main list), 读 item_template_id 和 durability
            var bagItem = LoadBagEquipmentEntry(connection, transaction, characterId, slotIndex);
            if (bagItem == null)
            {
                FileLogger.Log($"[Repair] Single slot={slotIndex} entry not found in character_items");
                return false;
            }

            if (!ItemMetadataResolver.TryLoadEquipmentFile(bagItem.ItemId, out var equ))
            {
                FileLogger.Log($"[Repair] Single slot={slotIndex} itemId=0x{bagItem.ItemId:X8} TryLoadEquipmentFile=false");
                return false;
            }

            // 无 [durability] 词条(默认-1)=不可修理装备, 拒绝
            if (equ.Durability < 0)
            {
                FileLogger.Log($"[Repair] Single slot={slotIndex} itemId=0x{bagItem.ItemId:X8} no durability field, not repairable");
                return false;
            }
            var maxDura = equ.Durability;
            FileLogger.Log($"[Repair] Single slot={slotIndex} itemId=0x{bagItem.ItemId:X8} curDura={bagItem.Durability} maxDura={maxDura} repairPrice={equ.RepairPrice} grade={equ.Grade}");
            if (bagItem.Durability >= maxDura)
            {
                result = new RepairEquipmentResult { SlotIndex = slotIndex, UpdatedGold = wallet.Gold };
                return true;
            }

            var cost = freeRepair ? 0 : EquipmentRepairPriceProvider.CalcRepairCost(
                equ.RepairPrice, equ.Grade, maxDura, bagItem.Durability, 0, quickRepair);
            if (!CurrencyService.TrySpendGold(connection, transaction, characterId, cost))
                return false;

            var newGold = wallet.Gold - cost;

            UpdateBagItemDurability(connection, transaction, characterId, slotIndex, (ushort)maxDura);

            _auditLogger.WriteAuditLog(connection, transaction, characterId, "repair_equipment",
                new ItemRecord
                {
                    ItemUid = bagItem.ItemUid, ListType = InventoryListType.Main,
                    SlotIndex = slotIndex, ItemTemplateId = bagItem.ItemId,
                    ItemKind = "equipment", StackCount = 1,
                },
                InventoryListType.Main, slotIndex, 1);

            result = new RepairEquipmentResult
            {
                SlotIndex = slotIndex,
                UpdatedGold = newGold,
                Cost = cost,
            };
            return true;
        }

        // 全部修理("一键修理"): 修穿戴装备 slot 11~22(character_equipped_entries) + 快捷栏 slot 3~8(character_items)。
        // 客户端全部修理收到 slot=0xFFFF 的 ACK 后自己把这些槽耐久本地拉满, 服务端只需扣钱+改DB耐久。
        // 装备类型过滤(下列13类)来自实测规则: 只修可修理装备, charm/装扮/称号/宠物跳过。
        // 适用类型: [weapon][coat][pants][hat][shoulder][waist][shoes][amulet][wrist][ring][support][aurora avatar][magic stone]
        private bool TryRepairAll(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId, WalletSnapshot wallet, bool quickRepair, bool freeRepair, out RepairEquipmentResult result)
        {
            result = null;

            // 待修项: IsEquipped=true→穿戴表(改raw_entry blob), false→快捷栏(改character_items.durability列)。
            var toRepair = new List<(int Slot, int ItemId, ushort MaxDura, bool IsEquipped, byte[] Raw)>();
            int totalCost = 0;

            // ── 穿戴装备 slot 11~22 (character_equipped_entries) ──
            foreach (var entry in LoadAllEquippedEntries(connection, transaction, characterId))
            {
                if (entry.Slot < 11 || entry.Slot > 22) continue;   // 只修 11~22 穿戴槽
                if (!ItemMetadataResolver.IsRepairAllEligible(entry.ItemId)) continue;   // 只修13类; charm/装扮等跳过
                if (!ItemMetadataResolver.TryLoadEquipmentFile(entry.ItemId, out var equ)) continue;
                if (equ.Durability < 0) continue;   // 无 [durability] 词条=不可修理

                var item = InvenItem.Parse(entry.Raw);
                if (item.Durability >= equ.Durability) continue;   // 已满

                var cost = freeRepair ? 0 : EquipmentRepairPriceProvider.CalcRepairCost(
                    equ.RepairPrice, equ.Grade, equ.Durability, item.Durability, item.EnchantUpgradeCount, quickRepair);
                FileLogger.Log($"[Repair] All equipped slot={entry.Slot} itemId=0x{entry.ItemId:X8} curDura={item.Durability} maxDura={equ.Durability} cost={cost}");
                toRepair.Add((entry.Slot, entry.ItemId, (ushort)equ.Durability, true, entry.Raw));
                totalCost += cost;
            }

            // ── 快捷栏 slot 3~8 (character_items list_type=0), 客户端全部修理也扫这段 ──
            for (int slot = 3; slot <= 8; slot++)
            {
                var bagItem = LoadBagEquipmentEntry(connection, transaction, characterId, slot);
                if (bagItem == null) continue;
                if (!ItemMetadataResolver.IsRepairAllEligible(bagItem.ItemId)) continue;   // 只修13类; charm跳过
                if (!ItemMetadataResolver.TryLoadEquipmentFile(bagItem.ItemId, out var equ)) continue;
                if (equ.Durability < 0) continue;
                if (bagItem.Durability >= equ.Durability) continue;   // 已满

                var cost = freeRepair ? 0 : EquipmentRepairPriceProvider.CalcRepairCost(
                    equ.RepairPrice, equ.Grade, equ.Durability, bagItem.Durability, 0, quickRepair);
                FileLogger.Log($"[Repair] All quickslot slot={slot} itemId=0x{bagItem.ItemId:X8} curDura={bagItem.Durability} maxDura={equ.Durability} cost={cost}");
                toRepair.Add((slot, bagItem.ItemId, (ushort)equ.Durability, false, null));
                totalCost += cost;
            }

            FileLogger.Log($"[Repair] All: {toRepair.Count} items totalCost={totalCost} walletGold={wallet.Gold}");

            // 全满或无可修装备: 成功但金币不变。
            if (toRepair.Count == 0)
            {
                result = new RepairEquipmentResult { UpdatedGold = wallet.Gold, Cost = 0 };
                return true;
            }

            if (!CurrencyService.TrySpendGold(connection, transaction, characterId, totalCost))
                return false;

            var newGold = wallet.Gold - totalCost;

            foreach (var (slot, itemId, maxDura, isEquipped, raw) in toRepair)
            {
                if (isEquipped)
                    UpdateEquippedEntryRaw(connection, transaction, characterId, slot, PatchDurabilityInPlace(raw, maxDura));
                else
                    UpdateBagItemDurability(connection, transaction, characterId, slot, maxDura);

                _auditLogger.WriteAuditLog(connection, transaction, characterId, "repair_equipment",
                    new ItemRecord
                    {
                        ItemUid = 0,
                        ListType = isEquipped ? InventoryListType.Equipment : InventoryListType.Main,
                        SlotIndex = (short)slot, ItemTemplateId = itemId,
                        ItemKind = "equipment", StackCount = 1,
                    },
                    isEquipped ? InventoryListType.Equipment : InventoryListType.Main, (short)slot, 1);
            }

            // 全部修理只发一个 slot=0xFFFF 的 ACK, money=扣完总开销的最终余额。
            result = new RepairEquipmentResult { UpdatedGold = newGold, Cost = totalCost };
            return true;
        }

        // ── helpers ──

        private sealed class BagEquipmentEntry
        {
            public long ItemUid;
            public int ItemId;
            public ushort Durability;
        }

        private BagEquipmentEntry LoadBagEquipmentEntry(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int slot)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT item_uid, item_template_id, durability FROM character_items WHERE character_id=@cid AND list_type=0 AND slot_index=@slot AND item_kind='equipment'";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", slot);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new BagEquipmentEntry
                    {
                        ItemUid = r.GetInt64(0),
                        ItemId = r.GetInt32(1),
                        Durability = (ushort)r.GetInt32(2),
                    };
                }
            }
        }

        private static void UpdateBagItemDurability(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int slot, ushort newDurability)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE character_items SET durability=@dur WHERE character_id=@cid AND list_type=0 AND slot_index=@slot";
                cmd.Parameters.AddWithValue("@dur", newDurability);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", slot);
                cmd.ExecuteNonQuery();
            }
        }

        private sealed class EquippedEntryRecord
        {
            public int Slot;
            public int ItemId;
            public byte[] Raw;
        }

        private EquippedEntryRecord LoadEquippedEntry(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int slot)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT slot, item_id, raw_entry FROM character_equipped_entries WHERE character_id=@cid AND slot=@slot";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", slot);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new EquippedEntryRecord
                    {
                        Slot = r.GetInt32(0),
                        ItemId = r.GetInt32(1),
                        Raw = (byte[])r.GetValue(2),
                    };
                }
            }
        }

        private List<EquippedEntryRecord> LoadAllEquippedEntries(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId)
        {
            var list = new List<EquippedEntryRecord>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT slot, item_id, raw_entry FROM character_equipped_entries WHERE character_id=@cid ORDER BY slot";
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new EquippedEntryRecord
                        {
                            Slot = r.GetInt32(0),
                            ItemId = r.GetInt32(1),
                            Raw = (byte[])r.GetValue(2),
                        });
                }
            }
            return list;
        }

        private static void UpdateEquippedEntryRaw(
            SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int slot, byte[] raw)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE character_equipped_entries SET raw_entry=@raw WHERE character_id=@cid AND slot=@slot";
                cmd.Parameters.AddWithValue("@raw", raw);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", slot);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
