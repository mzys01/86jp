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
            if (_byCharacterId.TryGetValue(characterId, out var session))
            {
                var handler = SessionEnding;
                if (handler != null)
                {
                    foreach (var d in handler.GetInvocationList())
                        await ((Func<int, EnhancedClientSession, Task>)d)(characterId, session);
                }
                _byCharacterId.TryRemove(characterId, out _);
                FileLogger.Log($"[SessionDirectory] Unregistered characterId={characterId}");
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
    }
}
