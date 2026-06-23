using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Dungeon
{
    public static class DungeonDropTable
    {
        public struct EquipEntry
        {
            public int ItemId;
            public int Rarity;
        }

        private static readonly Lazy<(int Min, int Max)[]> _levelRanges
            = new Lazy<(int, int)[]>(LoadDungeonDropInfo);

        private static readonly Lazy<Dictionary<int, EquipEntry[]>> _itemsByLevel
            = new Lazy<Dictionary<int, EquipEntry[]>>(LoadItemDictionary);

        public static EquipEntry[] GetEquipmentPool(int monsterLevel)
        {
            var ranges = _levelRanges.Value;
            if (monsterLevel < 1 || monsterLevel > ranges.Length)
                return null;

            var range = ranges[monsterLevel - 1];
            if (range.Min <= 0 || range.Max <= 0)
                return null;

            var items = _itemsByLevel.Value;
            var pool = new List<EquipEntry>();

            for (int lvl = range.Min; lvl <= range.Max; lvl++)
            {
                if (items.TryGetValue(lvl, out var arr))
                    pool.AddRange(arr);
            }

            return pool.Count > 0 ? pool.ToArray() : null;
        }

        private static (int Min, int Max)[] LoadDungeonDropInfo()
        {
            try
            {
                var text = GameWorld.PvfArchiveAccessor.ReadText("Etc/ItemDictionary/DungeonDropInfo.etc");
                var match = Regex.Match(text, @"\[normal table\]\s*([\s\S]*?)\s*\[/normal table\]");
                if (!match.Success)
                {
                    FileLogger.Log("[DungeonDropTable] DungeonDropInfo.etc: [normal table] not found");
                    return Array.Empty<(int, int)>();
                }

                var tokens = match.Groups[1].Value.Split(
                    new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var result = new (int, int)[99];
                for (int i = 0; i + 2 < tokens.Length; i += 3)
                {
                    if (int.TryParse(tokens[i], out var level)
                        && int.TryParse(tokens[i + 1], out var min)
                        && int.TryParse(tokens[i + 2], out var max)
                        && level >= 1 && level <= 99)
                    {
                        result[level - 1] = (min, max);
                    }
                }

                FileLogger.Log("[DungeonDropTable] Loaded DungeonDropInfo: 99 level ranges");
                return result;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonDropTable] Failed to load DungeonDropInfo: {ex.Message}");
                return Array.Empty<(int, int)>();
            }
        }

        private static Dictionary<int, EquipEntry[]> LoadItemDictionary()
        {
            var tempDict = new Dictionary<int, List<EquipEntry>>();
            try
            {
                var text = GameWorld.PvfArchiveAccessor.ReadText("Etc/ItemDictionary/ItemDictionary.etc");
                var allTokens = text.Split(
                    new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var nums = new List<int>(16);
                bool inName = false;

                for (int ti = 0; ti < allTokens.Length; ti++)
                {
                    var token = allTokens[ti];

                    if (inName)
                    {
                        if (token.IndexOf('`') >= 0)
                        {
                            inName = false;
                            AddEntry(nums, tempDict);
                            nums.Clear();
                        }
                        continue;
                    }

                    int bq = token.IndexOf('`');
                    if (bq >= 0)
                    {
                        int lastBq = token.LastIndexOf('`');
                        if (lastBq > bq)
                        {
                            AddEntry(nums, tempDict);
                            nums.Clear();
                        }
                        else
                        {
                            inName = true;
                        }
                        continue;
                    }

                    if (int.TryParse(token, out var n))
                        nums.Add(n);
                }

                var result = new Dictionary<int, EquipEntry[]>(tempDict.Count);
                int totalItems = 0;
                foreach (var kv in tempDict)
                {
                    result[kv.Key] = kv.Value.ToArray();
                    totalItems += kv.Value.Count;
                }

                FileLogger.Log($"[DungeonDropTable] Loaded ItemDictionary: {totalItems} items across {result.Count} drop levels");
                return result;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonDropTable] Failed to load ItemDictionary: {ex.Message}");
                return new Dictionary<int, EquipEntry[]>();
            }
        }

        private static void AddEntry(List<int> nums, Dictionary<int, List<EquipEntry>> dict)
        {
            if (nums.Count < 3) return;

            var itemId = nums[0];
            var rarity = nums[1];
            var dropLevel = nums[2];

            if (dropLevel <= 0) return;
            if (rarity > 1) return;

            if (!dict.TryGetValue(dropLevel, out var list))
            {
                list = new List<EquipEntry>();
                dict[dropLevel] = list;
            }
            list.Add(new EquipEntry { ItemId = itemId, Rarity = rarity });
        }
    }
}
