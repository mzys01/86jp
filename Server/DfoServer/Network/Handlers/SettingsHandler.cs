using System;
using System.Threading.Tasks;
using DfoServer.Game.Appearance;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Settings;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers
{
    public sealed class SettingsHandler
    {
        private readonly AccountSettingsRepository _repo;
        private readonly ICharacterStateRepository _characterStateRepository;

        public SettingsHandler()
        {
            _repo = new AccountSettingsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            _characterStateRepository = new SqliteCharacterStateRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
        }

        public void Handle_SAVE_GAME_OPTION_1(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4) return;
            int len = BitConverter.ToInt32(body, 0);
            if (len <= 0 || body.Length < 4 + len) return;

            var blob = new byte[len];
            Buffer.BlockCopy(body, 4, blob, 0, len);

            var accountId = session.Account?.AccountId ?? 1;
            _repo.SaveMainOption(accountId, blob);
            FileLogger.Log($"[GameProtocol] SAVE_GAME_OPTION_1: account={accountId} len={len}");
        }

        public void Handle_SAVE_GAME_OPTION_2(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4) return;
            int len = BitConverter.ToInt32(body, 0);
            if (len <= 0 || body.Length < 4 + len) return;

            var blob = new byte[len];
            Buffer.BlockCopy(body, 4, blob, 0, len);

            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);
            if (characterId > 0)
            {
                _characterStateRepository.SaveHotkeyConfig(characterId, blob);
                _repo.SaveHotkeySlots(accountId, AccountSettings.ExtractAccountScopedHotkeySlots(blob));
                FileLogger.Log($"[GameProtocol] SAVE_GAME_OPTION_2: character={characterId} account={accountId} len={len}");
                return;
            }

            _repo.SaveHotkeySlots(accountId, AccountSettings.ExtractAccountScopedHotkeySlots(blob));
            FileLogger.Log($"[GameProtocol] SAVE_GAME_OPTION_2: account={accountId} len={len}");
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

        public async Task Handle_CHANGE_EMOTION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);
            if (characterId <= 0 || body == null || body.Length < 2)
            {
                FileLogger.Log($"[GameProtocol] CHANGE_EMOTION ignored: character={characterId} account={accountId} len={body?.Length ?? 0}");
                return;
            }

            var emotionIndex = BitConverter.ToUInt16(body, 0);
            _characterStateRepository.SaveEmotionIndex(characterId, emotionIndex);

            if (session?.Player != null)
            {
                var tail = session.Player.Subtype0Tail ?? new Game.SelectCharacter.UserInfoMinimumTailSnapshot();
                tail.MoodValue = emotionIndex;
                tail.EmotionIndex = emotionIndex;
                tail.ActionByte = (byte)Math.Min(emotionIndex, (ushort)byte.MaxValue);
                session.Player.Subtype0Tail = tail;

                var notiBody = AppearanceService.BuildNoti2Body(session.Player);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, notiBody));
            }

            FileLogger.Log($"[GameProtocol] CHANGE_EMOTION: character={characterId} account={accountId} emotion={emotionIndex}");
        }

        public void Handle_SAVE_CHARACTER_OPTION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);
            if (characterId <= 0 || body == null)
            {
                FileLogger.Log($"[GameProtocol] SAVE_CHARACTER_OPTION ignored: character={characterId} account={accountId} len={body?.Length ?? 0}");
                return;
            }

            var repo = new Game.CharacterData.SqliteCharacterStateRepository(
                ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            repo.SaveCharacterOption(characterId, body);
            FileLogger.Log($"[GameProtocol] SAVE_CHARACTER_OPTION: character={characterId} account={accountId} len={body.Length}");
        }
    }
}
