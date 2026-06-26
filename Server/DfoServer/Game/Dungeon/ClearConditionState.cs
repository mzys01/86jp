using System.Collections.Generic;
using PvfLib;

namespace DfoServer.Game.Dungeon
{
    public sealed class ClearConditionState
    {
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

        // df_game_r CClearCondition::ClearCondition (0x82FEFCE)
        public bool Check(int type, int targetId)
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

        public bool IsCleared => TotalRequired > 0 && TotalRequired <= CurrentProgress;

        public bool HasConditions => TotalRequired > 0;
    }
}
