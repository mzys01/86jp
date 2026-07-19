using DfoServer.Game.Appearance;
using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    // 背包刷新通知的唯一实现点(0x000D 全量 / 0x000E 增量 / 0x02CA 排列锁 / 0x00FB 装备锁 / 0x0002 外观)。
    // 各 handler 经此发送刷新, 不再把 InventoryHandler 当服务拽引用;
    // 将来做多人可见性广播, 只在本类扩展。
    public sealed class InventoryRefreshSender
    {
        private const string ProtocolName = "GameProtocol";

        private readonly IInventoryStore _inventoryStore;
        private readonly SqliteSelectCharacterDataSource _dataSource;   // 仅外观重建(AppearanceService)使用
        private readonly ICharacterRepository _characterRepository;
        private readonly HonorLevelSyncService _honorLevel;

        public InventoryRefreshSender(
            IInventoryStore inventoryStore,
            SqliteSelectCharacterDataSource dataSource,
            ICharacterRepository characterRepository)
        {
            _inventoryStore = inventoryStore ?? throw new ArgumentNullException(nameof(inventoryStore));
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _honorLevel = new HonorLevelSyncService(characterRepository);
        }

        public async Task SendNoti2AppearanceUpdate(EnhancedClientSession session)
        {
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var noti2Body = AppearanceService.UpdateAndBroadcast(
                session.Player, _dataSource, _characterRepository, cid, aid);
            FileLogger.Log($"[{ProtocolName}] NOTI 2 appearance update: {session.Player.AppearanceEntries.Length} entries, body={noti2Body.Length}B");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, noti2Body));
        }

        public void ReloadSubtype0Tail(EnhancedClientSession session)
        {
            if (session?.Player == null)
                return;

            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var tail = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath)
                .Load(cid)
                ?? session.Player.Subtype0Tail
                ?? new UserInfoMinimumTailSnapshot();
            HonorLevelDataProvider.ApplyToSubtype0Tail(tail, _honorLevel.LoadSummary(aid));
            session.Player.Subtype0Tail = tail;
        }

        public async Task SendCreatureItemListRefresh(EnhancedClientSession session)
        {
            var (cid, _) = SessionOwnerResolver.Resolve(session);
            var list = _dataSource.LoadCreatureItemListSnapshot(cid);
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)(list?.Entries.Count ?? 0));
            if (list != null)
            {
                foreach (var entry in list.Entries)
                    CreatureListBodyBuilder.WriteCreatureEntry(writer, entry);
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0069, writer.ToArray()));
        }

        public async Task SendItemListRefresh(EnhancedClientSession session, params InventoryListType[] listTypes)
        {
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var snapshot = _inventoryStore.LoadCharacterItemListSnapshot(cid, aid);

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
                var (cid, aid) = SessionOwnerResolver.Resolve(session);
                var updates = new List<CommonInventoryItem>();
                foreach (var slotIndex in slots)
                {
                    var item = _inventoryStore.LoadCommonItemForRefresh(cid, aid, itemSpace, slotIndex)
                        ?? ItemListUpdateBuilder.CreateEmptyCommonItem(slotIndex);
                    updates.Add(item);
                }

                await SendCommonItemUpdates(session, itemSpace, updates);
                return;
            }

            if (itemSpace == InventoryListType.Avatar)
            {
                var (cid, aid) = SessionOwnerResolver.Resolve(session);
                var updates = new List<AvatarInventoryItem>();
                var emptySlots = new List<short>();
                foreach (var slotIndex in slots)
                {
                    var item = _inventoryStore.LoadAvatarItemForRefresh(cid, slotIndex);
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

            if (itemSpace == InventoryListType.Equipment)
            {
                var (cid, aid) = SessionOwnerResolver.Resolve(session);
                var updates = new List<CommonInventoryItem>();
                var emptySlots = new List<short>();
                foreach (var slotIndex in slots)
                {
                    var item = _inventoryStore.LoadEquipmentCommonItemForRefresh(cid, slotIndex);
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
                        ItemListUpdateBuilder.BuildEquipmentUpdates(updates)));
                }

                if (emptySlots.Count > 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x000E,
                        ItemListUpdateBuilder.BuildEmptyUpdates(itemSpace, emptySlots)));
                }
                return;
            }

            if (itemSpace == InventoryListType.Pet)
            {
                var (cid, aid) = SessionOwnerResolver.Resolve(session);
                var updates = new List<PetInventoryItem>();
                var emptySlots = new List<short>();
                foreach (var slotIndex in slots)
                {
                    var item = _inventoryStore.LoadPetItemForRefresh(cid, slotIndex);
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

        public static Task SendCommonItemUpdates(
            EnhancedClientSession session,
            InventoryListType itemSpace,
            IReadOnlyList<CommonInventoryItem> updates)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (updates == null || updates.Count == 0)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000E,
                ItemListUpdateBuilder.BuildCommonUpdates(itemSpace, updates)));
        }

        public async Task SendSortItemLockSlotRefresh(EnhancedClientSession session, InventoryListType listType, short slotIndex)
        {
            await SendUpdateItemList(session, listType, slotIndex);
        }

        public async Task SendSortItemLockRefresh(EnhancedClientSession session, InventoryListType listType)
        {
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var refreshListType = MapToSortLockListType(listType);
            var locks = _inventoryStore.LoadSortItemLocks(cid, refreshListType);
            foreach (var entry in locks)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CA, SortItemLockBuilder.BuildLock(entry)));
        }

        public async Task SendAllSortItemLockRefresh(EnhancedClientSession session)
        {
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var locks = _inventoryStore.LoadSortItemLocks(cid);
            foreach (var entry in locks)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CA, SortItemLockBuilder.BuildLock(entry)));
        }

        public async Task SendEquipmentItemLockListRefresh(EnhancedClientSession session, InventoryListType listType)
        {
            if (!IsEquipmentItemLockListType(listType))
                return;

            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var locks = _inventoryStore.LoadEquipmentItemLocks(cid);
            LogEquipmentItemLockList("ITEM_LOCK_LIST_REFRESH", locks);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00FB,
                EquipmentItemLockBuilder.BuildLockList(locks)));
        }

        public async Task SendAllEquipmentItemLockListRefresh(EnhancedClientSession session)
        {
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var locks = _inventoryStore.LoadEquipmentItemLocks(cid);
            LogEquipmentItemLockList("ITEM_LOCK_LIST_ALL", locks);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00FB,
                EquipmentItemLockBuilder.BuildLockList(locks)));
        }

        internal static InventoryListType MapToSortLockListType(InventoryListType listType)
        {
            return MapToNotiListType(listType);
        }

        internal static InventoryListType MapToNotiListType(InventoryListType moveListType)
        {
            if (moveListType == InventoryListType.Equipment)
                return InventoryListType.Avatar;
            return moveListType;
        }

        private static bool IsEquipmentItemLockListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.Pet;
        }

        internal static void LogEquipmentItemLockList(string tag, IReadOnlyList<EquipmentItemLockEntry> locks)
        {
            var builder = new StringBuilder();
            builder.Append($"[{ProtocolName}] {tag}: count={locks?.Count ?? 0}");
            if (locks != null)
            {
                foreach (var item in locks)
                    builder.Append($" ({item.ListType},{item.SlotIndex},state={item.State},remain={item.RemainingSeconds})");
            }

            FileLogger.Log(builder.ToString());
        }
    }
}
