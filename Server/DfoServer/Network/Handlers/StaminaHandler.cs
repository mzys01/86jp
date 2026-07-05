using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    // 疲劳(虚弱)恢复: CMD 0x0009 RECOVER_STAMINA。
    // 扣金币 -> 清虚弱值 -> 回 0x0021 + 金币刷新。
    // 与副本流程无关(城镇里也能用), 原先寄居在副本共享服务里, 拆出独立成域。
    public sealed class StaminaHandler
    {
        private const string ProtocolLogName = "GameProtocol";

        private readonly IAssetService _assetService;

        public StaminaHandler(IAssetService assetService)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
        }

        public async Task Handle_ENUM_CMDPACKET_RECOVER_STAMINA(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: uid={session?.Player?.UserId ?? 0} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");

            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0)
                return;

            var accountId = session?.Account?.AccountId ?? 1;
            if (accountId <= 0)
                accountId = 1;

            try
            {
                var repo = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var tail = repo.Load(characterId) ?? session.Player.Subtype0Tail;
                if (tail == null || tail.Stamina == 0)
                {
                    await SendRecoverStaminaErrorAsync(session, 18);
                    FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: no weakness state cid={characterId}");
                    return;
                }

                var cost = CalculateRecoverStaminaGoldCost(session.Player.Level, tail.Stamina);
                int updatedGold;
                using (var scope = _assetService.OpenScope(characterId, accountId))
                {
                    var wallet = _assetService.LoadWallet(scope);
                    if (wallet.Gold < cost)
                    {
                        await SendRecoverStaminaErrorAsync(session, 22);
                        FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: insufficient gold cid={characterId} need={cost} have={wallet.Gold} stamina={tail.Stamina}");
                        return;
                    }

                    updatedGold = wallet.Gold - cost;
                    if (cost > 0 && !_assetService.TrySpendGold(scope, cost))
                    {
                        await SendRecoverStaminaErrorAsync(session, 22);
                        FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: TrySpendGold refused cid={characterId} need={cost}");
                        return;
                    }
                    scope.Commit();
                }

                tail.Stamina = 0;
                tail.FatiguePenalty = 0;
                SaveSubtype0Tail(characterId, tail);
                session.Player.Subtype0Tail = tail;

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0021, new[] { (byte)100 }));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                    ItemListUpdateBuilder.BuildGoldUpdate(updatedGold)));

                FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: success cid={characterId} cost={cost} gold={updatedGold}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA ERROR: cid={characterId} {ex}");
                await SendRecoverStaminaErrorAsync(session, 4);
            }
        }

        internal static int CalculateRecoverStaminaGoldCost(byte level, byte stamina)
        {
            if (stamina == 0)
                return 0;

            var basePrice = RecoverStaminaPriceProvider.GetBasePrice(level);
            var normalizedStamina = Math.Min((byte)10, stamina);
            var officialCurrentStamina = Math.Max(0, 100 - normalizedStamina * 9);
            var cost = basePrice * (100 - officialCurrentStamina) / 90;
            return Math.Max(0, cost);
        }

        private static void SaveSubtype0Tail(int characterId, UserInfoMinimumTailSnapshot tail)
        {
            var connStr = SqliteDatabaseBootstrap.Initialize(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
            {
                conn.Open();
                SqliteSubtype0FieldsRepository.Save(conn, characterId, tail);
            }
        }

        private static Task SendRecoverStaminaErrorAsync(EnhancedClientSession session, byte errorCode)
        {
            if (session == null || session.TcpClient == null || !session.TcpClient.Connected)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0009, new[] { (byte)0, errorCode, (byte)0 }));
        }
    }
}
