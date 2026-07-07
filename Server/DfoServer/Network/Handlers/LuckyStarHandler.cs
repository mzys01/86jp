using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    /// 租赁商店：购买幸运星（0x0373）。
    public sealed class LuckyStarHandler
    {
        private readonly IAssetService _assetService;
        private readonly SqliteSelectCharacterDataSource _dataSource;
        private readonly IRentalTimeProvider _rentalTimeProvider;

        public LuckyStarHandler(
            IAssetService assetService,
            SqliteSelectCharacterDataSource dataSource,
            IRentalTimeProvider rentalTimeProvider = null)
        {
            _assetService = assetService;
            _dataSource = dataSource;
            _rentalTimeProvider = rentalTimeProvider ?? SystemRentalTimeProvider.Instance;
        }

        public async Task HandleShopPurchasePacket(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (characterId, _) = InventoryHandler.ResolveOwner(session);
            if (characterId <= 0 || body == null || body.Length < RentalCatalogCodec.ShopPacketQtyOffset + 2)
                return;

            if (!RentalCatalogCodec.TryParseShopPacketBuyCount(body, out var buyCount))
            {
                FileLogger.Log($"[LuckyStar] REJECT 0x0373 char={characterId} invalid qty bodyLen={body.Length} tail={BitConverter.ToString(body, Math.Max(0, body.Length - 8))}");
                await Send0373Error(session);
                return;
            }

            await ExecuteLuckyStarPurchase(session, buyCount, body);
        }

        private async Task ExecuteLuckyStarPurchase(EnhancedClientSession session, int buyCount, byte[] purchaseRequestBody)
        {
            var (characterId, accountId) = InventoryHandler.ResolveOwner(session);
            if (characterId <= 0)
                return;

            FileLogger.Log($"[LuckyStar] BUY request: char={characterId} buyCount={buyCount} via=0x0373");
            var totalGoldCost = RentalCatalogCodec.GoldCostPerStar * buyCount;

            var newLuckyStar = (ushort)0;
            int newGold;

            // 金币扣减和幸运星增加必须同事务提交，避免只扣金币或只加星。
            using (var scope = _assetService.OpenScope(characterId, accountId))
            {
                var wallet = _assetService.LoadWallet(scope);
                if (wallet.LuckyStar + buyCount > RentalCatalogCodec.MaxLuckyStar)
                {
                    await Send0373Error(session);
                    return;
                }

                if (wallet.Gold < totalGoldCost)
                {
                    FileLogger.Log($"[LuckyStar] BUY: insufficient gold need={totalGoldCost} have={wallet.Gold} char={characterId}");
                    await Send0373Error(session);
                    return;
                }

                newGold = wallet.Gold - totalGoldCost;
                newLuckyStar = (ushort)(wallet.LuckyStar + buyCount);
                if (!_assetService.TrySpendGold(scope, totalGoldCost))
                {
                    FileLogger.Log($"[LuckyStar] BUY: TrySpendGold refused need={totalGoldCost} char={characterId}");
                    await Send0373Error(session);
                    return;
                }

                _assetService.GrantLuckyStar(scope, buyCount);
                scope.Commit();
            }

            FileLogger.Log($"[LuckyStar] BUY: char={characterId} count={buyCount} gold=-{totalGoldCost} -> {newGold} stars={newLuckyStar}");

            await LuckyStarClientNotifier.SyncPurchaseAsync(
                session,
                _dataSource,
                characterId,
                accountId,
                (ushort)buyCount,
                newLuckyStar,
                _rentalTimeProvider,
                purchaseRequestBody);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                ItemListUpdateBuilder.BuildGoldUpdate(newGold)));
        }

        private static async Task Send0373Error(EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0373, new byte[] { 0x00, 0x04 }));
        }
    }
}
