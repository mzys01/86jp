using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Network;

namespace DfoServer.Game.Session
{
    public interface ISessionDirectory
    {
        void Register(int characterId, EnhancedClientSession session);
        Task UnregisterAsync(int characterId);
        bool TryGet(int characterId, out EnhancedClientSession session);
        IReadOnlyList<EnhancedClientSession> GetAllGameSessions();

        Task SendToAsync(int characterId, byte[] packet);
        Task BroadcastToAsync(IEnumerable<int> characterIds, byte[] packet);

        event Func<int, EnhancedClientSession, Task> SessionEnding;
    }
}
