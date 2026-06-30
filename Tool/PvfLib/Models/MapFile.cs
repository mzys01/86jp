using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PvfLib
{
    public class MapBackgroundAnimation
    {
        public string Filename { get; set; }
        public string Layer { get; set; }
        public string Order { get; set; }
    }

    public enum MonsterType
    {
        Normal,
        Champion,
        SuperChampion,
        Boss,
        MaxValue
    }

    public class MonsterInfo
    {
        public int? MonsterId { get; set; }
        public int? AutoLv { get; set; }
        public int? Lv { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public int? Z { get; set; }
        public int? RandomDropCnt { get; set; }
        public int? SpecifyDropCnt { get; set; }
        public string Fixed { get; set; }
        public MonsterType Type { get; set; }
    }

    public class PassiveObjectInfo
    {
        public int ObjectCode { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Flags { get; set; }
    }

    public class HellPartyMapEntry
    {
        public int GroupId { get; set; }
        public int Rate { get; set; }
        public int Order { get; set; }
    }

    public class SpecialPassiveObjectInfo
    {
        public int ObjectCode { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Flags { get; set; }
        public List<HellPartyMapEntry> HellPartyEntries { get; set; } = new List<HellPartyMapEntry>();
    }

    public enum ApcFaction
    {
        Character = 0,
        Monster = 100,
        Neutral = 200,
    }

    public enum ApcAIType
    {
        Normal = 5,
        Champion = 6,
        Boss = 8,
    }

    public class AICharacterInfo
    {
        public int Code { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Direction { get; set; }
        public ApcFaction Faction { get; set; }
        public ApcAIType AIType { get; set; }
    }

    /// <summary>
    /// </summary>
    public class MapFile : PvfModelBase
    {
        public string MapName { get; set; }
        public int[] PlayerNumber { get; set; }
        public int[] PvpStartArea { get; set; }
        public int DungeonId { get; set; } = -1;
        public string Type { get; set; }
        public string Greed { get; set; }
        public List<string> Tiles { get; set; } = new List<string>();
        public int FarSightScroll { get; set; } = -1;
        public int MiddleSightScroll { get; set; } = -1;
        public int NearSightScroll { get; set; } = -1;
        public List<MapBackgroundAnimation> BackgroundAnimations { get; set; } = new List<MapBackgroundAnimation>();
        public int[] PathgatePos { get; set; }
        public List<string> Sounds { get; set; } = new List<string>();
        public int AnimationObjectCount { get; set; } = -1;
        public int PassiveObjectCount { get; set; } = -1;
        public List<PassiveObjectInfo> PassiveObjects { get; set; } = new List<PassiveObjectInfo>();
        public int SpecialPassiveObjectCount { get; set; } = -1;
        public List<SpecialPassiveObjectInfo> SpecialPassiveObjects { get; set; } = new List<SpecialPassiveObjectInfo>();
        public int MonsterCount { get; set; } = -1;
        public List<MonsterInfo> Monsters { get; set; } = new List<MonsterInfo>();
        public int EventMonsterPositionCount { get; set; } = -1;
        public int NpcCount { get; set; } = -1;
        public string MonsterSpecificAI { get; set; }
        public string Buff { get; set; }
        public List<AICharacterInfo> AICharacters { get; set; } = new List<AICharacterInfo>();

        private static readonly Regex BacktickStringRx = new Regex("`([^`]+)`", RegexOptions.Compiled);
        private static readonly Regex AniReferenceRx = new Regex("`[^`]+\\.ani`", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex InlineHellPartyRx = new Regex(
            @"`?\[hellparty\]`?(?<body>.*?)`?\[/hellparty\]`?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        public static MapFile Parse(string content)
        {
            if (string.IsNullOrEmpty(content))
                return new MapFile { Content = content ?? string.Empty, Root = new ScriptNode { Tag = "ROOT" } };

            var root = new ScriptParser().Parse(content);
            var map = new MapFile { Root = root, Content = content };

            foreach (var node in root.Children)
            {
                string data = node.DataItems.Count > 0 ? node.GetFirstDataContent(content).Trim() : string.Empty;
                switch (node.Tag.ToLowerInvariant())
                {
                    case "map name":
                        map.MapName = StripBacktick(data);
                        break;
                    case "player number":
                        map.PlayerNumber = ParseIntArray(data);
                        break;
                    case "pvp start area":
                        map.PvpStartArea = ParseIntArray(data);
                        break;
                    case "dungeon":
                        map.DungeonId = ParseInt(data);
                        break;
                    case "type":
                        map.Type = StripBacktick(data);
                        break;
                    case "greed":
                        map.Greed = StripBacktick(data);
                        break;
                    case "tile":
                        map.Tiles.AddRange(ParseBacktickStrings(data));
                        break;
                    case "far sight scroll":
                        map.FarSightScroll = ParseInt(data);
                        break;
                    case "middle sight scroll":
                        map.MiddleSightScroll = ParseInt(data);
                        break;
                    case "near sight scroll":
                        map.NearSightScroll = ParseInt(data);
                        break;
                    case "background animation":
                        map.BackgroundAnimations.AddRange(ParseBackgroundAnimations(node, content));
                        break;
                    case "pathgate pos":
                        map.PathgatePos = ParseIntArray(data);
                        break;
                    case "sound":
                        map.Sounds.AddRange(ParseBacktickStrings(data));
                        break;
                    case "animation":
                        map.AnimationObjectCount = CountAnimationReferences(data);
                        break;
                    case "passive object":
                        map.PassiveObjectCount = CountNumberGroups(data, 4);
                        map.PassiveObjects = ParsePassiveObjects(data);
                        break;
                    case "special passive object":
                        map.SpecialPassiveObjectCount = CountNumberGroups(data, 4);
                        map.SpecialPassiveObjects = ParseSpecialPassiveObjects(data);
                        break;
                    case "monster":
                        map.MonsterCount = CountNumberGroups(data, 4);
                        map.Monsters = ParseMonsters(data);
                        break;
                    case "event monster position":
                        map.EventMonsterPositionCount = CountNumberGroups(data, 3);
                        break;
                    case "npc":
                        map.NpcCount = CountNumberGroups(data, 4);
                        break;
                    case "monster specific ai":
                        map.MonsterSpecificAI = data;
                        break;
                    case "buff":
                        map.Buff = data;
                        break;
                    case "ai character":
                        map.AICharacters = ParseAICharacters(data);
                        break;
                }
            }

            return map;
        }

        private static List<MapBackgroundAnimation> ParseBackgroundAnimations(ScriptNode node, string content)
        {
            var result = new List<MapBackgroundAnimation>();
            foreach (var child in node.GetChildren("ani info"))
            {
                var info = new MapBackgroundAnimation();
                var filename = child.GetChild("filename");
                var layer = child.GetChild("layer");
                var order = child.GetChild("order");
                if (filename != null) info.Filename = StripBacktick(filename.GetFirstDataContent(content));
                if (layer != null) info.Layer = StripBacktick(layer.GetFirstDataContent(content));
                if (order != null) info.Order = StripBacktick(order.GetFirstDataContent(content));
                result.Add(info);
            }
            return result;
        }

        private static List<string> ParseBacktickStrings(string data)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(data)) return result;

            var matches = BacktickStringRx.Matches(data);
            foreach (Match match in matches)
                result.Add(match.Groups[1].Value);
            return result;
        }

        private static int CountAnimationReferences(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return -1;
            return AniReferenceRx.Matches(data).Count;
        }

        private static int CountNumberGroups(string data, int groupSize)
        {
            if (string.IsNullOrWhiteSpace(data) || groupSize <= 0) return -1;
            var numbers = ParseIntArray(data);
            if (numbers.Length == 0) return -1;
            return numbers.Length / groupSize;
        }

        private static List<AICharacterInfo> ParseAICharacters(string data)
        {
            var result = new List<AICharacterInfo>();
            var values = Regex.Split(data.Trim(), @"\s+");
            int i = 0;
            while (i < values.Length)
            {
                int code;
                if (!int.TryParse(values[i], out code)) break;
                var entry = new AICharacterInfo { Code = code };
                if (i + 1 < values.Length) { int v; if (int.TryParse(values[i + 1], out v)) entry.X = v; }
                if (i + 2 < values.Length) { int v; if (int.TryParse(values[i + 2], out v)) entry.Y = v; }
                if (i + 3 < values.Length) { int v; if (int.TryParse(values[i + 3], out v)) entry.Direction = v; }
                i += 4;
                if (i < values.Length)
                {
                    var f = StripBacktick(values[i]).ToLowerInvariant();
                    if (f == "[character]") entry.Faction = ApcFaction.Character;
                    else if (f == "[monster]") entry.Faction = ApcFaction.Monster;
                    else if (f == "[neutral]") entry.Faction = ApcFaction.Neutral;
                    i++;
                }
                if (i < values.Length)
                {
                    var a = StripBacktick(values[i]).ToLowerInvariant();
                    if (a == "[normal]") entry.AIType = ApcAIType.Normal;
                    else if (a == "[champion]") entry.AIType = ApcAIType.Champion;
                    else if (a == "[boss]") entry.AIType = ApcAIType.Boss;
                    i++;
                }
                // 末尾两个数值字段当前未使用。
                for (int skip = 0; skip < 2 && i < values.Length; skip++)
                {
                    int dummy;
                    if (int.TryParse(values[i], out dummy)) i++;
                    else break;
                }
                result.Add(entry);
            }
            return result;
        }

        private static List<MonsterInfo> ParseMonsters(string data)
        {
            var result = new List<MonsterInfo>();
            if (string.IsNullOrWhiteSpace(data))
                return result;

            var values = data.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index + 9 < values.Length; index += 10)
            {
                result.Add(new MonsterInfo
                {
                    MonsterId = ParseNullableInt(values[index]),
                    Lv = ParseNullableInt(values[index + 1]),
                    AutoLv = ParseNullableInt(values[index + 2]),
                    X = ParseNullableInt(values[index + 3]),
                    Y = ParseNullableInt(values[index + 4]),
                    Z = ParseNullableInt(values[index + 5]),
                    RandomDropCnt = ParseNullableInt(values[index + 6]),
                    SpecifyDropCnt = ParseNullableInt(values[index + 7]),
                    Fixed = StripBacktick(values[index + 8]),
                    Type = ParseMonsterType(StripBacktick(values[index + 9])),
                });
            }

            return result;
        }

        private static List<PassiveObjectInfo> ParsePassiveObjects(string data)
        {
            var result = new List<PassiveObjectInfo>();
            if (string.IsNullOrWhiteSpace(data)) return result;
            var nums = ParseIntArray(data);
            for (int i = 0; i + 3 < nums.Length; i += 4)
            {
                result.Add(new PassiveObjectInfo
                {
                    ObjectCode = nums[i],
                    X = nums[i + 1],
                    Y = nums[i + 2],
                    Flags = nums[i + 3],
                });
            }
            return result;
        }

        private static List<SpecialPassiveObjectInfo> ParseSpecialPassiveObjects(string data)
        {
            var result = new List<SpecialPassiveObjectInfo>();
            if (string.IsNullOrWhiteSpace(data)) return result;

            var hellMatch = InlineHellPartyRx.Match(data);
            var head = hellMatch.Success ? data.Substring(0, hellMatch.Index) : data;
            var nums = ParseIntArray(head);
            for (int i = 0; i + 3 < nums.Length; i += 4)
            {
                result.Add(new SpecialPassiveObjectInfo
                {
                    ObjectCode = nums[i],
                    X = nums[i + 1],
                    Y = nums[i + 2],
                    Flags = nums[i + 3],
                });
            }

            if (hellMatch.Success && result.Count > 0)
            {
                var entries = ParseHellPartyEntries(hellMatch.Groups["body"].Value);
                if (entries.Count > 0)
                    result[result.Count - 1].HellPartyEntries.AddRange(entries);
            }

            return result;
        }

        private static List<HellPartyMapEntry> ParseHellPartyEntries(string data)
        {
            var result = new List<HellPartyMapEntry>();
            var nums = ParseIntArray(data);
            for (int i = 0; i + 2 < nums.Length; i += 3)
            {
                result.Add(new HellPartyMapEntry
                {
                    GroupId = nums[i],
                    Rate = nums[i + 1],
                    Order = nums[i + 2],
                });
            }

            return result;
        }

        private static int? ParseNullableInt(string value)
        {
            return int.TryParse(value, out var result) ? result : (int?)null;
        }

        private static MonsterType ParseMonsterType(string value)
        {
            switch (value)
            {
                case "[normal]":
                case "normal":
                    return MonsterType.Normal;
                case "[champion]":
                case "champion":
                    return MonsterType.Champion;
                case "[super champion]":
                case "super champion":
                    return MonsterType.SuperChampion;
                case "[boss]":
                case "boss":
                    return MonsterType.Boss;
                default:
                    return MonsterType.MaxValue;
            }
        }
    }
}
