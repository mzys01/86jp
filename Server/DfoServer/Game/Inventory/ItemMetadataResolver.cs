using DfoServer.GameWorld;
using PvfLib;
using System;
using System.IO;

namespace DfoServer.Game.Inventory
{
    public sealed class ItemMetadata
    {
        public string ItemKind { get; set; } = "unknown";

        public string StackableType { get; set; }

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
        private static readonly Lazy<LstFile> EquipmentList = new Lazy<LstFile>(() => LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst")));
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
    }
}