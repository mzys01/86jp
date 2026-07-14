using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    /// 租赁商店：租赁武器（0x0372）。
    public sealed class RentalHandler
    {
        private readonly IAssetService _assetService;
        private readonly IInventoryStore _inventoryStore;
        private readonly IRentalTimeProvider _rentalTimeProvider;
        private readonly SqliteSelectCharacterDataSource _dataSource;

        public RentalHandler(
            IAssetService assetService,
            IInventoryStore inventoryStore,
            SqliteSelectCharacterDataSource dataSource,
            IRentalTimeProvider rentalTimeProvider = null)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
            _inventoryStore = inventoryStore ?? throw new ArgumentNullException(nameof(inventoryStore));
            _dataSource = dataSource;
            _rentalTimeProvider = rentalTimeProvider ?? SystemRentalTimeProvider.Instance;
        }

        public async Task HandleRentWeapon(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (characterId, accountId) = InventoryHandler.ResolveOwner(session);
            if (characterId <= 0)
                return;

            if (!RentalWeaponRequestCodec.TryParse(body, out var weaponId, out var parsedInventoryId, out var starCost, out var priceTier))
            {
                var tail = body == null || body.Length == 0
                    ? string.Empty
                    : BitConverter.ToString(body, Math.Max(0, body.Length - 8));
                var head = body == null || body.Length == 0
                    ? string.Empty
                    : BitConverter.ToString(body, 0, Math.Min(13, body.Length));
                var detail = RentalWeaponRequestCodec.DescribeParseFailure(body);
                FileLogger.Log($"[Rental] REJECT 0x0372 char={characterId} parse failed bodyLen={body?.Length ?? 0} head={head} tail={tail} detail={detail}");
                await Send0372Error(session);
                return;
            }

            var inventoryTemplateId = (int)parsedInventoryId;
            var rental = LoadRentalInfo(characterId);
            var expireTime = (int)ResolveRentalExpireTime();
            rental.UpsertItem(weaponId, (uint)inventoryTemplateId, (uint)expireTime);

            short invSlot = -1;
            int instanceValue = 0;
            var pickupSucceeded = false;
            ushort luckyStar = 0;

            // 扣幸运星、发放装备、保存 0x0357 内部存储必须同事务提交；发放失败则整体回滚。
            using (var scope = _assetService.OpenScope(characterId, accountId))
            {
                var currentLuckyStar = _assetService.LoadWallet(scope).LuckyStar;
                if (!_assetService.TrySpendLuckyStar(scope, starCost))
                {
                    FileLogger.Log($"[Rental] RENT_WEAPON: insufficient stars need={starCost} have={currentLuckyStar} char={characterId}");
                    await Send0372Error(session);
                    return;
                }

                pickupSucceeded = _inventoryStore.TryPickupRentalWeapon(
                    scope.Connection, scope.Transaction, characterId, accountId, inventoryTemplateId, expireTime, out invSlot, out instanceValue);

                if (pickupSucceeded)
                {
                    _dataSource.SaveRentalInfo(scope.Connection, scope.Transaction, characterId, rental);
                    luckyStar = (ushort)Math.Max(0, currentLuckyStar - starCost);
                    scope.Commit();
                }
            }

            if (!pickupSucceeded)
            {
                FileLogger.Log($"[Rental] RENT_WEAPON: pickup FAILED (inventory full or invalid) shop=0x{weaponId:X8} inv=0x{inventoryTemplateId:X8} char={characterId}");
                await Send0372Error(session);
                return;
            }

            if (invSlot >= 0)
                FileLogger.Log($"[Rental] RENT_WEAPON: added/refreshed inv shop=0x{weaponId:X8} inv=0x{inventoryTemplateId:X8} invSlot={invSlot} char={characterId}");
            else
                FileLogger.Log($"[Rental] RENT_WEAPON: refreshed equipped shop=0x{weaponId:X8} inv=0x{inventoryTemplateId:X8} char={characterId}");

            FileLogger.Log($"[Rental] RENT_WEAPON: char={characterId} weapon=0x{weaponId:X8} inv=0x{parsedInventoryId:X8} cost={starCost} priceTier={priceTier} starsLeft={luckyStar} expire={expireTime}");

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0372, CommonPacketBodyBuilder.BuildSuccessAck()));
            await RentalInfoPanelNotifier.SyncAsync(session, _dataSource, characterId, luckyStar, _rentalTimeProvider);

            if (invSlot >= 0)
            {
                var itemSnapshot = _inventoryStore.LoadCharacterItemListSnapshot(characterId, accountId);
                var pickedItem = itemSnapshot.MainItems.FirstOrDefault(i => i.SlotIndex == invSlot);
                if (pickedItem != null)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                        ItemListUpdateBuilder.BuildCommonUpdates(new[] { pickedItem })));
                }
                else
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                        ItemListUpdateBuilder.BuildCommonUpdates(new[]
                        {
                            new CommonInventoryItem
                            {
                                SlotIndex = invSlot,
                                ItemTemplateId = inventoryTemplateId,
                                CountOrInstanceValue = instanceValue > 0
                                    ? instanceValue
                                    : RentalWeaponRequestCodec.RentalWeaponQualitySeed,
                                Durability = RentalWeaponRequestCodec.RentalWeaponDurability,
                                Marker16 = -1,
                                ExpireTime = expireTime,
                            }
                        })));
                }
            }
        }

        private RentalInfoSnapshot LoadRentalInfo(int characterId)
        {
            return _dataSource.LoadRentalInfo(characterId);
        }

        private static async Task Send0372Error(EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0372, new byte[] { 0x00, 0x04 }));
        }

        private uint ResolveRentalExpireTime()
        {
            return _rentalTimeProvider.UtcNowUnixSeconds() + (uint)RentalWeaponRequestCodec.RentalDurationSeconds;
        }
    }
}
