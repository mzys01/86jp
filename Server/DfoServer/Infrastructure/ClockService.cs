using System;
using System.Collections.Generic;
using System.Threading;

namespace DfoServer.Infrastructure
{
    // 时钟服务: 进程内定时触发器, 到了指定时间点执行注册的回调。
    // 只解决"到点主动给在线玩家做某件事"(推送/广播/定期结算); 数据本身的正确性
    // 一律由正常业务路径(登录/使用时读库判定)保证, 不依赖本时钟。
    //
    // 使用规则(接入前必读):
    //   1) 本服务不往数据库写任何东西, 也不记"上次执行到哪"。停机或卡顿期间错过的
    //      时间点不补触发、连续错过多次只触发一次——所以你的功能必须做到:
    //      时钟一次都没响, 玩家在登录/使用时拿到的状态也照样正确;
    //   2) 回调里只允许 读数据(可顺带结算)/检查条件/给在线玩家发包。不允许凭空
    //      创造新数据(如直接发道具)——那类需求应做成"落库记账+登录或使用时兑现",
    //      时钟顶多提醒在线的人;
    //   3) 回调不会与自己并发(上一轮没跑完则跳过本轮检查), 抛异常只记日志、
    //      不影响其他回调; 耗时或异步操作请自行派发;
    //   4) 系统时间被往回调时自动重新对表, 该轮不触发; 被拨回去的时间点随时间
    //      再次到来会再触发一次(在规则2约束下这是无害的)。
    //
    // 时间基准: 北京时间(UTC+8), 与每日重置口径一致(06:00)。
    // 用法(构造期注册, Program 启动服务器后统一 Start):
    //   ClockService.Instance.RegisterMinuteTick("online-progress", utcNow => { ... });
    //   ClockService.Instance.RegisterDailyMoment("fatigue-refresh", 6, 0, utcNow => { ... });
    //   ClockService.Instance.RegisterWeeklyMoment("raid-open", DayOfWeek.Wednesday, 20, 0, utcNow => { ... });
    public sealed class ClockService
    {
        public static readonly ClockService Instance = new ClockService();

        private const int TimeZoneOffsetHours = 8;   // 北京 UTC+8
        private const int TimerResolutionMs = 5000;  // 5秒分辨率: 时刻触发误差 ≤5s

        private sealed class MomentEntry
        {
            public string Name;
            public int Hour;
            public int Minute;
            public DayOfWeek? Day;          // null = 每日
            public DateTime NextDueUtc;     // MinValue = 未定位(首查时定位, 启动前的时刻不补)
            public Action<DateTime> Callback;
        }

        private sealed class OneShotEntry
        {
            public string Name;
            public DateTime DueUtc;
            public Action<DateTime> Callback;
        }

        private readonly object _sync = new object();
        private readonly List<KeyValuePair<string, Action<DateTime>>> _minuteTicks
            = new List<KeyValuePair<string, Action<DateTime>>>();
        private readonly List<MomentEntry> _moments = new List<MomentEntry>();
        private readonly Dictionary<string, OneShotEntry> _oneShots
            = new Dictionary<string, OneShotEntry>(StringComparer.Ordinal);
        private long _lastMinuteIndex = -1;
        private DateTime _lastCheckedUtc = DateTime.MinValue;
        private Timer _timer;
        private int _checking;   // OnTimer 重入门闩: 保证回调不重叠

        // 每分钟节拍(跨整分触发一次)。不保证补齐卡顿跳过的分钟, 消费者必须幂等/拉取式。
        public void RegisterMinuteTick(string name, Action<DateTime> callback)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is empty", nameof(name));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (_sync)
                _minuteTicks.Add(new KeyValuePair<string, Action<DateTime>>(name, callback));
        }

        // 每日北京时间 HH:mm 触发一次。
        public void RegisterDailyMoment(string name, int hour, int minute, Action<DateTime> callback)
            => RegisterMoment(name, null, hour, minute, callback);

        // 每周指定星期的北京时间 HH:mm 触发一次。
        public void RegisterWeeklyMoment(string name, DayOfWeek day, int hour, int minute, Action<DateTime> callback)
            => RegisterMoment(name, day, hour, minute, callback);

        // 进程内一次性定时器。同名任务会覆盖旧任务；会话/登录重建时,
        // 调用方必须从持久化状态重新恢复仍需要的定时器。
        public void ScheduleOneShot(string name, DateTime dueUtc, Action<DateTime> callback)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is empty", nameof(name));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (dueUtc.Kind == DateTimeKind.Local)
                dueUtc = dueUtc.ToUniversalTime();

            lock (_sync)
            {
                _oneShots[name] = new OneShotEntry
                {
                    Name = name,
                    DueUtc = dueUtc,
                    Callback = callback,
                };
            }
        }

        public bool CancelOneShot(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            lock (_sync)
                return _oneShots.Remove(name);
        }

        private void RegisterMoment(string name, DayOfWeek? day, int hour, int minute, Action<DateTime> callback)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is empty", nameof(name));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (hour < 0 || hour > 23) throw new ArgumentOutOfRangeException(nameof(hour), hour, "hour must be 0-23");
            if (minute < 0 || minute > 59) throw new ArgumentOutOfRangeException(nameof(minute), minute, "minute must be 0-59");

            lock (_sync)
                _moments.Add(new MomentEntry
                {
                    Name = name,
                    Hour = hour,
                    Minute = minute,
                    Day = day,
                    NextDueUtc = DateTime.MinValue,
                    Callback = callback,
                });
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_timer != null)
                    return;
                _timer = new Timer(OnTimer, null, 1000, TimerResolutionMs);
            }
        }

        private void OnTimer(object state)
        {
            if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
                return;   // 上一轮未结束(慢回调), 跳过本轮 — 回调保证不重叠

            try
            {
                CheckOnce(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Clock] check error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
            }
        }

        // 检查一轮并触发到期回调(自测以合成时间直接调用)。
        // 首轮只定位不触发: 启动之前错过的时刻一律不补。
        internal void CheckOnce(DateTime utcNow)
        {
            List<KeyValuePair<string, Action<DateTime>>> dueMinuteTicks = null;
            List<MomentEntry> dueMoments = null;
            List<OneShotEntry> dueOneShots = null;

            lock (_sync)
            {
                // 系统时钟回拨: 重新定位全部基准, 该轮不触发(与"首查只定位"同语义)。
                // 重定位后已过时刻可能随时间重新到来而再次触发 — 消费者幂等前提下无害。
                if (_lastCheckedUtc != DateTime.MinValue && utcNow < _lastCheckedUtc)
                {
                    FileLogger.Log($"[Clock] wall clock moved backwards ({_lastCheckedUtc:HH:mm:ss} -> {utcNow:HH:mm:ss} UTC), re-anchoring");
                    _lastMinuteIndex = utcNow.Ticks / TimeSpan.TicksPerMinute;
                    foreach (var moment in _moments)
                        if (moment.NextDueUtc != DateTime.MinValue)
                            moment.NextDueUtc = ComputeNextDueUtc(moment, utcNow);
                    _lastCheckedUtc = utcNow;
                    return;
                }
                _lastCheckedUtc = utcNow;

                var minuteIndex = utcNow.Ticks / TimeSpan.TicksPerMinute;
                if (_lastMinuteIndex < 0)
                {
                    _lastMinuteIndex = minuteIndex;
                }
                else if (minuteIndex > _lastMinuteIndex)
                {
                    _lastMinuteIndex = minuteIndex;
                    if (_minuteTicks.Count > 0)
                        dueMinuteTicks = new List<KeyValuePair<string, Action<DateTime>>>(_minuteTicks);
                }

                foreach (var moment in _moments)
                {
                    if (moment.NextDueUtc == DateTime.MinValue)
                    {
                        moment.NextDueUtc = ComputeNextDueUtc(moment, utcNow);
                        continue;
                    }

                    if (utcNow >= moment.NextDueUtc)
                    {
                        (dueMoments ?? (dueMoments = new List<MomentEntry>())).Add(moment);
                        moment.NextDueUtc = ComputeNextDueUtc(moment, utcNow);   // 严格晚于now: 间隙塌缩
                    }
                }
            }

            // 回调在锁外执行: 允许回调内再注册, 也避免慢回调拖住注册
            lock (_sync)
            {
                if (_oneShots.Count > 0)
                {
                    foreach (var oneShot in _oneShots.Values)
                    {
                        if (utcNow >= oneShot.DueUtc)
                            (dueOneShots ?? (dueOneShots = new List<OneShotEntry>())).Add(oneShot);
                    }

                    if (dueOneShots != null)
                    {
                        foreach (var oneShot in dueOneShots)
                        {
                            if (_oneShots.TryGetValue(oneShot.Name, out var current)
                                && ReferenceEquals(current, oneShot))
                            {
                                _oneShots.Remove(oneShot.Name);
                            }
                        }
                    }
                }
            }

            if (dueMinuteTicks != null)
                foreach (var tick in dueMinuteTicks)
                    Invoke(tick.Key, tick.Value, utcNow);

            if (dueMoments != null)
                foreach (var moment in dueMoments)
                    Invoke(moment.Name, moment.Callback, utcNow);

            if (dueOneShots != null)
                foreach (var oneShot in dueOneShots)
                    Invoke(oneShot.Name, oneShot.Callback, utcNow);
        }

        private static void Invoke(string name, Action<DateTime> callback, DateTime utcNow)
        {
            try
            {
                callback(utcNow);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Clock] callback '{name}' error: {ex.Message}");
            }
        }

        // 下一个到期时刻(严格晚于 utcNow), 按北京时间帧计算后换回 UTC。
        private static DateTime ComputeNextDueUtc(MomentEntry moment, DateTime utcNow)
        {
            var beijing = utcNow.AddHours(TimeZoneOffsetHours);
            var candidate = beijing.Date.AddHours(moment.Hour).AddMinutes(moment.Minute);
            if (moment.Day.HasValue)
            {
                var forwardDays = ((int)moment.Day.Value - (int)candidate.DayOfWeek + 7) % 7;
                candidate = candidate.AddDays(forwardDays);
            }

            var stepDays = moment.Day.HasValue ? 7 : 1;
            while (candidate <= beijing)
                candidate = candidate.AddDays(stepDays);

            return candidate.AddHours(-TimeZoneOffsetHours);
        }
    }
}
