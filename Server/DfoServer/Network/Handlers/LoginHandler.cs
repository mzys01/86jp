using DfoServer.Game.Accounts;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class LoginHandler
    {
        private const string DefaultLoginMid = "10038";

        private readonly IAccountRepository _accountRepository;

        public string ProtocolName => "GameProtocol";

        public LoginHandler(IAccountRepository accountRepository)
        {
            _accountRepository = accountRepository ?? throw new ArgumentNullException(nameof(accountRepository));
        }

        public async Task Handle_ClientFirstConnected(EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0001, LoginPacketBuilder.BuildInitialLoginNotice()));
        }

        public async Task Handle_ENUM_CMDPACKET_LOGIN(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            try
            {
                var mId = DefaultLoginMid;
                var passwordHash = string.Empty;
                if (LoginRequestParser.TryParse(body, out var parsed))
                {
                    mId = parsed.MId;
                    passwordHash = parsed.PasswordHash ?? string.Empty;
                    FileLogger.Log($"[{ProtocolName}] Login request parsed: m_id={mId} pwd_md5={passwordHash}");
                }
                else
                {
                    FileLogger.Log($"[{ProtocolName}] Login body unparseable, falling back to m_id={DefaultLoginMid}");
                }

                var account = _accountRepository.GetByMid(mId);
                if (account == null)
                {
                    var newId = _accountRepository.Create(mId, passwordHash);
                    account = _accountRepository.GetById(newId);
                    FileLogger.Log($"[{ProtocolName}] Login auto-created account id={newId} m_id={mId}");
                }
                session.Account = account;
                var remoteIp = session.TcpClient?.Client?.RemoteEndPoint?.ToString() ?? string.Empty;
                _accountRepository.UpdateLastLogin(account.AccountId, remoteIp, DateTime.UtcNow);
                FileLogger.Log($"[{ProtocolName}] Login bound session {session.SessionId} -> account_id={account.AccountId} m_id={account.MId}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] Login account lookup failed: {ex.Message}");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0001, LoginPacketBuilder.BuildLoginSuccess()));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00B7, ServiceNotificationBuilder.BuildAuctionService(0x00, 0x00)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00B7, ServiceNotificationBuilder.BuildAuctionService(0x01, 0x00)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0289, CommonPacketBodyBuilder.BuildZeroBytes(8)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x01A1, CommonPacketBodyBuilder.BuildZeroBytes(1)));
        }
    }
}
