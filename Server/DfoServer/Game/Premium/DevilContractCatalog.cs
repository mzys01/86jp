using DfoServer.GameWorld;
using DfoServer.Network;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.Game.Premium
{
    public sealed class DevilContractCatalog
    {
        private static DevilContractCatalog _cached;
        private readonly Dictionary<int, SlotEntry> _byCommodityNo;

        private DevilContractCatalog(Dictionary<int, SlotEntry> byCommodityNo)
        {
            _byCommodityNo = byCommodityNo;
        }

        public static DevilContractCatalog Load()
        {
            return _cached ?? (_cached = Parse(PvfArchiveAccessor.ReadText("etc/cerashop.etc")));
        }

        public bool TryGetSlot(int commodityNo, out int slotIndex, out int durationDays, out int ceraPrice)
        {
            slotIndex = -1;
            durationDays = 0;
            ceraPrice = 0;
            if (!_byCommodityNo.TryGetValue(commodityNo, out var entry))
                return false;
            slotIndex = entry.SlotIndex;
            durationDays = entry.DurationDays;
            ceraPrice = entry.CeraPrice;
            return true;
        }

        // slot 0-7 的 premium_type 存储偏移，避免与 premiumlist_new.etc 的 type 冲突
        public const int SlotPremiumTypeBase = 580;

        public static int SlotToPremiumType(int slotIndex) => SlotPremiumTypeBase + slotIndex;
        public static bool IsDevilContractSlotType(int premiumType) => premiumType >= SlotPremiumTypeBase && premiumType < SlotPremiumTypeBase + 8;
        public static int PremiumTypeToSlot(int premiumType) => premiumType - SlotPremiumTypeBase;

        // "自动修理"服务 = 魔王契约 slot 6 (实测确认), premium_type=586。
        // 激活且未过期时装备修理免费。
        public const int AutoRepairSlotIndex = 6;
        public const int AutoRepairPremiumType = SlotPremiumTypeBase + AutoRepairSlotIndex;   // 586

        // ack_premium_blob 中魔王契約整体激活标记
        public const int ActivationPremiumType = 58;

        internal static DevilContractCatalog Parse(string text)
        {
            var section = ExtractSection(text ?? string.Empty, "selectable character premium");
            var tokens = Tokenize(section);
            var map = new Dictionary<int, SlotEntry>();

            for (var i = 0; i + 8 < tokens.Count; i += 9)
            {
                if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var commodityNo))
                    continue;
                if (commodityNo <= 0) continue;

                int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId);
                int.TryParse(tokens[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slotIndex);
                int.TryParse(tokens[i + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days);
                int.TryParse(tokens[i + 5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ceraPrice);

                if (slotIndex < 0 || slotIndex >= 8) continue;

                map[commodityNo] = new SlotEntry
                {
                    SlotIndex = slotIndex,
                    ItemId = itemId,
                    DurationDays = days,
                    CeraPrice = ceraPrice,
                };
            }

            FileLogger.Log($"[DevilContractCatalog] Loaded {map.Count} entries from [selectable character premium]");
            return new DevilContractCatalog(map);
        }

        private static string ExtractSection(string content, string section)
        {
            var startTag = "[" + section + "]";
            var endTag = "[/" + section + "]";
            var start = content.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            start += startTag.Length;
            var end = content.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
            return end > start ? content.Substring(start, end - start) : content.Substring(start);
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            for (var i = 0; i < text.Length;)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                if (text[i] == '`')
                {
                    var end = text.IndexOf('`', i + 1);
                    if (end < 0) end = text.Length - 1;
                    tokens.Add(text.Substring(i + 1, end - i - 1));
                    i = end + 1;
                    continue;
                }
                var s = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '`')
                    i++;
                tokens.Add(text.Substring(s, i - s));
            }
            return tokens;
        }

        private sealed class SlotEntry
        {
            public int SlotIndex;
            public int ItemId;
            public int DurationDays;
            public int CeraPrice;
        }
    }
}
