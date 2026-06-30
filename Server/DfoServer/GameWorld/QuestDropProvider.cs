using System;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    public struct QuestDropCandidate
    {
        public int ItemId;
        public int Count;
        public int DropRate;
        public int MaxStack;
    }

    public static class QuestDropProvider
    {
        private static readonly Random _rng = new Random();

        /// <summary>
        /// 检查杀怪是否触发任务物品掉落。
        /// 匹配逻辑 (IDA 实证 Quest::CheckKillMonster @ 0x83535d6):
        ///   monsterCode 精确匹配 (无通配), dungeonId -1=任意, difficulty -1=任意, enemyType 默认1
        /// </summary>
        public static List<QuestDropCandidate> CheckMonsterDrop(
            ICollection<int> activeQuestIds, int dungeonIndex, int difficulty, int monsterCode)
        {
            if (activeQuestIds == null || activeQuestIds.Count == 0)
                return null;

            var results = new List<QuestDropCandidate>();

            foreach (var questId in activeQuestIds)
            {
                try
                {
                    var qst = QuestData.GetQuestFile(questId);
                    if (qst == null || qst.MonsterRewardItems == null || qst.MonsterRewardItems.Count == 0)
                        continue;

                    foreach (var entry in qst.MonsterRewardItems)
                    {
                        if (entry.MonsterCode != monsterCode)
                            continue;
                        if (entry.DungeonId != -1 && entry.DungeonId != dungeonIndex)
                            continue;
                        if (entry.Difficulty >= 0 && entry.Difficulty != difficulty)
                            continue;

                        results.Add(new QuestDropCandidate
                        {
                            ItemId = entry.ItemId,
                            Count = entry.Count,
                            DropRate = entry.DropRate,
                            MaxStack = entry.MaxStack,
                        });
                    }
                }
                catch { }
            }

            return results.Count > 0 ? results : null;
        }

        /// <summary>
        /// 从候选列表 roll 掉落数量。
        /// 逻辑 (IDA 实证 CheckQuestMonster):
        ///   随机选一个候选 → for(k=0; k &lt; count; k++) if(rand(100) &lt; dropRate) actualCount++
        ///   如果 maxStack 已满则换下一个候选
        /// 简化: 单机不做装备/VIP 加成, 只用基础 dropRate
        /// </summary>
        public static int RollDrop(QuestDropCandidate candidate, int currentHeld)
        {
            if (candidate.MaxStack != -1 && currentHeld >= candidate.MaxStack)
                return 0;

            int actual = 0;
            int maxAttempts = Math.Max(1, candidate.Count);
            for (int k = 0; k < maxAttempts; k++)
            {
                if (_rng.Next(100) < candidate.DropRate)
                    actual++;
            }

            if (actual <= 0) return 0;
            if (actual > 999) actual = 999;

            if (candidate.MaxStack != -1 && currentHeld + actual > candidate.MaxStack)
                actual = candidate.MaxStack - currentHeld;

            return Math.Max(0, actual);
        }
    }
}
