using DfoServer.Game.Skills;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Mercenary
{
    // 支援兵技能配置的一条记录。
    public sealed class StrikerSkillEntry
    {
        public int Job { get; set; }
        public int GrowType { get; set; }
        public int SkillIndex { get; set; }
        public int ComboIndex { get; set; }
        public int RequiredSkillIndex { get; set; }
        public string VideoPath { get; set; }
        public int[] ComponentSkillIndexes { get; set; } = Array.Empty<int>();
        public string SkillName { get; set; }
        public int RequiredLevel { get; set; }
    }

    public static class StrikerSkillDataProvider
    {
        private static readonly object Sync = new object();
        private static List<StrikerSkillEntry> _entries;

        public static void Warmup()
        {
            EnsureLoaded();
        }

        public static IReadOnlyList<StrikerSkillEntry> GetAvailableSkills(int job, int growType, int level)
        {
            EnsureLoaded();
            // 部分数据库转职值带有打包信息，低四位才对应支援兵配置。
            var normalizedGrowType = NormalizeGrowType(growType);
            var result = new List<StrikerSkillEntry>();
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Job != job || entry.GrowType != normalizedGrowType)
                    continue;
                if (entry.RequiredLevel > level)
                    continue;

                result.Add(entry);
            }
            return result;
        }

        public static IReadOnlyList<StrikerSkillEntry> GetAll()
        {
            EnsureLoaded();
            return _entries;
        }

        public static StrikerSkillEntry FindBySkill(int job, int growType, int skillIndex, int comboIndex)
        {
            EnsureLoaded();

            var normalizedGrowType = NormalizeGrowType(growType);
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Job != job || entry.GrowType != normalizedGrowType)
                    continue;
                if (entry.SkillIndex == skillIndex && (comboIndex <= 0 || entry.ComboIndex == comboIndex))
                    return entry;
            }

            return null;
        }

        public static int NormalizeGrowType(int growType)
        {
            return growType > 0x0F ? growType & 0x0F : growType;
        }

        private static void EnsureLoaded()
        {
            if (_entries != null)
                return;

            lock (Sync)
            {
                if (_entries != null)
                    return;

                _entries = Parse(PvfArchiveAccessor.ReadText("etc/linksystem/striker.etc"));
            }
        }

        private static List<StrikerSkillEntry> Parse(string text)
        {
            var section = ExtractSection(text, "striker skill");
            var tokens = Tokenize(section);
            var entries = new List<StrikerSkillEntry>();

            int offset = 0;
            while (offset < tokens.Count)
            {
                if (!TryReadInt(tokens, ref offset, out var job)
                    || !TryReadInt(tokens, ref offset, out var growType)
                    || !TryReadInt(tokens, ref offset, out var skillIndex)
                    || !TryReadInt(tokens, ref offset, out var comboIndex)
                    || !TryReadInt(tokens, ref offset, out var requiredSkillIndex)
                    || !TryReadString(tokens, ref offset, out var videoPath)
                    || !TryReadInt(tokens, ref offset, out var componentCount))
                {
                    break;
                }

                if (componentCount < 0 || componentCount > 128)
                    break;

                var components = new int[componentCount];
                var valid = true;
                for (int i = 0; i < componentCount; i++)
                {
                    if (!TryReadInt(tokens, ref offset, out components[i]))
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                    break;

                var data = SkillDataProvider.GetSkill(job, skillIndex);
                entries.Add(new StrikerSkillEntry
                {
                    Job = job,
                    GrowType = growType,
                    SkillIndex = skillIndex,
                    ComboIndex = comboIndex,
                    RequiredSkillIndex = requiredSkillIndex,
                    VideoPath = videoPath,
                    ComponentSkillIndexes = components,
                    SkillName = data?.Name,
                    RequiredLevel = data?.RequiredLevel ?? 0,
                });
            }

            return entries;
        }

        private static string ExtractSection(string text, string tag)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var startTag = "[" + tag + "]";
            var endTag = "[/" + tag + "]";
            var start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;
            start += startTag.Length;

            var end = text.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                end = text.Length;

            return text.Substring(start, end - start);
        }

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            if (string.IsNullOrWhiteSpace(text))
                return tokens;

            foreach (Match m in Regex.Matches(text, @"`([^`]*)`|[-]?\d+"))
            {
                if (m.Value.StartsWith("`", StringComparison.Ordinal))
                    tokens.Add(new Token { Text = m.Groups[1].Value, IsString = true });
                else if (int.TryParse(m.Value, out var value))
                    tokens.Add(new Token { Number = value });
            }

            return tokens;
        }

        private static bool TryReadInt(List<Token> tokens, ref int offset, out int value)
        {
            value = 0;
            if (offset >= tokens.Count || tokens[offset].IsString)
                return false;
            value = tokens[offset++].Number;
            return true;
        }

        private static bool TryReadString(List<Token> tokens, ref int offset, out string value)
        {
            value = null;
            if (offset >= tokens.Count || !tokens[offset].IsString)
                return false;
            value = tokens[offset++].Text;
            return true;
        }

        private struct Token
        {
            public bool IsString;
            public int Number;
            public string Text;
        }
    }
}
