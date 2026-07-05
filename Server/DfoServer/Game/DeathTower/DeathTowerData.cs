using System;
using System.Collections.Generic;
using DfoServer.GameWorld;
using PvfLib;
using DungeonWorld = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Game.DeathTower
{
    // PVF 数据加载: .dgn 文件的 [dungeon type]="tower of death" 判定 + 层地图列表。
    // 运行时缓存(塔配置不变), 首次访问时从 PVF 加载。
    public static class DeathTowerData
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<int, TowerConfig> _cache = new Dictionary<int, TowerConfig>();

        public sealed class TowerConfig
        {
            public int DungeonId;
            public int TotalStages;
            public int[] StageMapIds;       // index=stageNumber(0-based), value=mapId
            public int BasisLevel;
        }

        // PVF [dungeon type] == "tower of death" 判定。
        public static bool IsDeathTower(int dungeonId)
        {
            return GetConfig(dungeonId) != null;
        }

        public static TowerConfig GetConfig(int dungeonId)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(dungeonId, out var cached))
                    return cached;

                var config = TryLoadFromPvf(dungeonId);
                _cache[dungeonId] = config;
                return config;
            }
        }

        private static TowerConfig TryLoadFromPvf(int dungeonId)
        {
            try
            {
                var lstFile = DungeonWorld.LoadDungeonLstFile();
                var entry = lstFile.GetById(dungeonId);
                if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                    return null;

                var content = PvfArchiveAccessor.ReadText(System.IO.Path.Combine("dungeon", entry.FilePath));
                if (content == null)
                    return null;

                if (!IsTowerOfDeathType(content))
                    return null;

                var mapIds = ParseDeathTowerMapIndexes(content);
                if (mapIds == null || mapIds.Length == 0)
                    return null;

                var basisLevel = ParseBasisLevel(content);

                var config = new TowerConfig
                {
                    DungeonId = dungeonId,
                    TotalStages = mapIds.Length,
                    StageMapIds = mapIds,
                    BasisLevel = basisLevel,
                };
                FileLogger.Log($"[DeathTower] PVF loaded: dungeon={dungeonId} stages={mapIds.Length} basisLv={basisLevel} firstMap={mapIds[0]} lastMap={mapIds[mapIds.Length - 1]}");
                return config;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DeathTower] PVF load failed for dungeon {dungeonId}: {ex.Message}");
                return null;
            }
        }

        private static bool IsTowerOfDeathType(string content)
        {
            var idx = content.IndexOf("[dungeon type]", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            var afterTag = content.Substring(idx + 14, Math.Min(60, content.Length - idx - 14));
            return afterTag.IndexOf("tower of death", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int[] ParseDeathTowerMapIndexes(string content)
        {
            var tag = "[death tower map indexes]";
            var idx = content.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var endTag = "[/death tower map indexes]";
            var endIdx = content.IndexOf(endTag, idx, StringComparison.OrdinalIgnoreCase);
            var section = endIdx > idx
                ? content.Substring(idx + tag.Length, endIdx - idx - tag.Length)
                : content.Substring(idx + tag.Length, Math.Min(2000, content.Length - idx - tag.Length));

            var tokens = section.Split(new[] { ' ', '\t', '\r', '\n', '`' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 1) return null;

            if (!int.TryParse(tokens[0], out var totalStages) || totalStages <= 0)
                return null;

            var mapIds = new int[totalStages];
            // Format: totalStages [stageNum mapId] × totalStages
            for (int i = 0; i < totalStages && (1 + i * 2 + 1) < tokens.Length; i++)
            {
                int.TryParse(tokens[1 + i * 2 + 1], out mapIds[i]);
            }
            return mapIds;
        }

        private static int ParseBasisLevel(string content)
        {
            var tag = "[basis level]";
            var idx = content.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 1;
            var after = content.Substring(idx + tag.Length, Math.Min(20, content.Length - idx - tag.Length));
            var tokens = after.Split(new[] { ' ', '\t', '\r', '\n', '`' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0 && int.TryParse(tokens[0], out var level) && level > 0)
                return level;
            return 1;
        }
    }
}
