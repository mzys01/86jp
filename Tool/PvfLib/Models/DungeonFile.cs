using System;
using System.Collections.Generic;
using System.Globalization;

namespace PvfLib
{
    /// <summary>
    /// </summary>
    public class RidableObject
    {
        public int MapX { get; set; }
        public int MapY { get; set; }
        public int ObjectIndex { get; set; }
        public int PosX { get; set; }
        public int PosY { get; set; }
        public int Faction { get; set; }
    }

    public class RidableObjectScript
    {
        public int SelectCount { get; set; }
        public bool Regenerate { get; set; }
        public List<RidableObject> Objects { get; set; } = new List<RidableObject>();
    }

    public class MazeInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public string Greed { get; set; }
        public string MapSpecification { get; set; }
        public List<MapSpecificationItem> MapSpecifications { get; set; } = new List<MapSpecificationItem>();
        public int[] StartMap { get; set; }                 // [x, y, ?, ?]
        public int[] BossMap { get; set; }
        public int[] HitCount { get; set; }
        public int SealDoorAppearRate { get; set; } = -1;
        public int[] QuestConnection { get; set; }          // [flag, questId, value]
        public RidableObjectScript RidableScript { get; set; }

        public List<ScriptNode> Nodes { get; set; } = new List<ScriptNode>();
    }

    /// <summary>
    /// </summary>
    public class DungeonFile : PvfModelBase
    {
        #region 常用字段

        public string Name { get; set; }
        public string Explain { get; set; }
        public string CutsceneImage { get; set; }
        public int CutsceneImageParam { get; set; } = -1;
        public string MinimapImage { get; set; }
        public string EnteringTitle { get; set; }
        public int MinimumRequiredLevel { get; set; } = -1;
        public int BasisLevel { get; set; } = -1;
        public float ExperienceIncreasingPoint { get; set; } = -1;
        public int BackgroundPos { get; set; } = -1;
        public string DungeonType { get; set; }
        public int[] Champion { get; set; }
        public int[] PathgateObject { get; set; }
        public string WorldmapPatternInfo { get; set; }
        public string WorldmapInfo { get; set; }
        public int Difficulty { get; set; } = -1;
        public bool NoFatigue { get; set; }
        public int[] NamedMonster { get; set; }
        public int[] RecommendedLevel { get; set; }         // [min, max]
        public int LimitPartyCount { get; set; } = -1;

        #endregion

        public List<SpecialPassiveObjectItem> SpecialPassiveObjectItems { get; set; } = new List<SpecialPassiveObjectItem>();

        public List<MazeInfo> Mazes { get; set; } = new List<MazeInfo>();
        #region 解析

        private static readonly HashSet<string> MazeTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "size", "greed", "map specification", "start map", "boss map",
            "hit count", "seal door appear rate", "quest connection",
            "randomized object creation"
        };

        public static DungeonFile Parse(string content)
        {
            if (string.IsNullOrEmpty(content)) return new DungeonFile { Content = content ?? "" };

            var root = new ScriptParser().Parse(content);
            var dgn = new DungeonFile { Root = root, Content = content };

            var metaNodes = new List<ScriptNode>();
            List<ScriptNode> currentMaze = null;

            foreach (var child in root.Children)
            {
                if (child.Tag.Equals("maze info", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentMaze != null && currentMaze.Count > 0)
                        dgn.Mazes.Add(BuildMazeInfo(currentMaze, content));
                    currentMaze = new List<ScriptNode>();
                }
                else if (currentMaze != null && MazeTags.Contains(child.Tag))
                {
                    currentMaze.Add(child);
                }
                else
                {
                    metaNodes.Add(child);
                }
            }

            if (currentMaze != null && currentMaze.Count > 0)
                dgn.Mazes.Add(BuildMazeInfo(currentMaze, content));

            ExtractMetadata(dgn, metaNodes, content);
            return dgn;
        }

        private static void ExtractMetadata(DungeonFile dgn, List<ScriptNode> nodes, string text)
        {
            foreach (var node in nodes)
            {
                string data = node.DataItems.Count > 0 ? node.GetFirstDataContent(text).Trim() : "";
                switch (node.Tag.ToLowerInvariant())
                {
                    case "name":
                        dgn.Name = StripBacktick(data);
                        break;
                    case "explain":
                        dgn.Explain = StripBacktick(data);
                        break;
                    case "cutscene image":
                        ParseCutsceneImage(data, dgn);
                        break;
                    case "minimap image":
                        dgn.MinimapImage = StripBacktick(data);
                        break;
                    case "entering title":
                        dgn.EnteringTitle = StripBacktick(data);
                        break;
                    case "minimum required level":
                        dgn.MinimumRequiredLevel = ParseInt(data);
                        break;
                    case "basis level":
                        dgn.BasisLevel = ParseInt(data);
                        break;
                    case "experience increasing point":
                        float f;
                        if (float.TryParse(data, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                            dgn.ExperienceIncreasingPoint = f;
                        break;
                    case "background pos":
                        dgn.BackgroundPos = ParseInt(data);
                        break;
                    case "dungeon type":
                        dgn.DungeonType = StripBacktick(data);
                        break;
                    case "champion":
                        dgn.Champion = ParseIntArray(data);
                        break;
                    case "pathgate object":
                        dgn.PathgateObject = ParseIntArray(data);
                        break;
                    case "worldmap pattern info":
                        dgn.WorldmapPatternInfo = data;
                        break;
                    case "worldmap info":
                        dgn.WorldmapInfo = data;
                        break;
                    case "difficulty":
                        dgn.Difficulty = ParseInt(data);
                        break;
                    case "no fatigue":
                        dgn.NoFatigue = true;
                        break;
                    case "named monster":
                        dgn.NamedMonster = ParseIntArray(data);
                        break;
                    case "recommended level":
                        dgn.RecommendedLevel = ParseIntArray(data);
                        break;
                    case "limit party count":
                        dgn.LimitPartyCount = ParseInt(data);
                        break;
                    case "special passive object item":
                        try { ParseSpecialPassiveObjectItem(data, dgn); }
                        catch { }
                        break;
                }
            }
        }

        private static void ParseSpecialPassiveObjectItem(string data, DungeonFile dgn)
        {
            // Multi-group format: levelOverride flag count [itemId dropRate]... repeated
            // Each group = one stDungeonAssignItem_t entry
            var vals = ParseIntArray(data);
            int pos = 0;
            int idx = 0;
            while (pos + 2 < vals.Length)
            {
                int levelOverride = vals[pos];
                int flag = vals[pos + 1];
                int count = vals[pos + 2];
                pos += 3;
                for (int i = 0; i < count && pos + 1 < vals.Length; i++)
                {
                    dgn.SpecialPassiveObjectItems.Add(new SpecialPassiveObjectItem
                    {
                        Index = idx,
                        LevelOverride = levelOverride,
                        ItemId = vals[pos],
                        DropRate = vals[pos + 1],
                    });
                    pos += 2;
                }
                idx++;
            }
        }

        private static MazeInfo BuildMazeInfo(List<ScriptNode> nodes, string text)
        {
            var maze = new MazeInfo { Nodes = nodes };
            foreach (var node in nodes)
            {
                string data = node.DataItems.Count > 0 ? node.GetFirstDataContent(text).Trim() : "";
                switch (node.Tag.ToLowerInvariant())
                {
                    case "size":
                        var sz = ParseIntArray(data);
                        if (sz.Length >= 2) { maze.Width = sz[0]; maze.Height = sz[1]; }
                        break;
                    case "greed":
                        maze.Greed = StripBacktick(data);
                        break;
                    case "map specification":
                        maze.MapSpecification = data;
                        maze.MapSpecifications = ParseMapSpecifications(data);
                        break;
                    case "start map":
                        maze.StartMap = ParseIntArray(data);
                        break;
                    case "boss map":
                        maze.BossMap = ParseIntArray(data);
                        break;
                    case "hit count":
                        maze.HitCount = ParseIntArray(data);
                        break;
                    case "seal door appear rate":
                        maze.SealDoorAppearRate = ParseInt(data);
                        break;
                    case "quest connection":
                        maze.QuestConnection = ParseIntArray(data);
                        break;
                    case "randomized object creation":
                        maze.RidableScript = ParseRidableObjectScript(node, text);
                        break;
                }
            }
            return maze;
        }

        private static RidableObjectScript ParseRidableObjectScript(ScriptNode node, string text)
        {
            var script = new RidableObjectScript();
            foreach (var child in node.Children)
            {
                var tag = child.Tag.ToLowerInvariant();
                var childData = child.DataItems.Count > 0 ? (child.GetFirstDataContent(text) ?? "").Trim() : "";
                switch (tag)
                {
                    case "select":
                        script.SelectCount = ParseInt(childData);
                        break;
                    case "regenerate":
                        script.Regenerate = ParseInt(childData) != 0;
                        break;
                    case "object":
                        var obj = new RidableObject();
                        foreach (var sub in child.Children)
                        {
                            var subTag = sub.Tag.ToLowerInvariant();
                            var subData = sub.DataItems.Count > 0 ? (sub.GetFirstDataContent(text) ?? "").Trim() : "";
                            switch (subTag)
                            {
                                case "map":
                                    var mapVals = ParseIntArray(subData);
                                    if (mapVals != null && mapVals.Length >= 2) { obj.MapX = mapVals[0]; obj.MapY = mapVals[1]; }
                                    break;
                                case "index":
                                    obj.ObjectIndex = ParseInt(subData);
                                    break;
                                case "pos":
                                    var posVals = ParseIntArray(subData);
                                    if (posVals != null && posVals.Length >= 2) { obj.PosX = posVals[0]; obj.PosY = posVals[1]; }
                                    break;
                                case "monster":
                                    obj.Faction = 100;
                                    break;
                                case "neutral":
                                    obj.Faction = 200;
                                    break;
                                case "character":
                                    obj.Faction = 0;
                                    break;
                            }
                        }
                        if (obj.PosX == 0 && obj.PosY == 0 && child.DataItems.Count > 0)
                        {
                            var objData = (child.GetFirstDataContent(text) ?? "").Trim();
                            var fallbackPos = ParseIntArray(objData);
                            if (fallbackPos != null && fallbackPos.Length >= 2)
                            {
                                obj.PosX = fallbackPos[0];
                                obj.PosY = fallbackPos[1];
                            }
                        }
                        if (obj.ObjectIndex > 0)
                            script.Objects.Add(obj);
                        break;
                }
            }
            return script;
        }

        private static List<MapSpecificationItem> ParseMapSpecifications(string data)
        {
            var result = new List<MapSpecificationItem>();
            var values = ParseStringArray(data);
            var index = 0;

            while (index < values.Length - 3)
            {
                var type = StripBacktick(values[index]);
                if (type == "map" || type == "boss")
                {
                    if (int.TryParse(values[index + 1], out var x) &&
                        int.TryParse(values[index + 2], out var y) &&
                        int.TryParse(values[index + 3], out var mapIndex))
                    {
                        var item = new MapSpecificationItem
                        {
                            Type = type,
                            X = x,
                            Y = y,
                            Index = mapIndex,
                        };

                        var candidates = new System.Collections.Generic.List<int> { mapIndex };
                        index += 4;
                        while (index < values.Length && int.TryParse(values[index], out var extra))
                        {
                            candidates.Add(extra);
                            index++;
                        }
                        if (candidates.Count > 1)
                            item.MapCandidates = candidates.ToArray();

                        result.Add(item);
                        continue;
                    }
                }
                else if (type == "layered")
                {
                    if (int.TryParse(values[index + 1], out var lx) &&
                        int.TryParse(values[index + 2], out var ly))
                    {
                        index += 3;
                        var ids = new List<int>();
                        while (index < values.Length && int.TryParse(values[index], out var id))
                        {
                            ids.Add(id);
                            index++;
                        }
                        if (ids.Count > 0)
                        {
                            result.Add(new MapSpecificationItem
                            {
                                Type = "layered",
                                X = lx,
                                Y = ly,
                                Index = ids[0],
                                LayeredMapIds = ids.ToArray(),
                            });
                        }
                        continue;
                    }
                }

                index++;
            }

            return result;
        }

        private static string[] ParseStringArray(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return Array.Empty<string>();

            return data.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        #endregion

        #region 辅助

        private static void ParseCutsceneImage(string data, DungeonFile dgn)
        {
            if (string.IsNullOrEmpty(data)) return;
            var parts = data.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
                dgn.CutsceneImage = StripBacktick(parts[0]);
            if (parts.Length >= 2)
            {
                int v;
                if (int.TryParse(parts[1], out v))
                    dgn.CutsceneImageParam = v;
            }
        }

        #endregion
    }

    public class MapSpecificationItem
    {
        public string Type { get; set; }

        public int X { get; set; }

        public int Y { get; set; }

        public int Index { get; set; }

        public int[] LayeredMapIds { get; set; }

        public int[] MapCandidates { get; set; }
    }

    public class SpecialPassiveObjectItem
    {
        public int Index { get; set; }
        public int LevelOverride { get; set; }
        public int ItemId { get; set; }
        public int DropRate { get; set; }
    }
}
