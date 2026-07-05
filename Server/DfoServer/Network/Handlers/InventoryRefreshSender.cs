using DfoServer.Game.Appearance;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Skills;
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

        public InventoryRefreshSender(
            IInventoryStore inventoryStore,
            SqliteSelectCharacterDataSource dataSource,
            ICharacterRepository characterRepository)
        {
            _inventoryStore = inventoryStore ?? throw new ArgumentNullException(nameof(inventoryStore));
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
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
            var (cid, _) = SessionOwnerResolver.Resolve(session);
            var tail = _dataSource.LoadSubtype0TailSnapshot(cid);
            if (tail != null && session?.Player != null)
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

        public async Task SendSubtype0PetStateRefresh(EnhancedClientSession session)
        {
            var player = session?.Player;
            if (player == null)
                return;

            var appearanceEntries = player.AppearanceEntries;
            try
            {
                AppearanceService.RepairLegacyTitleAppearanceBlobIfNeeded(player.CharacterId);
                var storedRecord = _characterRepository.GetById(player.CharacterId);
                if (storedRecord?.Appearance != null && storedRecord.Appearance.Length > 0)
                    appearanceEntries = storedRecord.Appearance;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] NOTI 2 pet state appearance fallback failed: {ex.Message}");
            }

            var record = new CharacterRecord
            {
                CharacterId = player.CharacterId,
                Name = player.Name,
                Job = player.Job,
                GrowType = player.GrowType,
                Level = player.Level,
                UserState = player.UserState,
                Appearance = appearanceEntries,
                Subtype0Tail = player.Subtype0Tail,
            };

            // Pet body changes use NOTI2 USERINFO subtype0 (body mode 0) with one minimum record.
            // Do not send the roster subtype2 form here; PR #376 clone-title roster fixes stay in that path.
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteUInt16(0x0001);
            writer.WriteUInt16(player.UserId);
            writer.WriteDstr(player.Name);
            writer.WriteBytes(UserInfoSubtype0Builder.BuildRemainingBytes(record));
            var body = writer.ToArray();
            FileLogger.Log($"[{ProtocolName}] NOTI 2 pet state update: petFlag={player.Subtype0Tail?.PetDisplayFlag ?? 0} body={body.Length}B");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, body));
        }

        public async Task SendSubtype1Refresh(EnhancedClientSession session)
        {
            var (cid, _) = SessionOwnerResolver.Resolve(session);
            if (cid <= 0)
                return;

            var subtype1Repo = new SqliteSubtype1Repository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            if (!subtype1Repo.HasData(cid))
                return;

            var addition = subtype1Repo.Load(cid);
            if (addition == null)
                return;

            var record = _characterRepository.GetById(cid);
            var skillRepo = new SqliteCharacterProgressRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            var skills = skillRepo.HasSkills(cid)
                ? SkillStateService.LoadAndSync(
                    skillRepo,
                    cid,
                    record?.Job ?? session.Player.Job,
                    record?.Level ?? session.Player.Level,
                    record?.BonusSp ?? 0,
                    record?.BonusTp ?? 0,
                    persist: false).Skills
                : null;

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteUInt16(1);
            writer.WriteUInt16((ushort)cid);
            writer.WriteBytes(UserInfoSubtype1Builder.BuildFromSnapshot(addition, skills));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, writer.ToArray()));
        }

        public async Task SendPetItemSlotRefresh(EnhancedClientSession session, IReadOnlyList<short> slotIndexes)
        {
            if (slotIndexes == null || slotIndexes.Count == 0)
                return;

            var (cid, _) = SessionOwnerResolver.Resolve(session);
            var updates = new List<PetInventoryItem>();
            var seen = new HashSet<short>();
            foreach (var slotIndex in slotIndexes)
            {
                if (SqliteInventoryStore.IsPetServerStorageSlot(slotIndex) || !seen.Add(slotIndex))
                    continue;

                var item = _inventoryStore.LoadPetItemForRefresh(cid, slotIndex);
                if (item != null)
                    updates.Add(item);
            }

            if (updates.Count == 0)
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000E,
                ItemListUpdateBuilder.BuildPetUpdates(updates)));
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

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x000E,
                    ItemListUpdateBuilder.BuildCommonUpdates(itemSpace, updates)));
                return;
            }

            if (itemSpace == InventoryListType.Avatar || itemSpace == InventoryListType.Equipment)
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
