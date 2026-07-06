using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
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
            if (!_inventoryStore.TryMoveItem(cid, aid, request, out var result))
            {
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
            await _refresh.SendSortItemLockRefresh(session, request.SourceListType);
            if (InventoryRefreshSender.MapToSortLockListType(request.SourceListType) != InventoryRefreshSender.MapToSortLockListType(request.DestinationListType))
                await _refresh.SendSortItemLockRefresh(session, request.DestinationListType);

            if (result.PetCreatureStateChanged)
            {
                _refresh.ReloadSubtype0Tail(session);
                await _refresh.SendCreatureItemListRefresh(session);
                await _refresh.SendNoti2AppearanceUpdate(session);
                FileLogger.Log($"[{ProtocolName}] pet creature switch: 0x0069 + NOTI2 mode0 sent via upstream subtype0 fields");
            }

            if (result.PetItemStateChanged
                || result.PetItemFullRefresh
                || result.PetCreatureRefreshSlots.Count > 0
                || result.EquipmentRefreshSlots.Count > 0)
            {
                if (result.PetItemFullRefresh)
                    await _refresh.SendItemListRefresh(session, InventoryListType.Pet);
                else if (result.PetCreatureRefreshSlots.Count > 0)
                    await _refresh.SendUpdateItemList(session, InventoryListType.Pet, result.PetCreatureRefreshSlots);

                if (result.EquipmentRefreshSlots.Count > 0)
                    await _refresh.SendUpdateItemList(session, InventoryListType.Equipment, result.EquipmentRefreshSlots);
            }

            if (!result.PetCreatureStateChanged
                && !result.PetItemStateChanged
                && ShouldSendSubtype0AppearanceUpdate(session, result))
                await _refresh.SendNoti2AppearanceUpdate(session);
        }

        private static bool ShouldSendSubtype0AppearanceUpdate(EnhancedClientSession session, InventoryMoveResult result)
        {
            return result != null
                && result.Mutated
                && ShouldSendTownAvatarSubtype0Refresh(session, result.AffectedEquipmentSlot);
        }

        private static bool ShouldSendTownAvatarSubtype0Refresh(EnhancedClientSession session, short equipmentSlot)
        {
            return session?.Player?.CurrentRun == null
                && IsAvatarEquipmentSlot(equipmentSlot);
        }

        private static bool IsAvatarEquipmentSlot(short slot)
        {
            return slot >= (short)EquipmentType.HatAvatar
                && slot <= (short)EquipmentType.WeaponAvatar;
        }

        private static bool ShouldSuppressSelfUserInfoRefresh(EnhancedClientSession session)
        {
            return session?.Player != null;
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
                await _refresh.SendSortItemLockRefresh(session, listType);
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

            if (listType != InventoryListType.Equipment)
            {
                await _refresh.SendItemListRefresh(session, notiListType);
                await _refresh.SendEquipmentItemLockListRefresh(session, notiListType);
            }

            await _refresh.SendSortItemLockRefresh(session, notiListType);
        }
    }
}
