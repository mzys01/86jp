using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Inventory
{
    public sealed class ItemMetadata
    {
        public string ItemKind { get; set; } = "unknown";

        public string StackableType { get; set; }

        public string PvfFilePath { get; set; }

        public int BuyGold { get; set; }

        public int BuyCoin { get; set; }

        public int SellGold { get; set; }

        public ushort Durability { get; set; }

        public int StackLimit { get; set; }

        public int NeedMaterialId { get; set; }

        public int NeedMaterialCount { get; set; }

        public int Grade { get; set; }

        public bool IsStackable => string.Equals(ItemKind, "stackable", StringComparison.Ordinal);

        public bool IsMaterialExchange => NeedMaterialId > 0 && NeedMaterialCount > 0;

        public void GetSlotRange(out int slotStart, out int slotEnd)
        {
            if (string.Equals(ItemKind, "equipment", StringComparison.Ordinal))
            {
                slotStart = 9; slotEnd = 64; return;
            }
            if (StackableType == null)
            {
                slotStart = 65; slotEnd = 120; return;
            }
            var st = StackableType.Replace("`", "").Trim().ToLowerInvariant();
            if (st.StartsWith("[material]"))
            {
                if (st.EndsWith("4"))
                { slotStart = 345; slotEnd = 359; }
                else
                { slotStart = 121; slotEnd = 176; }
                return;
            }
            if (st.StartsWith("[quest]"))
            { slotStart = 177; slotEnd = 232; return; }
            if (st.StartsWith("[material expert job]"))
            { slotStart = 233; slotEnd = 288; return; }
            if (st.StartsWith("[avatar emblem]"))
            { slotStart = 289; slotEnd = 344; return; }
            slotStart = 65; slotEnd = 120;
        }
    }

    internal sealed class ItemSellRates
    {
        public int Equipment { get; set; } = 200;  
        public int NonStackable { get; set; } = 150; 
        public int Stackable { get; set; } = 30;    

        public static ItemSellRates Parse(string content)
        {
            var r = new ItemSellRates();
            if (string.IsNullOrEmpty(content)) return r;
            
            var m = System.Text.RegularExpressions.Regex.Match(content, @"\]\s*\r?\n\s*(\d+)\s+(\d+)\s+(\d+)");
            if (m.Success)
            {
                r.Equipment = int.Parse(m.Groups[1].Value);
                r.NonStackable = int.Parse(m.Groups[2].Value);
                r.Stackable = int.Parse(m.Groups[3].Value);
            }
            return r;
        }
    }

    public static class ItemMetadataResolver 
    {
        internal static readonly Lazy<LstFile> EquipmentList = new Lazy<LstFile>(() => LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst")));
        private static readonly Lazy<LstFile> StackableList = new Lazy<LstFile>(() => LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst")));
        private static readonly Lazy<ItemSellRates> SellRates = new Lazy<ItemSellRates>(() => ItemSellRates.Parse(PvfArchiveAccessor.ReadText("equipment/pricetable.tbl")));

        public static ItemMetadata Resolve(int itemTemplateId)
        {
            var equipmentEntry = EquipmentList.Value.GetById(itemTemplateId);
            if (equipmentEntry != null)
            {
                var equipment = EquipmentFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("equipment", equipmentEntry.FilePath)));
                var buyGold = Math.Max(0, equipment.Price >= 0 ? equipment.Price : equipment.Value);
                
                
                var baseSellPrice = equipment.Value >= 0 ? equipment.Value : buyGold;
                var sellGold = Math.Max(1, baseSellPrice * SellRates.Value.Equipment / 1000);
                var durability = equipment.Durability > 0 ? equipment.Durability : 45;

                return new ItemMetadata
                {
                    ItemKind = "equipment",
                    PvfFilePath = equipmentEntry.FilePath,
                    BuyGold = buyGold,
                    SellGold = sellGold,
                    Durability = (ushort)durability,
                    StackLimit = 1,
                    Grade = equipment.Grade,
                };
            }

            var stackableEntry = StackableList.Value.GetById(itemTemplateId);
            if (stackableEntry != null)
            {
                var stackable = StackableItemFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("stackable", stackableEntry.FilePath)));
                var buyGold = Math.Max(0, stackable.Price >= 0 ? stackable.Price : stackable.Value);
                
                
                
                
                var baseSellPrice = stackable.Value >= 0 ? stackable.Value : buyGold;
                var sellGold = Math.Max(1, baseSellPrice * SellRates.Value.Equipment / 1000);

                int needMatId = 0, needMatCount = 0;
                if (!string.IsNullOrWhiteSpace(stackable.NeedMaterial))
                {
                    var parts = stackable.NeedMaterial.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        int.TryParse(parts[0], out needMatId);
                        int.TryParse(parts[1], out needMatCount);
                    }
                }

                
                
                var hasMaterialCost = needMatId > 0 && needMatCount > 0;
                return new ItemMetadata
                {
                    ItemKind = "stackable",
                    StackableType = stackable.StackableType,
                    PvfFilePath = stackableEntry.FilePath,
                    BuyGold = hasMaterialCost ? 0 : buyGold,
                    SellGold = sellGold,
                    Durability = 0,
                    StackLimit = stackable.StackLimit,
                    NeedMaterialId = needMatId,
                    NeedMaterialCount = needMatCount,
                };
            }

            return new ItemMetadata
            {
                ItemKind = "special",
                BuyGold = 0,
                SellGold = 1,
                Durability = 0,
                StackLimit = 1,
            };
        }

        public static LstEntry GetStackableEntry(int itemTemplateId)
        {
            return StackableList.Value.GetById(itemTemplateId);
        }

        public static bool TryValidateEnchantByBeadTarget(int beadItemTemplateId, int targetItemTemplateId, byte enchantUpgradeCount, out int enchantCardItemId, out string rejectReason)
        {
            enchantCardItemId = 0;
            rejectReason = null;

            if (!TryLoadStackable(beadItemTemplateId, out var bead))
            {
                rejectReason = "bead is not found in stackable.lst";
                return false;
            }

            if (bead.MonsterCardId > 0)
                enchantCardItemId = bead.MonsterCardId;
            else if (bead.EnchantIndex > 0)
                enchantCardItemId = bead.EnchantIndex;
            else
            {
                rejectReason = "bead has no monster card id/enchant index";
                return false;
            }

            // 宝珠直接声明 target item id 时，只有白名单装备能被附魔。
            if (bead.TargetItemIds != null && bead.TargetItemIds.Count > 0 && !bead.TargetItemIds.Contains(targetItemTemplateId))
            {
                rejectReason = "target item id is not allowed by bead target item id";
                return false;
            }

            if (!TryGetEquipmentType(targetItemTemplateId, out var targetEquipmentType))
            {
                rejectReason = "target is not found in equipment.lst";
                return false;
            }

            StackableItemFile card = null;
            if (bead.MonsterCardId > 0)
            {
                if (!TryLoadStackable(bead.MonsterCardId, out card))
                {
                    rejectReason = "monster card is not found in stackable.lst";
                    return false;
                }
            }
            else
            {
                TryLoadStackable(enchantCardItemId, out card);
            }

            if (card != null)
            {
                // monster card 的 string data: 第一个是图片资源，后续是允许附魔的 equipment type。
                var allowedTypes = ExtractAllowedEquipmentTypes(card.StringDataItems);
                if (allowedTypes.Count > 0 && !allowedTypes.Contains(targetEquipmentType))
                {
                    rejectReason = "target equipment type is not allowed by monster card string data";
                    return false;
                }

                if (card.EnchantTable.Count > 0 && !card.EnchantTable.Contains(enchantUpgradeCount))
                {
                    rejectReason = "enchant upgrade count is not allowed by monster card enchant table";
                    return false;
                }

                if (card.EnchantTable.Count == 0 && enchantUpgradeCount != 0)
                {
                    rejectReason = "monster card has no enchant table for upgraded bead";
                    return false;
                }
            }

            if (card == null && enchantUpgradeCount != 0)
            {
                rejectReason = "upgraded enchant bead requires monster card enchant table";
                return false;
            }

            return true;
        }

        private static bool TryLoadStackable(int itemTemplateId, out StackableItemFile stackable)
        {
            stackable = null;
            var stackableEntry = StackableList.Value.GetById(itemTemplateId);
            if (stackableEntry == null)
                return false;

            stackable = StackableItemFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("stackable", stackableEntry.FilePath)));
            return true;
        }

        private static bool TryGetEquipmentType(int itemTemplateId, out string equipmentType)
        {
            equipmentType = null;
            var equipmentEntry = EquipmentList.Value.GetById(itemTemplateId);
            if (equipmentEntry == null)
                return false;

            var equipment = EquipmentFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("equipment", equipmentEntry.FilePath)));
            equipmentType = NormalizeEquipmentType(equipment.EquipmentType);
            return !string.IsNullOrWhiteSpace(equipmentType);
        }

        private static HashSet<string> ExtractAllowedEquipmentTypes(List<string> stringDataItems)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (stringDataItems == null || stringDataItems.Count <= 1)
                return result;

            for (var i = 1; i < stringDataItems.Count; i++)
            {
                var normalized = NormalizeEquipmentType(stringDataItems[i]);
                if (!string.IsNullOrWhiteSpace(normalized))
                    result.Add(normalized);
            }

            return result;
        }

        private static string NormalizeEquipmentType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var start = raw.IndexOf('[', StringComparison.Ordinal);
            var end = start >= 0 ? raw.IndexOf(']', start + 1) : -1;
            if (start < 0 || end <= start)
                return raw.Trim('`', ' ', '\t', '\r', '\n').ToLowerInvariant();

            return raw.Substring(start, end - start + 1).ToLowerInvariant();
        }

        /// <summary>
        /// 判断物品是否为克隆装扮。克隆装扮的 PVF [item category] 段值为 "clear avatar"。
        /// </summary>
        public static bool IsCloneAvatarItem(int itemTemplateId)
        {
            var equipmentEntry = EquipmentList.Value.GetById(itemTemplateId);
            if (equipmentEntry == null) return false;
            var equipment = EquipmentFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("equipment", equipmentEntry.FilePath)));
            return string.Equals(equipment.ItemCategory, "clear avatar", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPetInventoryEquipment(int itemTemplateId)
        {
            return CreatureExtraResolver.IsPetInventoryEquipment(itemTemplateId);
        }

        public static bool IsPetConsumableItem(ItemMetadata metadata)
        {
            if (metadata == null || !metadata.IsStackable || string.IsNullOrWhiteSpace(metadata.StackableType))
                return false;
            var stackableType = metadata.StackableType.Replace("`", "").Trim();
            return stackableType.StartsWith("[creature]", StringComparison.OrdinalIgnoreCase)
                || stackableType.StartsWith("[feed]", StringComparison.OrdinalIgnoreCase);
        }
    }
}
