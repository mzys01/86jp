using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    /// 校验并解析 chn_rental 背包 ID；发放等级以客户端 0x0372 包内模板 ID 为准。
    public static class RentalWeaponInventoryMapper
    {
        private sealed class RentalWeaponIdentity
        {
            public string SeriesKey { get; set; }
            public int StarPrice { get; set; }
        }

        private static readonly Lazy<Dictionary<int, RentalWeaponIdentity>> IdentityById =
            new Lazy<Dictionary<int, RentalWeaponIdentity>>(BuildIdentityIndex);

        public static bool IsValidInventoryTemplate(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return false;

            return IdentityById.Value.ContainsKey(itemTemplateId);
        }

        public static string GetSeriesKey(int inventoryTemplateId)
        {
            if (IdentityById.Value.TryGetValue(inventoryTemplateId, out var identity))
                return identity.SeriesKey;

            return "unknown|" + inventoryTemplateId;
        }

        public static int GetStarPrice(int inventoryTemplateId)
        {
            if (IdentityById.Value.TryGetValue(inventoryTemplateId, out var identity) && identity.StarPrice > 0)
                return identity.StarPrice;

            var buyGold = ItemMetadataResolver.Resolve(inventoryTemplateId).BuyGold;
            return buyGold > 0 ? buyGold : 0;
        }

        private static Dictionary<int, RentalWeaponIdentity> BuildIdentityIndex()
        {
            var byId = new Dictionary<int, RentalWeaponIdentity>();
            var lst = LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst"));
            foreach (var entry in lst.Entries)
            {
                if (entry.FilePath.IndexOf("chn_rental_", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var weaponFolder = GetWeaponFolder(entry.FilePath);
                if (weaponFolder == null)
                    continue;

                var equipment = EquipmentFile.Parse(
                    PvfArchiveAccessor.ReadText(Path.Combine("equipment", entry.FilePath)));
                var name = (equipment.Name ?? string.Empty).Trim();
                if (name.Length == 0)
                    continue;

                byId[entry.Id] = new RentalWeaponIdentity
                {
                    SeriesKey = weaponFolder + "|" + name,
                    StarPrice = equipment.Price,
                };
            }

            return byId;
        }

        private static string GetWeaponFolder(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            var normalized = filePath.Replace('\\', '/');
            var weaponIndex = normalized.IndexOf("/weapon/", StringComparison.OrdinalIgnoreCase);
            if (weaponIndex < 0)
                return null;

            var slashAfterType = normalized.IndexOf('/', weaponIndex + "/weapon/".Length);
            if (slashAfterType < 0)
                return null;

            return normalized.Substring(0, slashAfterType + 1);
        }
    }
}
