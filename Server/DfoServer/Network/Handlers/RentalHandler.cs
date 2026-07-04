using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    /// 租赁商店：租武器（0x0372）。
    public sealed class RentalHandler
    {
        private const ushort NotiRental = 0x0357;

        private readonly IAssetService _assetService;
        private readonly IInventoryStore _inventoryStore;
        private readonly SqliteSelectCharacterDataSource _dataSource;   // 仅 init body 读写(0x0357 面板)

        public RentalHandler(
            IAssetService assetService,
            IInventoryStore inventoryStore,
            SqliteSelectCharacterDataSource dataSource)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
            _inventoryStore = inventoryStore ?? throw new ArgumentNullException(nameof(inventoryStore));
            _dataSource = dataSource;
        }

        public async Task HandleRentWeapon(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (characterId, accountId) = InventoryHandler.ResolveOwner(session);
            if (characterId <= 0)
                return;

            if (!RentalWeaponRequestCodec.TryParse(body, out var weaponId, out var parsedInventoryId, out var starCost, out var priceTier))
            {
                var tail = body == null || body.Length == 0
                    ? ""
                    : BitConverter.ToString(body, Math.Max(0, body.Length - 8));
                var head = body == null || body.Length == 0
                    ? ""
                    : BitConverter.ToString(body, 0, Math.Min(13, body.Length));
                var detail = RentalWeaponRequestCodec.DescribeParseFailure(body);
                FileLogger.Log($"[Rental] REJECT 0x0372 char={characterId} parse failed bodyLen={body?.Length ?? 0} head={head} tail={tail} detail={detail}");
                await Send0372Error(session);
                return;
            }

            var inventoryTemplateId = (int)parsedInventoryId;

            var rental = LoadRentalInfo(characterId);
            var expireTime = (int)ResolveRentalExpireTime();
            ResetRentalItemExpire(rental, weaponId, (uint)expireTime);

            short invSlot = -1;
            int instanceValue = 0;
            var pickupSucceeded = false;
            ushort luckyStar = 0;

            // 余额检查与扣减在同一事务内(条件扣减), 不足额直接失败, 不再有跨scope的检查/扣减竞态
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
                // pickup 失败: 不提交, 星币扣减随事务回滚
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
            await SyncRentalPanelNoti(session, luckyStar, rental);

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

        private static async Task Send0372Error(EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0372, new byte[] { 0x00, 0x04 }));
        }

        private static uint ResolveRentalExpireTime()
        {
            var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return now + (uint)RentalWeaponRequestCodec.RentalDurationSeconds;
        }

        private static void ResetRentalItemExpire(RentalInfoSnapshot rental, uint weaponId, uint expireTime)
        {
            for (var i = 0; i < rental.Items.Count; i++)
            {
                if (rental.Items[i].ItemId == weaponId)
                {
                    rental.Items[i].ExpireTime = expireTime;
                    return;
                }
            }

            rental.Items.Add(new RentalItemSnapshot { ItemId = weaponId, ExpireTime = expireTime });
        }
    }
}
