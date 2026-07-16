using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Lottery
{
    public sealed class PendingLotteryOpen
    {
        internal PendingLotteryOpen(short slotIndex, DateTime createdAtUtc, LotteryOpenPlan openPlan)
        {
            SlotIndex = slotIndex;
            CreatedAtUtc = createdAtUtc;
            OpenPlan = openPlan;
        }

        public short SlotIndex { get; }

        public DateTime CreatedAtUtc { get; }

        public LotteryOpenPlan OpenPlan { get; }
    }

    public sealed class LotteryOpenSessionCoordinator
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

        private readonly object _sync = new object();
        private readonly Dictionary<Guid, PendingLotteryOpen> _pending
            = new Dictionary<Guid, PendingLotteryOpen>();
        private readonly TimeSpan _timeout;
        private readonly Func<DateTime> _utcNow;

        public LotteryOpenSessionCoordinator(TimeSpan? timeout = null, Func<DateTime> utcNow = null)
        {
            _timeout = timeout ?? DefaultTimeout;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public void Set(Guid sessionId, short slotIndex, LotteryOpenPlan openPlan = null)
        {
            lock (_sync)
            {
                var now = _utcNow();
                CleanupExpired(now);
                _pending[sessionId] = new PendingLotteryOpen(slotIndex, now, openPlan);
            }
        }

        public bool TryTake(Guid sessionId, short? expectedSlotIndex, out PendingLotteryOpen pending)
        {
            lock (_sync)
            {
                CleanupExpired(_utcNow());
                if (!_pending.TryGetValue(sessionId, out pending))
                    return false;

                if (expectedSlotIndex.HasValue && pending.SlotIndex != expectedSlotIndex.Value)
                    return false;

                _pending.Remove(sessionId);
                return true;
            }
        }

        public void Remove(Guid sessionId)
        {
            lock (_sync)
                _pending.Remove(sessionId);
        }

        private void CleanupExpired(DateTime nowUtc)
        {
            var expired = _pending
                .Where(pair => nowUtc - pair.Value.CreatedAtUtc > _timeout)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var sessionId in expired)
                _pending.Remove(sessionId);
        }
    }
}
