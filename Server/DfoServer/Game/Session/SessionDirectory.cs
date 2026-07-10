using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Network;

namespace DfoServer.Game.Session
{
    public sealed class SessionDirectory : ISessionDirectory
    {
        private readonly ConcurrentDictionary<int, EnhancedClientSession> _byCharacterId = new ConcurrentDictionary<int, EnhancedClientSession>();

        public event Func<int, EnhancedClientSession, Task> SessionEnding;

        public void Register(int characterId, EnhancedClientSession session)
        {
            _byCharacterId[characterId] = session;
            FileLogger.Log($"[SessionDirectory] Registered characterId={characterId} session={session.SessionId}");
        }

        public async Task UnregisterAsync(int characterId)
        {
            if (!_byCharacterId.TryGetValue(characterId, out var session))
                return;

            try
            {
                var handler = SessionEnding;
                if (handler != null)
                {
                    // 逐订阅者隔离异常: 单个订阅者失败不能阻断其余订阅者, 更不能阻断下面的移除。
                    foreach (var d in handler.GetInvocationList())
                    {
                        try
                        {
                            await ((Func<int, EnhancedClientSession, Task>)d)(characterId, session);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log($"[SessionDirectory] SessionEnding subscriber failed for characterId={characterId}: {ex}");
                        }
                    }
                }
            }
            finally
            {
                // 比较移除: await 订阅者期间玩家可能已快速重连并 Register 了新 session,
                // 只有当表里仍是本次拆线的旧 session 时才移除, 不能误删新 session 的注册。
                var removed = ((ICollection<KeyValuePair<int, EnhancedClientSession>>)_byCharacterId)
                    .Remove(new KeyValuePair<int, EnhancedClientSession>(characterId, session));
                FileLogger.Log(removed
                    ? $"[SessionDirectory] Unregistered characterId={characterId}"
                    : $"[SessionDirectory] Unregister skipped, characterId={characterId} re-registered during teardown");
            }
        }

        public bool TryGet(int characterId, out EnhancedClientSession session)
        {
            return _byCharacterId.TryGetValue(characterId, out session);
        }

        public IReadOnlyList<EnhancedClientSession> GetAllGameSessions()
        {
            return _byCharacterId.Values.ToList();
        }

        public async Task SendToAsync(int characterId, byte[] packet)
        {
            if (_byCharacterId.TryGetValue(characterId, out var session))
                await session.SendPacketAsync(packet);
        }

        public async Task BroadcastToAsync(IEnumerable<int> characterIds, byte[] packet)
        {
            var tasks = new List<Task>();
            foreach (var characterId in characterIds)
            {
                if (_byCharacterId.TryGetValue(characterId, out var session))
                    tasks.Add(session.SendPacketAsync(packet));
            }
            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
        }

        // 城镇同屏的区域视图: 同一注册表上的过滤查询, 不另设第二份会话索引。
        // 副本内玩家(CurrentRun != null)一律排除 —— 其 town/area 字段是进本前的残值, 不该出现在城镇名册里。
        public IReadOnlyList<EnhancedClientSession> GetSessionsInArea(byte townId, byte areaId, int excludeCharacterId)
        {
            var result = new List<EnhancedClientSession>();
            foreach (var kvp in _byCharacterId)
            {
                if (kvp.Key == excludeCharacterId) continue;
                var session = kvp.Value;
                var player = session?.Player;
                if (player == null || player.CharacterId <= 0) continue;
                if (player.CurrentRun != null) continue;
                if (player.CurTownId != townId || player.CurAreaId != areaId) continue;
                if (session.TcpClient == null || !session.TcpClient.Connected) continue;
                result.Add(session);
            }
            return result;
        }

        public async Task BroadcastToAreaAsync(byte townId, byte areaId, int excludeCharacterId, byte[] packet)
        {
            var targets = GetSessionsInArea(townId, areaId, excludeCharacterId);
            if (targets.Count == 0) return;
            var tasks = new List<Task>(targets.Count);
            foreach (var session in targets)
                tasks.Add(session.SendPacketAsync(packet));
            await Task.WhenAll(tasks);
        }
    }
}
