using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.TitleBook
{
    public sealed class TitleBookStaticDataProvider
    {
        private static readonly string[] CategoryNames = { "general", "specific", "pvp", "despair", "event" };
        private static readonly int[] DefaultCapacities = { 80, 170, 50, 100, 100 };

        private readonly Dictionary<(int Category, int Index), TitleBookSlotDefinition> _slots;
        private readonly Dictionary<int, TitleQuestDefinition> _quests;

        private TitleBookStaticDataProvider(
            Dictionary<(int Category, int Index), TitleBookSlotDefinition> slots,
            Dictionary<int, TitleQuestDefinition> quests)
        {
            _slots = slots;
            _quests = quests;
        }

        public static IReadOnlyList<int> CategoryCapacities => DefaultCapacities;

        public static TitleBookStaticDataProvider LoadDefault()
        {
            var path = ResolveTitleBookEtcPath();
            var quests = ParseTitleQuestDefinitions();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return new TitleBookStaticDataProvider(ParseTitleBookEtc(path), quests);

            return new TitleBookStaticDataProvider(BuildOpenFallback(), quests);
        }

        public TitleBookSlotDefinition GetSlot(int category, int index)
        {
            if (_slots.TryGetValue((category, index), out var definition))
                return definition;

            return new TitleBookSlotDefinition
            {
                Category = category,
                Index = index,
                SlotType = -1,
                QuestId = -1,
            };
        }

        public bool TryFindByQuestId(int questId, out TitleBookSlotDefinition definition)
        {
            definition = _slots.Values.FirstOrDefault(s => s.IsOpen && s.QuestId == questId);
            return definition != null;
        }

        public TitleQuestDefinition GetQuest(int questId)
        {
            return _quests.TryGetValue(questId, out var quest) ? quest : null;
        }

        private static Dictionary<(int Category, int Index), TitleBookSlotDefinition> ParseTitleBookEtc(string path)
        {
            var slots = BuildClosedDefaults();
            var inSection = false;
            var currentCategory = -1;

            foreach (var originalLine in File.ReadLines(path))
            {
                var line = StripComment(originalLine).Replace("`", "").Trim();
                if (line.Length == 0)
                    continue;

                if (line.Equals("[title collection info]", StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    currentCategory = -1;
                    continue;
                }

                if (!inSection)
                    continue;

                if (line.Equals("[/title collection info]", StringComparison.OrdinalIgnoreCase))
                {
                    inSection = false;
                    currentCategory = -1;
                    continue;
                }

                if (line.StartsWith("[/", StringComparison.Ordinal))
                    continue;

                var tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    continue;

                var category = Array.IndexOf(CategoryNames, tokens[0].ToLowerInvariant());
                if (category >= 0)
                {
                    currentCategory = category;
                    continue;
                }

                if (currentCategory < 0 || !int.TryParse(tokens[0], out var index))
                    continue;

                var slot = new TitleBookSlotDefinition
                {
                    Category = currentCategory,
                    Index = index,
                    SlotType = -1,
                    QuestId = -1,
                };

                if (tokens.Length >= 2 && int.TryParse(tokens[1], out var slotType))
                    slot.SlotType = slotType;

                if (tokens.Length >= 3 && int.TryParse(tokens[2], out var questId))
                    slot.QuestId = questId;

                if (tokens.Length >= 4 && int.TryParse(tokens[3], out var itemCount))
                {
                    for (var i = 0; i < itemCount && 4 + i < tokens.Length; i++)
                    {
                        if (int.TryParse(tokens[4 + i], out var itemId))
                            slot.AllowedTitleItemIds.Add(itemId);
                    }
                }

                slots[(currentCategory, index)] = slot;
            }

            return slots;
        }

        private static Dictionary<int, TitleQuestDefinition> ParseTitleQuestDefinitions()
        {
            var result = new Dictionary<int, TitleQuestDefinition>();
            var root = ResolveTitleQuestRoot();
            if (string.IsNullOrWhiteSpace(root))
                return result;

            var listPath = Path.Combine(root, "quest.lst");
            if (!File.Exists(listPath))
                return result;

            var lines = File.ReadAllLines(listPath);
            for (var i = 0; i < lines.Length; i++)
            {
                var questLine = NormalizeTokenLine(lines[i]);
                if (!int.TryParse(questLine, out var questId))
                    continue;

                var relativePath = FindNextTokenLine(lines, ref i);
                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;

                var qstPath = ResolveQstPath(root, relativePath);
                if (qstPath == null)
                    continue;

                var definition = ParseQst(questId, qstPath);
                if (definition != null)
                    result[questId] = definition;
            }

            return result;
        }

        private static TitleQuestDefinition ParseQst(int questId, string path)
        {
            var lines = File.ReadAllLines(path);
            var definition = new TitleQuestDefinition { QuestId = questId };
            for (var i = 0; i < lines.Length; i++)
            {
                var line = NormalizeTokenLine(lines[i]);
                if (line.Equals("[check count]", StringComparison.OrdinalIgnoreCase))
                {
                    var value = FindNextInt(lines, i + 1);
                    if (value > 0)
                        definition.CheckCount = (ushort)Math.Min(value, ushort.MaxValue);
                    continue;
                }

                if (line.Equals("[reward int data]", StringComparison.OrdinalIgnoreCase))
                {
                    var value = FindNextInt(lines, i + 1);
                    if (value > 0)
                        definition.RewardTitleItemId = value;
                }
            }

            return definition;
        }

        private static int FindNextInt(string[] lines, int start)
        {
            for (var i = start; i < lines.Length; i++)
            {
                var line = NormalizeTokenLine(lines[i]);
                if (line.StartsWith("[/", StringComparison.Ordinal))
                    return -1;

                var match = Regex.Match(line, @"-?\d+");
                if (match.Success && int.TryParse(match.Value, out var value))
                    return value;
            }
            return -1;
        }

        private static string FindNextTokenLine(string[] lines, ref int index)
        {
            for (var i = index + 1; i < lines.Length; i++)
            {
                var line = NormalizeTokenLine(lines[i]);
                if (line.Length == 0)
                    continue;

                index = i;
                return line;
            }
            return null;
        }

        private static string ResolveQstPath(string root, string relativePath)
        {
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var candidate = Path.Combine(root, normalized);
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(root, normalized.ToLowerInvariant());
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(root, "title", Path.GetFileName(normalized));
            return File.Exists(candidate) ? candidate : null;
        }

        private static Dictionary<(int Category, int Index), TitleBookSlotDefinition> BuildClosedDefaults()
        {
            var slots = new Dictionary<(int Category, int Index), TitleBookSlotDefinition>();
            for (var category = 0; category < DefaultCapacities.Length; category++)
            {
                for (var index = 0; index < DefaultCapacities[category]; index++)
                {
                    slots[(category, index)] = new TitleBookSlotDefinition
                    {
                        Category = category,
                        Index = index,
                        SlotType = -1,
                        QuestId = -1,
                    };
                }
            }
            return slots;
        }

        private static Dictionary<(int Category, int Index), TitleBookSlotDefinition> BuildOpenFallback()
        {
            var slots = BuildClosedDefaults();
            foreach (var key in slots.Keys.ToList())
            {
                var slot = slots[key];
                slot.SlotType = 1;
                slot.QuestId = -1;
                slot.AllowedTitleItemIds.Add(-1);
            }
            return slots;
        }

        private static string ResolveTitleBookEtcPath()
        {
            var configured = Environment.GetEnvironmentVariable("TITLEBOOK_ETC_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "doc", "titlebook", "etc", "titlebook.etc");
                if (File.Exists(candidate))
                    return candidate;

                candidate = Path.Combine(dir.FullName, "Data", "titlebook", "titlebook.etc");
                if (File.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }

        private static string ResolveTitleQuestRoot()
        {
            var configured = Environment.GetEnvironmentVariable("TITLEBOOK_QUEST_ROOT");
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
                return configured;

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "doc", "titlebook", "n_quest");
                if (Directory.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }

        private static string NormalizeTokenLine(string line)
        {
            return StripComment(line).Replace("`", "").Trim();
        }

        private static string StripComment(string line)
        {
            var index = line.IndexOf('#');
            return index >= 0 ? line.Substring(0, index) : line;
        }
    }
}
