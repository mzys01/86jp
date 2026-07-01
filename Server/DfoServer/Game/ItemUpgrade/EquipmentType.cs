using System;
using System.Collections.Generic;

namespace DfoServer.Game.ItemUpgrade
{
    public enum EquipmentType
    {
        Unknown = -1,
        HatAvatar = 0,
        HairAvatar = 1,
        FaceAvatar = 2,
        CoatAvatar = 3,
        PantsAvatar = 4,
        ShoesAvatar = 5,
        BreastAvatar = 6,
        WaistAvatar = 7,
        SkinAvatar = 8,
        AuroraAvatar = 9,
        Weapon = 10,
        TitleName = 11,
        Coat = 12,
        Shoulder = 13,
        Pants = 14,
        Shoes = 15,
        Waist = 16,
        Amulet = 17,
        Wrist = 18,
        Ring = 19,
        Support = 20,
        MagicStone = 21,
        Creature = 22,
        ArtifactRed = 23,
        ArtifactBlue = 24,
        ArtifactGreen = 25,
    }

    public static class EquipmentTypeInfo
    {
        private static readonly Dictionary<string, EquipmentType> TextToType = new Dictionary<string, EquipmentType>(StringComparer.OrdinalIgnoreCase)
        {
            ["[hat avatar]"] = EquipmentType.HatAvatar,
            ["[hair avatar]"] = EquipmentType.HairAvatar,
            ["[face avatar]"] = EquipmentType.FaceAvatar,
            ["[coat avatar]"] = EquipmentType.CoatAvatar,
            ["[pants avatar]"] = EquipmentType.PantsAvatar,
            ["[shoes avatar]"] = EquipmentType.ShoesAvatar,
            ["[breast avatar]"] = EquipmentType.BreastAvatar,
            ["[waist avatar]"] = EquipmentType.WaistAvatar,
            ["[skin avatar]"] = EquipmentType.SkinAvatar,
            ["[aurora avatar]"] = EquipmentType.AuroraAvatar,
            ["[weapon]"] = EquipmentType.Weapon,
            ["[title name]"] = EquipmentType.TitleName,
            ["[coat]"] = EquipmentType.Coat,
            ["[shoulder]"] = EquipmentType.Shoulder,
            ["[pants]"] = EquipmentType.Pants,
            ["[shoes]"] = EquipmentType.Shoes,
            ["[waist]"] = EquipmentType.Waist,
            ["[amulet]"] = EquipmentType.Amulet,
            ["[wrist]"] = EquipmentType.Wrist,
            ["[ring]"] = EquipmentType.Ring,
            ["[support]"] = EquipmentType.Support,
            ["[magic stone]"] = EquipmentType.MagicStone,
            ["[creature]"] = EquipmentType.Creature,
            ["[artifact red]"] = EquipmentType.ArtifactRed,
            ["[artifact blue]"] = EquipmentType.ArtifactBlue,
            ["[artifact green]"] = EquipmentType.ArtifactGreen,
        };

        private static readonly Dictionary<EquipmentType, string> TypeToText = BuildReverseMap();

        public static bool TryParse(string raw, out EquipmentType type)
        {
            type = EquipmentType.Unknown;
            var token = NormalizeToken(raw);
            return !string.IsNullOrEmpty(token) && TextToType.TryGetValue(token, out type);
        }

        public static EquipmentType ParseOrUnknown(string raw)
        {
            return TryParse(raw, out var type) ? type : EquipmentType.Unknown;
        }

        public static string ToPvfToken(EquipmentType type)
        {
            return TypeToText.TryGetValue(type, out var token) ? token : null;
        }

        public static bool IsWeapon(EquipmentType type)
        {
            return type == EquipmentType.Weapon;
        }

        public static bool IsArmor(EquipmentType type)
        {
            return type == EquipmentType.Coat
                || type == EquipmentType.Shoulder
                || type == EquipmentType.Pants
                || type == EquipmentType.Shoes
                || type == EquipmentType.Waist;
        }

        public static bool IsAccessory(EquipmentType type)
        {
            return type == EquipmentType.Amulet
                || type == EquipmentType.Wrist
                || type == EquipmentType.Ring;
        }

        public static bool IsSpecialEquipment(EquipmentType type)
        {
            return type == EquipmentType.Support || type == EquipmentType.MagicStone;
        }

        public static bool IsUpgradeTargetType(EquipmentType type)
        {
            return IsWeapon(type) || IsArmor(type) || IsAccessory(type) || IsSpecialEquipment(type);
        }

        public static bool MatchesSlotRestriction(EquipmentType type, int slotRestriction)
        {
            switch (slotRestriction)
            {
                case 0:
                    return true;
                case 1:
                    return IsWeapon(type);
                case 2:
                    return IsArmor(type);
                case 3:
                    return IsAccessory(type);
                default:
                    return false;
            }
        }

        private static string NormalizeToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var text = raw.Trim().Trim('`').Trim().ToLowerInvariant();
            var start = text.IndexOf('[', StringComparison.Ordinal);
            var end = start >= 0 ? text.IndexOf(']', start + 1) : -1;
            if (start >= 0 && end > start)
                return text.Substring(start, end - start + 1);

            return text;
        }

        private static Dictionary<EquipmentType, string> BuildReverseMap()
        {
            var map = new Dictionary<EquipmentType, string>();
            foreach (var pair in TextToType)
            {
                if (!map.ContainsKey(pair.Value))
                    map[pair.Value] = pair.Key;
            }

            return map;
        }
    }
}
