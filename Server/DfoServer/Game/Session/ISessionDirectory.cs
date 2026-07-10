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

        /// <summary>同一 (townId, areaId) 且不在副本中的其它在线会话(排除 excludeCharacterId 自己)。城镇同屏用。</summary>
        IReadOnlyList<EnhancedClientSession> GetSessionsInArea(byte townId, byte areaId, int excludeCharacterId);

        /// <summary>向同一 (townId, areaId) 的其它会话广播封包(排除 excludeCharacterId 自己)。</summary>
        Task BroadcastToAreaAsync(byte townId, byte areaId, int excludeCharacterId, byte[] packet);

        event Func<int, EnhancedClientSession, Task> SessionEnding;
    }
}
