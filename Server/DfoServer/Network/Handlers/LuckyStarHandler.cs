using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    /// 租赁商店幸运星：购买（0x0373）。
    public sealed class LuckyStarHandler
    {
        private const ushort NotiRental = 0x0357;

        private readonly IAssetService _assetService;
        private readonly SqliteSelectCharacterDataSource _dataSource;

        public LuckyStarHandler(IAssetService assetService, SqliteSelectCharacterDataSource dataSource)
        {
            _assetService = assetService;
            _dataSource = dataSource;
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

            var accountCatalog = _dataSource.LoadAccountMainOption(accountId);
            var purchaseAck = RentalCatalogCodec.BuildPurchaseAck(accountCatalog, (ushort)buyCount, newLuckyStar);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00C5, purchaseAck));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0373, Build0373SuccessAck(buyCount, newLuckyStar, purchaseRequestBody)));
            await SyncRentalPanelNoti(session, newLuckyStar, LoadRentalInfo(characterId));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                ItemListUpdateBuilder.BuildGoldUpdate(newGold)));
        }

        private async Task SyncRentalPanelNoti(EnhancedClientSession session, ushort luckyStar, RentalInfoSnapshot rental)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, NotiRental,
                RentalInfoBodyBuilder.BuildWireBody(luckyStar, rental)));
        }

        private RentalInfoSnapshot LoadRentalInfo(int characterId)
        {
            var rental = new RentalInfoSnapshot();
            RentalInfoSnapshot.ParseStorageBody(_dataSource.LoadCharacterInitBody(characterId, NotiRental), rental);
            return rental;
        }

        private static async Task Send0373Error(EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0373, new byte[] { 0x00, 0x04 }));
        }

        private static byte[] Build0373SuccessAck(int buyCount, ushort totalLuckyStar, byte[] purchaseRequestBody)
        {
            var requestLength = Math.Max(purchaseRequestBody?.Length ?? 0, RentalCatalogCodec.ShopPacketQtyOffset + 4);
            var body = new byte[1 + requestLength];
            body[0] = 0x01;

            if (purchaseRequestBody != null && purchaseRequestBody.Length > 0)
                Buffer.BlockCopy(purchaseRequestBody, 0, body, 1, purchaseRequestBody.Length);

            Buffer.BlockCopy(BitConverter.GetBytes((int)totalLuckyStar), 0, body, 1 + 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(buyCount), 0, body, 1 + RentalCatalogCodec.ShopPacketQtyOffset, 4);
            return body;
        }
    }
}
