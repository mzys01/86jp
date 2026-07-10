using System.Collections.Generic;
using PvfLib;

namespace DfoServer.Game.Dungeon
{
    public sealed class ClearConditionState
    {
        private readonly object _sync = new object();
        private readonly List<ClearConditionEntry> _conditions;
        private readonly int[] _counters;
        public int TotalRequired { get; }
        public int CurrentProgress { get; private set; }

        public ClearConditionState(List<ClearConditionEntry> conditions)
        {
            _conditions = conditions ?? new List<ClearConditionEntry>();
            _counters = new int[_conditions.Count];
            int total = 0;
            foreach (var c in _conditions)
                total += c.Count;
            TotalRequired = total;
        }

        /// <summary>同一份条件、计数器归零的新实例。组队进本 fan-out 时每成员各持一份 ——
        /// 引用共享会让多人并发 Check 互相污染计数(条件列表本身只读, 可共享)。</summary>
        public ClearConditionState CloneFresh()
        {
            return new ClearConditionState(_conditions);
        }

        // df_game_r CClearCondition::ClearCondition (0x82FEFCE)
        // 内置锁: 成员自己的 handler 线程与队友击杀 relay 线程都会调本方法, 计数器自增必须互斥。
        public bool Check(int type, int targetId)
        {
            lock (_sync)
            {
                for (int i = 0; i < _conditions.Count; i++)
                {
                    var c = _conditions[i];
                    if (c.Type == type && c.TargetId == targetId)
                    {
                        if (_counters[i] < c.Count)
                        {
                            _counters[i]++;
                            CurrentProgress++;
                        }
                    }
                }
                return TotalRequired > 0 && TotalRequired <= CurrentProgress;
            }
        }

        public bool IsCleared
        {
            get { lock (_sync) { return TotalRequired > 0 && TotalRequired <= CurrentProgress; } }
        }

        public bool HasConditions => TotalRequired > 0;
    }
}
