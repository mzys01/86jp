using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        private readonly SqliteSelectCharacterDataSource _sqliteSelectCharacterDataSource;
        private readonly ICharacterRepository _characterRepository;
        private readonly Func<byte[], Task> _broadcastGamePacket;

        public string ProtocolName => "GameProtocol";

        public InventoryHandler(
            SqliteSelectCharacterDataSource sqliteSelectCharacterDataSource,
            ICharacterRepository characterRepository,
            Func<byte[], Task> broadcastGamePacket = null)
        {
            _sqliteSelectCharacterDataSource = sqliteSelectCharacterDataSource ?? throw new ArgumentNullException(nameof(sqliteSelectCharacterDataSource));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _broadcastGamePacket = broadcastGamePacket;
        }

        private async Task SendNoti2AppearanceUpdate(EnhancedClientSession session)
        {
            var (cid, aid) = ResolveOwner(session);
            var noti2Body = AppearanceService.UpdateAndBroadcast(
                session.Player, _sqliteSelectCharacterDataSource, _characterRepository, cid, aid);
            FileLogger.Log($"[{ProtocolName}] NOTI 2 appearance update: {session.Player.AppearanceEntries.Length} entries, body={noti2Body.Length}B");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, noti2Body));
        }

        public async Task SendItemListRefresh(EnhancedClientSession session, params InventoryListType[] listTypes)
        {
            var (cid, aid) = ResolveOwner(session);
            var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);

            foreach (var listType in listTypes.Distinct().Select(MapToNotiListType).Distinct())
            {
                var itemBody = ItemListPacketBuilder.BuildBody(snapshot, listType);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000D, itemBody));
            }
        }

        public Task SendUpdateItemList(EnhancedClientSession session, InventoryListType itemSpace, short slotIndex)
        {
            return SendUpdateItemList(session, itemSpace, new[] { slotIndex });
        }

        public async Task SendUpdateItemList(EnhancedClientSession session, InventoryListType itemSpace, IEnumerable<short> slotIndexes)
        {
            if (slotIndexes == null)
                return;

            var slots = slotIndexes.Distinct().ToList();
            if (slots.Count == 0)
                return;

            if (ItemListUpdateBuilder.IsCommonUpdateItemSpace(itemSpace))
            {
                var (cid, aid) = ResolveOwner(session);
                var updates = new List<CommonInventoryItem>();
                foreach (var slotIndex in slots)
                {
                    var item = _sqliteSelectCharacterDataSource.LoadCommonItemForRefresh(cid, aid, itemSpace, slotIndex)
                        ?? ItemListUpdateBuilder.CreateEmptyCommonItem(slotIndex);
                    updates.Add(item);
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x000E,
                    ItemListUpdateBuilder.BuildCommonUpdates(itemSpace, updates)));
                return;
            }

            if (itemSpace == InventoryListType.Avatar || itemSpace == InventoryListType.Equipment)
            {
                var (cid, aid) = ResolveOwner(session);
                var updates = new List<AvatarInventoryItem>();
                var emptySlots = new List<short>();
                foreach (var slotIndex in slots)
                {
                    var item = _sqliteSelectCharacterDataSource.LoadAvatarItemForRefresh(cid, aid, slotIndex);
                    if (item != null)
                        updates.Add(item);
                    else
                        emptySlots.Add(slotIndex);
                }

                if (updates.Count > 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x000E,
                        ItemListUpdateBuilder.BuildAvatarUpdates(itemSpace, updates)));
                }

                if (emptySlots.Count > 0)
                {
                    // 时装/穿戴栏空槽刷新先按通用空 entry 测试，若客户端不消费再回退完整 0x0D。
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x000E,
                        ItemListUpdateBuilder.BuildEmptyUpdates(itemSpace, emptySlots)));
                }
                return;
            }

            if (itemSpace == InventoryListType.Pet)
            {
                var (cid, aid) = ResolveOwner(session);
                var updates = new List<PetInventoryItem>();
                var emptySlots = new List<short>();
                foreach (var slotIndex in slots)
                {
                    var item = _sqliteSelectCharacterDataSource.LoadPetItemForRefresh(cid, aid, slotIndex);
                    if (item != null)
                        updates.Add(item);
                    else
                        emptySlots.Add(slotIndex);
                }

                if (updates.Count > 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x000E,
                        ItemListUpdateBuilder.BuildPetUpdates(updates)));
                }

                if (emptySlots.Count > 0)
                {
                    // 宠物空槽刷新先按通用空 entry 测试，若客户端不消费再回退完整 0x0D。
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x000E,
                        ItemListUpdateBuilder.BuildEmptyUpdates(itemSpace, emptySlots)));
                }
                return;
            }

        }

        public async Task SendSortItemLockSlotRefresh(EnhancedClientSession session, InventoryListType listType, short slotIndex)
        {
            await SendUpdateItemList(session, listType, slotIndex);
        }

        public async Task SendSortItemLockRefresh(EnhancedClientSession session, InventoryListType listType)
        {
            var (cid, aid) = ResolveOwner(session);
            var refreshListType = MapToSortLockListType(listType);
            var locks = _sqliteSelectCharacterDataSource.LoadSortItemLocks(cid, aid, refreshListType);
            foreach (var entry in locks)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CA, SortItemLockBuilder.BuildLock(entry)));
        }

        public async Task SendAllSortItemLockRefresh(EnhancedClientSession session)
        {
            var (cid, aid) = ResolveOwner(session);
            var locks = _sqliteSelectCharacterDataSource.LoadSortItemLocks(cid, aid);
            foreach (var entry in locks)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CA, SortItemLockBuilder.BuildLock(entry)));
        }

        private static InventoryListType MapToSortLockListType(InventoryListType listType)
        {
            return MapToNotiListType(listType);
        }

        private static InventoryListType MapToNotiListType(InventoryListType moveListType)
        {
            if (moveListType == InventoryListType.Equipment)
                return InventoryListType.Avatar;
            return moveListType;
        }

        public static (int characterId, int accountId) ResolveOwner(EnhancedClientSession session)
            => SessionOwnerResolver.Resolve(session);

        public static bool TryParseDeleteOrSellRequest(byte[] body, out InventoryListType listType, out short slotIndex, out short itemCount)
        {
            listType = InventoryListType.Main;
            slotIndex = 0;
            itemCount = 0;

            if (body == null || body.Length < 4)
                return false;

            if (body.Length >= 5 && Enum.IsDefined(typeof(InventoryListType), (byte)body[0]))
            {
                listType = (InventoryListType)body[0];
                slotIndex = BitConverter.ToInt16(body, 1);
                itemCount = BitConverter.ToInt16(body, 3);
                return true;
            }

            slotIndex = BitConverter.ToInt16(body, 0);
            itemCount = BitConverter.ToInt16(body, 2);
            return true;
        }

        private static List<PackageGrantedItem> ToPackageGrantedItems(BoosterUseResult result)
        {
            var items = new List<PackageGrantedItem>();
            if (result == null)
                return items;

            foreach (var reward in result.Rewards)
            {
                items.Add(new PackageGrantedItem
                {
                    ListType = reward.ListType,
                    SlotIndex = reward.SlotIndex,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = reward.GrantedCount <= 0 ? 1 : reward.GrantedCount,
                    Durability = 0,
                });
            }

            return items;
        }
    }
}
