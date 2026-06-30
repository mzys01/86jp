using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.Game.Premium
{
    public sealed class PremiumCatalog
    {
        private static PremiumCatalog _cached;
        private readonly Dictionary<int, PremiumEntry> _byItemCode;

        private PremiumCatalog(Dictionary<int, PremiumEntry> byItemCode)
        {
            _byItemCode = byItemCode;
        }

        public static PremiumCatalog Load()
        {
            return _cached ?? (_cached = Parse(PvfArchiveAccessor.ReadText("etc/premiumlist_new.etc")));
        }

        public bool TryGetValue(int itemCode, out int premiumType, out int durationDays)
        {
            premiumType = 0;
            durationDays = 0;
            if (!_byItemCode.TryGetValue(itemCode, out var entry))
                return false;

            premiumType = entry.PremiumType;
            durationDays = entry.DurationDays;
            return true;
        }

        internal static PremiumCatalog Parse(string text)
        {
            var tokens = Tokenize(text ?? string.Empty);
            var map = new Dictionary<int, PremiumEntry>();
            var premiumType = 0;
            for (var i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i] == "[type]" && int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
                {
                    premiumType = type;
                    continue;
                }

                if (premiumType <= 0 || tokens[i] != "[item]" || i + 4 >= tokens.Count)
                    continue;

                if (!int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemCode)
                    || tokens[i + 2] != "[term]"
                    || !int.TryParse(tokens[i + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
                    continue;

                map[itemCode] = new PremiumEntry { PremiumType = premiumType, DurationDays = days };
            }

            return new PremiumCatalog(map);
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            for (var i = 0; i < text.Length;)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    i++;
                    continue;
                }

                var end = i + 1;
                if (text[i] == '[')
                    end = text.IndexOf(']', i + 1) + 1;
                else if (text[i] == '`')
                {
                    end = text.IndexOf('`', i + 1) + 1;
                    if (end <= 0) end = text.Length;
                    i = end;
                    continue;
                }
                else
                    while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '[')
                        end++;

                if (end <= i)
                    break;
                tokens.Add(text.Substring(i, end - i));
                i = end;
            }
            return tokens;
        }

        private sealed class PremiumEntry
        {
            public int PremiumType { get; set; }
            public int DurationDays { get; set; }
        }
    }
}
