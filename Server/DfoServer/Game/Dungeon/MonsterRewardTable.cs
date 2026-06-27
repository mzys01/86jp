using DfoServer.GameWorld;
using System;
using System.Globalization;

namespace DfoServer.Game.Dungeon
{
    public static class MonsterRewardTable
    {
        private static readonly int[] BaseExp = new int[]
        {
            30,   40,   50,   60,   70,   80,   90,  100,  110,  120,
           130,  140,  150,  160,  170,  185,  201,  218,  235,  253,
           271,  290,  310,  330,  351,  372,  394,  417,  440,  464,
           488,  513,  539,  565,  592,  619,  647,  676,  705,  735,
           765,  796,  828,  860,  893,  926,  960,  995, 1030, 1066,
          1102, 1139, 1177, 1215, 1254, 1293, 1333, 1374, 1415, 1457,
          1499, 1542, 1586, 1630, 1675, 1720, 1766, 1813, 1860, 1908,
          1956, 2005, 2055, 2105, 2156, 2207, 2259, 2312, 2365, 2419,
          2473, 2528, 2584, 2640, 2697, 2754, 2812, 2871, 2930, 2990,
          3050, 3111, 3173, 3235, 3298, 3361, 3425, 3490, 3555, 3575,
        };

        private static readonly object _lock = new object();
        private static float[] _difficultyExpRate;

        public static int GetBaseExp(int monsterLevel)
        {
            if (monsterLevel < 1 || monsterLevel > BaseExp.Length) return 0;
            return BaseExp[monsterLevel - 1];
        }

        public static float GetDifficultyExpRate(int difficulty)
        {
            EnsureDifficultyRatesLoaded();
            if (difficulty < 0 || difficulty >= _difficultyExpRate.Length) return 1.0f;
            return _difficultyExpRate[difficulty];
        }

        public static int CalcExp(int monsterLevel, float expWeight, int difficulty = 0)
        {
            var baseExp = GetBaseExp(monsterLevel);
            var diffRate = GetDifficultyExpRate(difficulty);
            return (int)(baseExp * expWeight * diffRate);
        }

        private static void EnsureDifficultyRatesLoaded()
        {
            if (_difficultyExpRate != null) return;
            lock (_lock)
            {
                if (_difficultyExpRate != null) return;
                _difficultyExpRate = ParseDifficultyExpRates();
            }
        }

        private static float[] ParseDifficultyExpRates()
        {
            try
            {
                var text = PvfArchiveAccessor.ReadText("Etc/ServerParameter.etc");
                if (string.IsNullOrEmpty(text)) return new float[] { 1f, 1f, 1f, 1f, 1f };

                var tag = "[dungeon difficulty exp bonusrate]";
                var idx = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return new float[] { 1f, 1f, 1f, 1f, 1f };
                idx += tag.Length;

                var end = text.IndexOf('[', idx);
                if (end < 0) end = text.Length;
                var content = text.Substring(idx, end - idx).Trim();
                var tokens = content.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var rates = new float[tokens.Length];
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        rates[i] = v;
                    else
                        rates[i] = 1f;
                }
                FileLogger.Log($"[MonsterRewardTable] Loaded difficulty exp rates: {string.Join(", ", rates)}");
                return rates;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[MonsterRewardTable] ERROR loading difficulty exp rates: {ex.Message}");
                return new float[] { 1f, 1f, 1f, 1f, 1f };
            }
        }
    }
}
