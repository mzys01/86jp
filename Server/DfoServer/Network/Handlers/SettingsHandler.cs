using System;
using DfoServer.Game.Settings;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers
{
    public sealed class SettingsHandler
    {
        private readonly AccountSettingsRepository _repo;

        public SettingsHandler()
        {
            _repo = new AccountSettingsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
        }

        public void Handle_SAVE_GAME_OPTION_1(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4) return;
            int len = BitConverter.ToInt32(body, 0);
            if (len <= 0 || body.Length < 4 + len) return;

            var blob = new byte[len];
            Buffer.BlockCopy(body, 4, blob, 0, len);

            int aid = session.Account?.AccountId ?? 1;
            _repo.SaveMainOption(aid, blob);
            FileLogger.Log($"[GameProtocol] SAVE_GAME_OPTION_1: account={aid} len={len}");
        }

        public void Handle_SAVE_GAME_OPTION_2(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4) return;
            int len = BitConverter.ToInt32(body, 0);
            if (len <= 0 || body.Length < 4 + len) return;

            var blob = new byte[len];
            Buffer.BlockCopy(body, 4, blob, 0, len);

            int aid = session.Account?.AccountId ?? 1;
            _repo.SaveHotkeySlots(aid, blob);
            FileLogger.Log($"[GameProtocol] SAVE_GAME_OPTION_2: account={aid} len={len}");
        }

        public void Handle_SAVE_QUICKCHAT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            
            if (body == null || body.Length < 5) return;
            int bankIndex = body[0];
            if (bankIndex > 1) return;
            int len = BitConverter.ToInt32(body, 1);
            if (len <= 0 || body.Length < 5 + len) return;

            var blob = new byte[len];
            Buffer.BlockCopy(body, 5, blob, 0, len);

            int aid = session.Account?.AccountId ?? 1;
            _repo.SaveQuickchatBank(aid, bankIndex, blob);
            FileLogger.Log($"[GameProtocol] SAVE_QUICKCHAT: account={aid} bank={bankIndex} len={len}");
        }
    }
}
