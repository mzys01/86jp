using System;
using System.Collections.Generic;
using System.Globalization;
using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;

namespace DfoServer.Game.DeathTower
{
    public sealed class DeathTowerRewardConfig
    {
        private static readonly object Sync = new object();
        private static DeathTowerRewardConfig _cached;

        private readonly float[] _expWeights;
        private readonly int[] _rewardCardCounts;

        private DeathTowerRewardConfig(
            int normalItemWeight,
            int magicItemWeight,
            int itemWeightTotal,
            float goldWeight,
            float[] expWeights,
            int[] rewardCardCounts)
        {
            NormalItemWeight = normalItemWeight;
            MagicItemWeight = magicItemWeight;
            ItemWeightTotal = itemWeightTotal;
            GoldWeight = goldWeight;
            _expWeights = expWeights ?? Array.Empty<float>();
            _rewardCardCounts = rewardCardCounts ?? Array.Empty<int>();
        }

        public int NormalItemWeight { get; }
        public int MagicItemWeight { get; }
        public int ItemWeightTotal { get; }
        public float GoldWeight { get; }

        public static DeathTowerRewardConfig Load()
        {
            lock (Sync)
            {
                if (_cached != null)
                    return _cached;

                try
                {
                    var text = PvfArchiveAccessor.ReadText("etc/deathtower.etc");
                    _cached = Parse(text);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[DeathTower] reward config load failed: {ex.Message}");
                    _cached = CreateUnavailable();
                }

                return _cached;
            }
        }

        internal static DeathTowerRewardConfig Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return CreateUnavailable();

            var probabilities = ParseInts(ReadSection(text, "reward item prob"));
            var goldWeights = ParseFloats(ReadSection(text, "reward gold weight"));
            var expWeights = ParseFloats(ReadSection(text, "reward exp weight"));
            var rewardCardCounts = ParseInts(ReadSection(text, "reward card num"));

            if (probabilities.Count < 3
                || goldWeights.Count == 0
                || expWeights.Count == 0
                || rewardCardCounts.Count == 0)
            {
                return CreateUnavailable();
            }

            var normalWeight = probabilities[0];
            var magicWeight = probabilities[1];
            var totalWeight = probabilities[2];
            if (normalWeight < 0 || magicWeight < 0 || totalWeight <= 0
                || normalWeight + magicWeight > totalWeight)
            {
                return CreateUnavailable();
            }

            return new DeathTowerRewardConfig(
                normalWeight,
                magicWeight,
                totalWeight,
                goldWeights[0] > 0 ? goldWeights[0] : 0f,
                expWeights.ToArray(),
                rewardCardCounts.ToArray());
        }

        public float GetExpWeight(int clearedFloorCount)
        {
            if (_expWeights.Length == 0)
                return 0;
            var index = Math.Max(0, Math.Min(clearedFloorCount - 1, _expWeights.Length - 1));
            return _expWeights[index];
        }

        public int GetRewardCardCount(int clearedFloorCount)
        {
            if (_rewardCardCounts.Length == 0)
                return 0;
            var index = Math.Max(0, Math.Min(clearedFloorCount - 1, _rewardCardCounts.Length - 1));
            return Math.Max(0, _rewardCardCounts[index]);
        }

        public int RollItemRarity(DnfLcg lcg)
        {
            if (lcg == null || ItemWeightTotal <= 0)
                return 0;
            // PVF stores two explicit weights and a total; the remaining weight is rarity 2.
            var roll = lcg.Next(ItemWeightTotal);
            if (roll < NormalItemWeight)
                return 0;
            if (roll < NormalItemWeight + MagicItemWeight)
                return 1;
            return 2;
        }

        private static DeathTowerRewardConfig CreateUnavailable()
        {
            return new DeathTowerRewardConfig(
                0,
                0,
                0,
                0f,
                Array.Empty<float>(),
                Array.Empty<int>());
        }

        private static string ReadSection(string text, string tagName)
        {
            var tag = "[" + tagName + "]";
            var start = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;
            start += tag.Length;

            var closeTag = "[/" + tagName + "]";
            var close = text.IndexOf(closeTag, start, StringComparison.OrdinalIgnoreCase);
            var nextTag = text.IndexOf('[', start);
            var end = close >= 0 && (nextTag < 0 || close <= nextTag)
                ? close
                : nextTag;
            if (end < 0)
                end = text.Length;
            return text.Substring(start, end - start);
        }

        private static List<int> ParseInts(string section)
        {
            var values = new List<int>();
            foreach (var token in SplitTokens(section))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
            }
            return values;
        }

        private static List<float> ParseFloats(string section)
        {
            var values = new List<float>();
            foreach (var token in SplitTokens(section))
            {
                if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
            }
            return values;
        }

        private static string[] SplitTokens(string section)
        {
            return (section ?? string.Empty).Split(
                new[] { ' ', '\t', '\r', '\n', '`' },
                StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
