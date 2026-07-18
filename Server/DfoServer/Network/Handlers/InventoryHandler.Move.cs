using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Pets;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        internal const string CharmQuickSlotLimitNoticeMessage = "快捷栏最多只能放置1个符咒。";

        public async Task Handle_ENUM_CMDPACKET_MOVE_ITEMSPACE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 14)
            {
                if (body != null && body.Length >= 4)
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0013,
                        MoveItemSpaceAckBuilder.BuildError(0x04, body[0], body.Length > 11 ? body[11] : body[0])));
                return;
            }

            var request = new InventoryMoveRequest
            {
                SourceListType = (InventoryListType)body[0],
                SourceSlotIndex = BitConverter.ToInt16(body, 1),
                SourceInstanceValue = BitConverter.ToInt32(body, 3),
                MoveCount = BitConverter.ToInt32(body, 7),
                DestinationListType = (InventoryListType)body[11],
                DestinationSlotIndex = BitConverter.ToInt16(body, 12),
                DestinationInstanceValue = body.Length >= 18 ? BitConverter.ToInt32(body, 14) : 0,
            };

            var srcIV = BitConverter.ToInt32(body, 3);
            var srcStack = BitConverter.ToInt32(body, 7);
            var dstStack = body.Length >= 22 ? BitConverter.ToInt32(body, 18) : 0;
            FileLogger.Log($"[{ProtocolName}] MOVE raw({body.Length}B): {BitConverter.ToString(body)}");
            FileLogger.Log($"[{ProtocolName}] MOVE fields: src=({request.SourceListType},slot{request.SourceSlotIndex},IV=0x{srcIV:X8},stk{srcStack}) dst=({request.DestinationListType},slot{request.DestinationSlotIndex},IV=0x{request.DestinationInstanceValue:X8},stk{dstStack})");

            var (cid, aid) = ResolveOwner(session);
            var petRuntimeMove = PetInventoryMoveCoordinator.Begin(session, request);

            if (!_inventoryStore.TryMoveItem(cid, aid, request, out var result))
            {
                if (result?.FailureReason == InventoryMoveFailureReason.CharmCarryLimit)
                {
                    // 0x04 是“仓库空间不足”，符咒快捷栏上限使用通用移动失败响应。
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0013,
                        MoveItemSpaceAckBuilder.BuildError(0x02, (byte)request.SourceListType, (byte)request.DestinationListType)));
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketType.SERVER_NOTICE_MESSAGE,
                        ServerNoticeMessageBuilder.Build(CharmQuickSlotLimitNoticeMessage)));
                    FileLogger.Log($"[{ProtocolName}] MOVE_ITEMSPACE: CHARM_LIMIT src=({request.SourceListType},{request.SourceSlotIndex}) dst=({request.DestinationListType},{request.DestinationSlotIndex})");
                    return;
                }

                FileLogger.Log($"[{ProtocolName}] MOVE_ITEMSPACE: FAILED src=({request.SourceListType},{request.SourceSlotIndex}) dst=({request.DestinationListType},{request.DestinationSlotIndex})");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0013,
                    MoveItemSpaceAckBuilder.BuildError(0x04, (byte)request.SourceListType, (byte)request.DestinationListType)));
                return;
            }

            if (result.AckError)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0013,
                    MoveItemSpaceAckBuilder.BuildError(0x02, (byte)request.SourceListType, (byte)request.DestinationListType)));
                FileLogger.Log($"[{ProtocolName}] MOVE_ITEMSPACE: ReverseError -> ERROR ACK (撤销反转包, 不卡住)");
                return;
            }

            ApplySubtype0TailMutation(session, result.Subtype0TailMutation);

            FileLogger.Log($"[{ProtocolName}] MOVE_ITEMSPACE: OK src=({result.SourceListType},{result.SourceSlotIndex}) dst=({result.DestinationListType},{result.DestinationSlotIndex}) moveVal={result.MoveValue32}");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0013, MoveItemSpaceAckBuilder.Build(result)));
            await SendMoveSortLockSignals(session, request, result);

            await PetInventoryMoveCoordinator.CompleteAsync(session, result, petRuntimeMove, _refresh);

            // In dungeon, weapon/title moves rely on the normal move ACK only; NOTI2 appearance rebuilds the pet actor.
            if (!PetInventoryMoveCoordinator.HandlesDefaultAppearanceRefresh(result)
                && ShouldSendSubtype0AppearanceUpdate(session, result))
            {
                // 先重载宠物字段再发 subtype0: 宠物ID不变时客户端只做原地更新,
                // 不会重建宠物或重置技能冷却, 因此副本内也可以安全发送。
                _refresh.ReloadSubtype0Tail(session);
                await _refresh.SendNoti2AppearanceUpdate(session);
            }
        }

        private static bool ShouldSendSubtype0AppearanceUpdate(EnhancedClientSession session, InventoryMoveResult result)
        {
            return result != null
                && result.Mutated
                && !ShouldUseDungeonEquipmentOnlyRefresh(session, result.AffectedEquipmentSlot)
                && IsAppearanceEquipmentSlot(result.AffectedEquipmentSlot);
        }

        private static bool ShouldUseDungeonEquipmentOnlyRefresh(EnhancedClientSession session, short slot)
        {
            return session?.Player?.CurrentRun != null
                && (slot == (short)EquipmentType.Weapon
                    || slot == (short)EquipmentType.TitleName);
        }

        private static bool IsAppearanceEquipmentSlot(short slot)
        {
            // 客户端收到移动应答后不会自行更新外观, 这些槽位变动必须跟发 subtype0:
            // 装扮0-10、武器11、称号12、宠物24、名称装饰卡28。
            return (slot >= (short)EquipmentType.HatAvatar && slot <= (short)EquipmentType.TitleName)
                || slot == (short)EquipmentType.Creature
                || slot == (short)EquipmentType.NameTag;
        }

        private static void ApplySubtype0TailMutation(EnhancedClientSession session, Subtype0TailMoveMutation mutation)
        {
            if (session?.Player == null || mutation == null)
                return;

            var tail = session.Player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();

            if (mutation.ForgingChanged)
                tail.Forging = mutation.Forging;

            if (mutation.NameTagChanged)
            {
                tail.NameTagItemId = mutation.NameTagItemId;
                tail.NameTagExpireTime = mutation.NameTagExpireTime;
            }

            if (mutation.EquippedCreatureChanged)
            {
                tail.EquippedCreatureItemId = mutation.EquippedCreatureItemId;
                tail.EquippedCreatureNameBytes = mutation.EquippedCreatureNameBytes ?? Array.Empty<byte>();
                tail.EquippedCreatureAliveState = mutation.EquippedCreatureAliveState;
            }

            session.Player.Subtype0Tail = tail;
        }

        public async Task Handle_ENUM_CMDPACKET_SORT_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 2)
                return;

            var listType = (InventoryListType)body[0];
            byte category = body[1];
            byte condition = body.Length > 2 ? body[2] : (byte)0;
            FileLogger.Log($"[{ProtocolName}] SORT_ITEM raw({body.Length}B): {BitConverter.ToString(body)}  listType={listType} category={category} condition={condition}(ignored)");

            var (cid, aid) = ResolveOwner(session);
            try
            {
                var ok = _inventoryStore.TrySortItems(cid, aid, listType, category);
                FileLogger.Log($"[{ProtocolName}] SORT: TrySortItems({listType}, cat={category})={ok}");
                if (!ok)
                    return;

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0014, SortItemAckBuilder.Build(listType)));
                await _refresh.SendItemListRefresh(session, listType);
                await _refresh.SendEquipmentItemLockListRefresh(session, listType);
                FileLogger.Log($"[{ProtocolName}] SORT: ack + ITEM_LIST sent, done");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] SORT EXCEPTION: {ex}");
                throw;
            }
        }

        public async Task Handle_ENUM_CMDPACKET_TOGGLE_SORT_ITEM_LOCK(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 3)
                return;

            var listType = (InventoryListType)body[0];
            var slotIndex = BitConverter.ToInt16(body, 1);
            var (cid, aid) = ResolveOwner(session);

            if (!_inventoryStore.TryToggleSortItemLock(cid, listType, slotIndex, out var entry))
                return;

            if (entry.State == 0)
            {
                await SendSortItemUnlockAckAndRefresh(session, listType, slotIndex);
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CA, SortItemLockBuilder.BuildLock(entry)));
        }

        public async Task Handle_ENUM_CMDPACKET_UNLOCK_SORT_ITEM_LOCK(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 3)
                return;

            var listType = (InventoryListType)body[0];
            var slotIndex = BitConverter.ToInt16(body, 1);
            var (cid, aid) = ResolveOwner(session);

            if (!_inventoryStore.TryUnlockSortItemLock(cid, listType, slotIndex))
                return;

            await SendSortItemUnlockAckAndRefresh(session, listType, slotIndex);
        }

        private async Task SendSortItemUnlockAckAndRefresh(EnhancedClientSession session, InventoryListType listType, short slotIndex)
        {
            var notiListType = InventoryRefreshSender.MapToSortLockListType(listType);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CB, SortItemLockBuilder.BuildUnlock(notiListType, slotIndex)));
        }

        private async Task SendMoveSortLockSignals(EnhancedClientSession session, InventoryMoveRequest request, InventoryMoveResult result)
        {
            if (result == null || !result.Mutated)
                return;

            var (cid, aid) = ResolveOwner(session);
            await SendSortLockSignalIfSlotLocked(session, cid, aid, request.SourceListType, request.SourceSlotIndex);

            if (request.SourceListType != request.DestinationListType
                || request.SourceSlotIndex != request.DestinationSlotIndex)
                await SendSortLockSignalIfSlotLocked(session, cid, aid, request.DestinationListType, request.DestinationSlotIndex);
        }

        private async Task SendSortLockSignalIfSlotLocked(EnhancedClientSession session, int characterId, int accountId, InventoryListType listType, short slotIndex)
        {
            if (!ShouldSendSortLockSignal(listType, slotIndex))
                return;

            if (!IsRefreshSlotSortLocked(characterId, accountId, listType, slotIndex))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CA, SortItemLockBuilder.BuildLock(new SortItemLockEntry
            {
                ListType = listType,
                SlotIndex = slotIndex,
                State = 1,
            })));
        }

        private static bool ShouldSendSortLockSignal(InventoryListType listType, short slotIndex)
        {
            if (listType == InventoryListType.Equipment)
                return false;

            if (listType == InventoryListType.Main
                && slotIndex >= SqliteInventoryStore.QuickSlotStart
                && slotIndex <= SqliteInventoryStore.QuickSlotEnd)
                return false;

            return true;
        }

        private bool IsRefreshSlotSortLocked(int characterId, int accountId, InventoryListType listType, short slotIndex)
        {
            switch (listType)
            {
                case InventoryListType.Main:
                case InventoryListType.PersonalCargo:
                case InventoryListType.AccountCargo:
                    var common = _inventoryStore.LoadCommonItemForRefresh(characterId, accountId, listType, slotIndex);
                    return IsSortLockedTail(common?.TailData2F);

                case InventoryListType.Avatar:
                    var avatar = _inventoryStore.LoadAvatarItemForRefresh(characterId, slotIndex);
                    return IsSortLockedTail(avatar?.TailData2F);

                case InventoryListType.Pet:
                    var pet = _inventoryStore.LoadPetItemForRefresh(characterId, slotIndex);
                    return IsSortLockedTail(pet?.TailData2F);

                default:
                    return false;
            }
        }

        private static bool IsSortLockedTail(byte[] tailData2F)
        {
            return tailData2F != null
                && tailData2F.Length > 36
                && tailData2F[36] == 1;
        }
    }
}
