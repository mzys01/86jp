using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class InventoryHandler
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
            if (!_sqliteSelectCharacterDataSource.TryMoveItem(cid, aid, request, out var result))
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

            FileLogger.Log($"[{ProtocolName}] MOVE_ITEMSPACE: OK src=({result.SourceListType},{result.SourceSlotIndex}) dst=({result.DestinationListType},{result.DestinationSlotIndex}) moveVal={result.MoveValue32}");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0013, MoveItemSpaceAckBuilder.Build(result)));
            await SendSortItemLockRefresh(session, request.SourceListType);
            if (MapToSortLockListType(request.SourceListType) != MapToSortLockListType(request.DestinationListType))
                await SendSortItemLockRefresh(session, request.DestinationListType);

            
            if (result.Mutated && (request.SourceListType == InventoryListType.Equipment || request.DestinationListType == InventoryListType.Equipment))
                await SendNoti2AppearanceUpdate(session);
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
                var ok = _sqliteSelectCharacterDataSource.TrySortItems(cid, aid, listType, category);
                FileLogger.Log($"[{ProtocolName}] SORT: TrySortItems({listType}, cat={category})={ok}");
                if (!ok)
                    return;

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0014, SortItemAckBuilder.Build(listType)));
                await SendItemListRefresh(session, listType);
                await SendSortItemLockRefresh(session, listType);
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

            if (!_sqliteSelectCharacterDataSource.TryToggleSortItemLock(cid, aid, listType, slotIndex, out var entry))
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

            if (!_sqliteSelectCharacterDataSource.TryUnlockSortItemLock(cid, aid, listType, slotIndex))
                return;

            await SendSortItemUnlockAckAndRefresh(session, listType, slotIndex);
        }

        private async Task SendSortItemUnlockAckAndRefresh(EnhancedClientSession session, InventoryListType listType, short slotIndex)
        {
            var notiListType = MapToSortLockListType(listType);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CB, SortItemLockBuilder.BuildUnlock(notiListType, slotIndex)));

            if (RequiresMainListRefreshForSortItemUnlock(notiListType))
                await SendItemListRefresh(session, InventoryListType.Main);
            else
                await SendSortItemLockSlotRefresh(session, notiListType, slotIndex);

            await SendSortItemLockRefresh(session, notiListType);
        }

        public async Task Handle_ENUM_CMDPACKET_DELETE_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4)
                return;

            var (cid, aid) = ResolveOwner(session);

            
            if (body.Length >= 15 && body[1] >= 1 && body[1] <= 100)
            {
                var listType = (InventoryListType)body[0];
                var arrayCount = body[1];
                var offset = 2;

                // Entry (12B): opType(u16) + slotIndex(u16) + itemId(i32) + deleteCount(i32)
                for (int i = 0; i < arrayCount && offset + 12 <= body.Length; i++)
                {
                    var opType = BitConverter.ToInt16(body, offset);
                    var slotIndex = BitConverter.ToInt16(body, offset + 2);
                    var itemId = BitConverter.ToInt32(body, offset + 4);
                    var deleteCount = (short)BitConverter.ToInt32(body, offset + 8);
                    offset += 12;

                    if (!_sqliteSelectCharacterDataSource.TryDeleteItem(cid, aid, listType, slotIndex, deleteCount, out var result))
                    {
                        FileLogger.Log($"[{ProtocolName}] DELETE_ITEM(ext): failed at listType={listType} slot={slotIndex} count={deleteCount}");
                        var errAck = new byte[] { 0x00, 0x17, (byte)listType };
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0012, errAck));
                        continue;
                    }

                    result.AppliedCount = deleteCount;
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0012, DeleteItemAckBuilder.Build(result)));
                    FileLogger.Log($"[{ProtocolName}] DELETE_ITEM(ext): slot={slotIndex} item=0x{itemId:X8} applied={deleteCount} remaining={result.RemainingStackCount}");
                }
                return;
            }

            
            if (!TryParseDeleteOrSellRequest(body, out var lt, out var si, out var ic))
                return;

            if (!_sqliteSelectCharacterDataSource.TryDeleteItem(cid, aid, lt, si, ic, out var simpleResult))
            {
                var errAck = new byte[] { 0x00, 0x17, (byte)lt };
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0012, errAck));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0012, DeleteItemAckBuilder.Build(simpleResult)));
        }

        public async Task Handle_ENUM_CMDPACKET_BUY_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4)
                return;

            var itemTemplateId = BitConverter.ToInt32(body, 0);
            var buyCount = body.Length >= 8 ? BitConverter.ToInt32(body, 4) : 1;
            if (buyCount <= 0) buyCount = 1;
            FileLogger.Log($"[{ProtocolName}] BUY_ITEM: itemTemplateId=0x{itemTemplateId:X8} count={buyCount}");

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryBuyItem(cid, aid, itemTemplateId, buyCount, out var result))
            {
                FileLogger.Log($"[{ProtocolName}] BUY_ITEM: FAILED itemTemplateId=0x{itemTemplateId:X8}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0015, BuyItemAckBuilder.BuildError(0x04)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] BUY_ITEM: OK slot={result.SlotIndex} gold={result.UpdatedGold} costId={result.CostItemTemplateId} costNew={result.CostItemNewStackCount}");
            var costItems = result.CostItemTemplateId > 0
                ? new System.Collections.Generic.List<CostItemUpdate> { new CostItemUpdate { ItemTemplateId = result.CostItemTemplateId, NewStackCount = result.CostItemNewStackCount } }
                : null;
            var ackBody = BuyItemAckBuilder.Build(result, costItems);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0015, ackBody));

            
            
            
            if (result.CostItemTemplateId > 0)
            {
                var updBody = TeleportPacketBuilder.BuildItemListUpdate(result.CostItemSlotIndex, result.CostItemTemplateId, result.CostItemNewStackCount);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, updBody));
                FileLogger.Log($"[{ProtocolName}] BUY_ITEM: NOTI 14 cost update slot={result.CostItemSlotIndex} id=0x{result.CostItemTemplateId:X8} newCount={result.CostItemNewStackCount}");
            }

            if (result.ListType == InventoryListType.Pet)
            {
                var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
                var petUpdateBody = BuildPetItemUpdates(snapshot, new HashSet<short> { result.SlotIndex });
                if (petUpdateBody != null)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, petUpdateBody));
                    FileLogger.Log($"[{ProtocolName}] BUY_ITEM: pet ITEM_LIST update sent slot={result.SlotIndex}");
                }
            }
        }

        public async Task Handle_ENUM_CMDPACKET_SELL_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] SELL_ITEM raw body({body?.Length ?? 0}): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseDeleteOrSellRequest(body, out var listType, out var slotIndex, out var sellCount))
                return;

            FileLogger.Log($"[{ProtocolName}] SELL_ITEM: listType={listType}({(byte)listType}) slot={slotIndex} count={sellCount}");

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TrySellItem(cid, aid, listType, slotIndex, sellCount, out var result))
            {
                FileLogger.Log($"[{ProtocolName}] SELL_ITEM: FAILED listType={listType} slot={slotIndex} count={sellCount}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0016, SellItemBuilder.BuildError(0x11)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] SELL_ITEM: OK gold={result.UpdatedGold} applied={result.AppliedCount}");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0016, SellItemBuilder.Build((byte)listType, result.SlotIndex, result.AppliedCount, result.UpdatedGold)));
        }

        public async Task Handle_ENUM_CMDPACKET_ENCHANT_BY_BEAD(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!EnchantByBeadRequest.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0110, EnchantByBeadAckBuilder.BuildError(EnchantByBeadResult.ErrorInvalidBead)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] ENCHANT_BY_BEAD raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} bead=({request.BeadListType},{request.BeadSlotIndex}) target=({request.TargetListType},{request.TargetSlotIndex})");

            var (cid, aid) = ResolveOwner(session);
            var command = request.ToCommand();
            if (!_sqliteSelectCharacterDataSource.TryEnchantByBead(cid, aid, command, out var result))
            {
                var errorCode = result != null ? result.ErrorCode : EnchantByBeadResult.ErrorInvalidBead;
                FileLogger.Log($"[{ProtocolName}] ENCHANT_BY_BEAD: FAILED error=0x{errorCode:X2}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0110, EnchantByBeadAckBuilder.BuildError(errorCode)));
                return;
            }

            // 原生顺序是先发 NOTI 14 刷新目标装备和宝珠，再发 0x0110 成功结果。
            var updateBody = ItemListUpdateBuilder.BuildCommonUpdates(new[] { result.TargetItem, result.BeadItem });
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, updateBody));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0110, EnchantByBeadAckBuilder.BuildSuccess(result)));

            FileLogger.Log($"[{ProtocolName}] ENCHANT_BY_BEAD: OK target=({request.TargetListType},{request.TargetSlotIndex}) enchantCard=0x{result.EnchantCardItemId:X8}");
        }

        public async Task Handle_ENUM_CMDPACKET_UPGRADE_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!ItemUpgradeRequest.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0050,
                    ItemUpgradeAckBuilder.BuildError(ItemUpgradeResult.ErrorInvalidTarget)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} mode={request.Mode} target=({request.TargetSlotIndex},0x{request.TargetItemTemplateId:X8}) materialSlot={request.MaterialSlotIndex} optSlot={request.OptionalTicketSlotIndex} name={request.TargetItemName}");

            var (cid, aid) = ResolveOwner(session);
            var command = request.ToCommand();
            if (!_sqliteSelectCharacterDataSource.TryUpgradeItem(cid, aid, command, out var result))
            {
                var errorCode = result != null ? result.ErrorCode : ItemUpgradeResult.ErrorInvalidTarget;
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: FAILED error={errorCode} mode={request.Mode} targetSlot={request.TargetSlotIndex} materialSlot={request.MaterialSlotIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0050, ItemUpgradeAckBuilder.BuildError(errorCode)));
                return;
            }

            var updates = new List<CommonInventoryItem>();
            if (result.TargetItem != null)
                updates.Add(result.TargetItem);
            if (result.ExtraItems != null)
                updates.AddRange(result.ExtraItems);
            // 0x0050 负责结果框；目标装备和金币仍通过 NOTI 14 实时刷新。

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0050, ItemUpgradeAckBuilder.BuildSuccess(result)));

            if (updates.Count > 0)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, ItemListUpdateBuilder.BuildCommonUpdates(updates)));

            if (result.GoldCost > 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                    TeleportPacketBuilder.BuildItemListUpdate(0, 0, result.UpdatedGold)));
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: gold refresh queued gold={result.UpdatedGold}");
            }

            await SendSortItemLockRefresh(session, InventoryListType.Main);

            if (result.NoticeRequired)
                await BroadcastItemUpgradeNotice(session, result);

            FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: OK scene={result.Scene} mode={result.Mode} targetSlot={result.TargetSlotIndex} level={result.OldLevel}->{result.NewLevel} success={result.UpgradeSucceeded} resultCode={result.ResultCode} rate={result.FinalSuccessWeight} gold={result.UpdatedGold}");
        }

        private async Task BroadcastItemUpgradeNotice(EnhancedClientSession session, ItemUpgradeResult result)
        {
            if (_broadcastGamePacket == null || result == null)
                return;

            try
            {
                var userUniqueId = session?.Player?.UserId ?? 0;
                if (userUniqueId == 0 && session?.Player?.CharacterId > 0)
                    userUniqueId = (ushort)session.Player.CharacterId;

                var body = ItemUpgradeNoticeBuilder.Build(result, userUniqueId);
                await _broadcastGamePacket(GamePacketEnvelopeBuilder.Build(0x00, 0x0056, body));
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: notice broadcast type=0x0056 uniqueId={userUniqueId} item=0x{result.TargetItemTemplateId:X8} level={result.NewLevel} mode={result.Mode}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: notice broadcast failed: {ex.Message}");
            }
        }

        public async Task Handle_EQUIPMENT_SOCKET_OPEN(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] EQUIP_SOCKET_OPEN 0x031D raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseSocketOpenBody(body, out var targetSlot, out var targetItemId, out var materialSlot))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x031D, new byte[] { 0x00 }));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryOpenEquipmentSocket(cid, aid, targetSlot, targetItemId, materialSlot, out var result))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x031D, new byte[] { 0x00, 0x04 }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x031D, BuildSocketOpenAck(targetSlot, targetItemId, materialSlot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000E,
                ItemListUpdateBuilder.BuildCommonUpdates(new[] { result.TargetItem })));

            if (result.MaterialConsumed && result.MaterialItem != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x000E,
                    ItemListUpdateBuilder.BuildCompactCommonUpdates(new[] { result.MaterialItem })));

            await SendSortItemLockRefresh(session, InventoryListType.Main);
            if (result.MaterialConsumed && result.MaterialItem != null)
                FileLogger.Log($"[{ProtocolName}] EQUIP_SOCKET_OPEN: OK targetSlot={targetSlot} item=0x{targetItemId:X8} materialSlot={materialSlot} left={result.MaterialItem.RemainingStackCount}");
            else
                FileLogger.Log($"[{ProtocolName}] EQUIP_SOCKET_OPEN: OK targetSlot={targetSlot} item=0x{targetItemId:X8} already-open repaired without consuming material");
        }

        public async Task Handle_EQUIPMENT_EMBLEM_ATTACH(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] EQUIP_EMBLEM_ATTACH 0x031C raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseEmblemAttachBody(body, out var targetSlot, out var targetItemId, out var emblems))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x031C, new byte[] { 0x00 }));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TrySetEquipmentEmblems(cid, aid, targetSlot, targetItemId, emblems, out var result))
            {
                if (await TryHandleAvatarEmblemAttach(session, 0x031C, targetSlot, targetItemId, emblems, cid, aid))
                    return;

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x031C, new byte[] { 0x00, 0x04 }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x031C, BuildEmblemAttachAck(targetSlot, targetItemId, emblems.Count)));
            if (!result.TargetEquipped && result.TargetItem != null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x000E,
                    ItemListUpdateBuilder.BuildCommonUpdates(new[] { result.TargetItem })));
            }
            await SendSortItemLockRefresh(session, InventoryListType.Main);
            FileLogger.Log($"[{ProtocolName}] EQUIP_EMBLEM_ATTACH: OK targetSlot={targetSlot} item=0x{targetItemId:X8} emblems={emblems.Count}");
        }

        public async Task Handle_AVATAR_SOCKET_OPEN(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] AVATAR_SOCKET_OPEN 0x00CE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseSocketOpenBody(body, out var targetSlot, out var targetItemId, out var materialSlot))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CE, new byte[] { 0x00 }));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryOpenAvatarSocket(cid, aid, targetSlot, targetItemId, materialSlot, out var result))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CE, new byte[] { 0x00, 0x04 }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CE, BuildSocketOpenAck(targetSlot, targetItemId, materialSlot)));

            if (result.MaterialConsumed && result.MaterialItem != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x000E,
                    ItemListUpdateBuilder.BuildCompactCommonUpdates(new[] { result.MaterialItem })));

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000E,
                ItemListUpdateBuilder.BuildAvatarUpdates(new[] { result.TargetItem })));
            await SendSortItemLockRefresh(session, InventoryListType.Avatar);

            if (result.MaterialConsumed && result.MaterialItem != null)
                FileLogger.Log($"[{ProtocolName}] AVATAR_SOCKET_OPEN: OK targetSlot={targetSlot} item=0x{targetItemId:X8} materialSlot={materialSlot} left={result.MaterialItem.RemainingStackCount}");
            else
                FileLogger.Log($"[{ProtocolName}] AVATAR_SOCKET_OPEN: OK targetSlot={targetSlot} item=0x{targetItemId:X8} already-open repaired without consuming material");
        }

        public async Task Handle_AVATAR_EMBLEM_ATTACH(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] AVATAR_EMBLEM_ATTACH 0x{header.type:X4} raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseAvatarEmblemAttachBody(body, out var targetSlot, out var targetItemId, out var emblems))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!await TryHandleAvatarEmblemAttach(session, header.type, targetSlot, targetItemId, emblems, cid, aid))
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00, 0x04 }));
        }

        private async Task<bool> TryHandleAvatarEmblemAttach(EnhancedClientSession session, ushort ackType, short targetSlot, int targetItemId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, int cid, int aid)
        {
            if (!_sqliteSelectCharacterDataSource.TrySetAvatarEmblems(cid, aid, targetSlot, targetItemId, emblems, out var result))
                return false;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, ackType, BuildEmblemAttachAck(targetSlot, targetItemId, emblems.Count)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000E,
                ItemListUpdateBuilder.BuildAvatarUpdates(new[] { result.TargetItem })));

            await SendSortItemLockRefresh(session, InventoryListType.Main);
            await SendSortItemLockRefresh(session, InventoryListType.Avatar);
            FileLogger.Log($"[{ProtocolName}] AVATAR_EMBLEM_ATTACH: OK targetSlot={targetSlot} item=0x{targetItemId:X8} emblems={emblems.Count} ack=0x{ackType:X4}");
            return true;
        }

        public async Task Handle_ENUM_CMDPACKET_USE_STACKABLE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            
            if (body == null || body.Length < 7)
                return;

            var slotIndex = BitConverter.ToInt16(body, 0);
            var listType = (InventoryListType)body[2];
            var instanceValue = BitConverter.ToInt32(body, 3);
            var itemCode = body.Length >= 11 ? BitConverter.ToInt32(body, 7) : 0;

            var (cid, aid) = ResolveOwner(session);

            if (!_sqliteSelectCharacterDataSource.TryDeleteItem(cid, aid, listType, slotIndex, 1, out var result))
            {
                FileLogger.Log($"[{ProtocolName}] USE_STACKABLE: failed to consume item 0x{itemCode:X8} at listType={listType} slot={slotIndex}");
                var errBody = UseStackableAckBuilder.BuildError((byte)listType, itemCode, instanceValue);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x002C, errBody));
                return;
            }

            
            var ackBody = UseStackableAckBuilder.BuildSuccess(slotIndex, (byte)listType, instanceValue, itemCode);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x002C, ackBody));

            FileLogger.Log($"[{ProtocolName}] USE_STACKABLE: consumed 1x item 0x{itemCode:X8} from slot {slotIndex}, remaining={result.RemainingStackCount}");
        }

        public async Task Handle_OPEN_AVATAR_PACKAGE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            var parsedAvatar = AvatarPackageOpenRequest.TryParse(body, out var request);
            if (!parsedAvatar)
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE: parse failed");
            }
            else
            {
                var (cid, aid) = ResolveOwner(session);
                if (_sqliteSelectCharacterDataSource.TryOpenAvatarPackage(cid, aid, request, out var result))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0207, AvatarPackageAckBuilder.BuildSuccess(result.SlotIndex)));
                    if (result.GrantedItems.Count > 0)
                    {
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                            SelectablePackageAckBuilder.BuildSuccess(result.SlotIndex, result.GrantedItems)));
                    }

                    if (result.SourceRemainingStackCount <= 0)
                        await SendConsumedSourceItemUpdate(session, result.SlotIndex, result.PackageItemTemplateId);

                    var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
                    var mainUpdateBody = BuildGrantedMainItemUpdates(snapshot, result.GrantedItems, result.SlotIndex, result.PackageItemTemplateId, result.SourceRemainingStackCount > 0);
                    if (mainUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, mainUpdateBody));

                    var petUpdateBody = BuildGrantedPetItemUpdates(snapshot, result.GrantedItems);
                    if (petUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, petUpdateBody));

                    var avatarUpdateBody = BuildGrantedAvatarItemUpdates(snapshot, result.GrantedItems);
                    if (avatarUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, avatarUpdateBody));

                    FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE: OK slot={result.SlotIndex} item=0x{result.PackageItemTemplateId:X8} avatar={result.AddedAvatarCount} main={result.AddedMainItemCount} pet={result.AddedPetCount}");
                    return;
                }

                FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE: avatar path failed slot={request.SlotIndex} choices={request.Choices.Count}, trying general package 0x0207");
            }

            if (await TryHandleOpenPackage0207(session, header, body))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0207, new byte[] { 0x00 }));
        }

        public async Task Handle_OPEN_SELECTABLE_PACKAGE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            var parsedSelectable = SelectablePackageOpenRequest.TryParse(body, out var request);
            if (!parsedSelectable)
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE: parse failed");
            }
            else
            {
                var (cid, aid) = ResolveOwner(session);
                if (_sqliteSelectCharacterDataSource.TryOpenSelectablePackage(cid, aid, request, out var result))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0, SelectablePackageAckBuilder.BuildSuccess(result.SlotIndex, result.GrantedItems)));

                    if (result.SourceRemainingStackCount <= 0)
                        await SendConsumedSourceItemUpdate(session, result.SlotIndex, result.PackageItemTemplateId);

                    var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
                    var mainUpdateBody = BuildGrantedMainItemUpdates(snapshot, result.GrantedItems, result.SlotIndex, result.PackageItemTemplateId, result.SourceRemainingStackCount > 0);
                    if (mainUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, mainUpdateBody));

                    var petUpdateBody = BuildGrantedPetItemUpdates(snapshot, result.GrantedItems);
                    if (petUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, petUpdateBody));

                    var avatarUpdateBody = BuildGrantedAvatarItemUpdates(snapshot, result.GrantedItems);
                    if (avatarUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, avatarUpdateBody));

                    FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE: OK slot={result.SlotIndex} item=0x{result.PackageItemTemplateId:X8} reward=0x{result.RewardItemTemplateId:X8} main={result.AddedMainItemCount} avatar={result.AddedAvatarCount} pet={result.AddedPetCount} ackItems={result.GrantedItems.Count}");
                    return;
                }

                FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE: selectable path failed slot={request.SlotIndex} selected=0x{request.SelectedItemTemplateId:X8}, trying general booster");
            }

            if (await TryHandleBoosterOpen(session, header, body))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0, SelectablePackageAckBuilder.BuildError()));
        }

        public async Task Handle_USE_BOOSTER_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!await TryHandleBoosterOpen(session, header, body))
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
        }

        public async Task Handle_OPEN_MAGIC_BOX(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var elapsed = Stopwatch.StartNew();
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!MagicBoxOpenRequest.TryParse(body, out var request) || request.ListType != InventoryListType.Main)
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX: parse/list failed");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            var materialSlotIndex = request.MaterialSlotIndex >= 0
                ? (short?)request.MaterialSlotIndex
                : null;
            var expectedMaterialItemTemplateId = request.MaterialItemTemplateId > 0
                ? request.MaterialItemTemplateId
                : 0;

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryUseBoosterItem(
                    cid,
                    aid,
                    new BoosterUseRequest
                    {
                        SlotIndex = request.SlotIndex,
                        SelectedItemTemplateIds = Array.Empty<int>(),
                        ExpectedItemTemplateId = request.ItemTemplateId,
                        MaterialSlotIndex = materialSlotIndex,
                        ExpectedMaterialItemTemplateId = expectedMaterialItemTemplateId,
                    },
                    out var result))
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX: failed cid={cid} aid={aid} slot={request.SlotIndex} item=0x{request.ItemTemplateId:X8} material=0x{request.MaterialItemTemplateId:X8}@{request.MaterialSlotIndex} requested={request.RequestedCount} elapsed={elapsed.ElapsedMilliseconds}ms");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            await SendBoosterUseResult(session, header.type, result);
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} requested={request.RequestedCount} applied={result.ConsumedSourceCount} remaining={result.SourceRemainingStackCount} material=0x{result.ConsumedMaterialItemTemplateId:X8}x{result.ConsumedMaterialCount}@{result.ConsumedMaterialSlotIndex} materialRemaining={result.ConsumedMaterialRemainingStackCount} rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))} elapsed={elapsed.ElapsedMilliseconds}ms");
        }

        public async Task Handle_OPEN_MAGIC_BOX_SINGLE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var elapsed = Stopwatch.StartNew();
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!MagicBoxOpenRequest.TryParseSingle(body, out var request) || request.ListType != InventoryListType.Main)
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE: parse/list failed");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            var materialSlotIndex = request.MaterialSlotIndex >= 0
                ? (short?)request.MaterialSlotIndex
                : null;
            var expectedMaterialItemTemplateId = request.MaterialItemTemplateId > 0
                ? request.MaterialItemTemplateId
                : 0;

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryUseBoosterItem(
                    cid,
                    aid,
                    new BoosterUseRequest
                    {
                        SlotIndex = request.SlotIndex,
                        SelectedItemTemplateIds = Array.Empty<int>(),
                        ExpectedItemTemplateId = request.ItemTemplateId,
                        MaterialSlotIndex = materialSlotIndex,
                        ExpectedMaterialItemTemplateId = expectedMaterialItemTemplateId,
                    },
                    out var result))
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE: failed cid={cid} aid={aid} slot={request.SlotIndex} materialSlot={(materialSlotIndex.HasValue ? materialSlotIndex.Value.ToString() : "auto")} requested={request.RequestedCount} elapsed={elapsed.ElapsedMilliseconds}ms");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            await SendBoosterUseResult(session, header.type, result);
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} requested={request.RequestedCount} applied={result.ConsumedSourceCount} remaining={result.SourceRemainingStackCount} material=0x{result.ConsumedMaterialItemTemplateId:X8}x{result.ConsumedMaterialCount}@{result.ConsumedMaterialSlotIndex} materialRemaining={result.ConsumedMaterialRemainingStackCount} rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))} elapsed={elapsed.ElapsedMilliseconds}ms");
        }

        private async Task<bool> TryHandleBoosterOpen(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var elapsed = Stopwatch.StartNew();
            short? slotIndex = body != null && body.Length >= 2
                ? BitConverter.ToInt16(body, 0)
                : (short?)null;
            var selectedItemTemplateIds = ParseBoosterSelectionItemIds(body);
            var selectedText = selectedItemTemplateIds.Count == 0
                ? "none"
                : string.Join(",", selectedItemTemplateIds.Select(id => $"0x{id:X8}"));
            FileLogger.Log($"[{ProtocolName}] USE_BOOSTER raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} slot={(slotIndex.HasValue ? slotIndex.Value.ToString() : "auto")} selected={selectedText}");

            if (slotIndex == 0 && header.type == 0x0218)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, CommonPacketBodyBuilder.BuildSuccessAck()));
                FileLogger.Log($"[{ProtocolName}] USE_BOOSTER: confirm ack type=0x{header.type:X4}");
                return true;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryUseBoosterItem(cid, aid, new BoosterUseRequest
                {
                    SlotIndex = slotIndex,
                    SelectedItemTemplateIds = selectedItemTemplateIds,
                }, out var result))
            {
                FileLogger.Log($"[{ProtocolName}] USE_BOOSTER: failed cid={cid} aid={aid} slot={(slotIndex.HasValue ? slotIndex.Value.ToString() : "auto")} elapsed={elapsed.ElapsedMilliseconds}ms");
                return false;
            }

            await SendBoosterUseResult(session, header.type, result);
            FileLogger.Log($"[{ProtocolName}] USE_BOOSTER: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} remaining={result.SourceRemainingStackCount}, rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))}, elapsed={elapsed.ElapsedMilliseconds}ms");
            return true;
        }

        private async Task<bool> TryHandleOpenPackage0207(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 3)
                return false;

            var slotIndex = BitConverter.ToInt16(body, 0);
            var selectedItemTemplateIds = Parse0207ItemIds(body);
            FileLogger.Log($"[{ProtocolName}] OPEN_PACKAGE_0207 raw({body.Length}B): {BitConverter.ToString(body)} slot={slotIndex} selected={string.Join(",", selectedItemTemplateIds.Select(id => $"0x{id:X8}"))}");

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryOpenPackage0207(cid, aid, slotIndex, selectedItemTemplateIds, out var result))
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_PACKAGE_0207: failed slot={slotIndex}");
                return false;
            }

            await SendBoosterUseResult(session, header.type, result);
            FileLogger.Log($"[{ProtocolName}] OPEN_PACKAGE_0207: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} rewards={result.Rewards.Count}");
            return true;
        }

        private async Task SendBoosterUseResult(EnhancedClientSession session, ushort responseType, BoosterUseResult result)
        {
            var grantedItems = ToPackageGrantedItems(result);

            if (responseType == 0x00A0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                    SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
            }
            else if (responseType == 0x0207)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0207,
                    AvatarPackageAckBuilder.BuildSuccess(result.SourceSlotIndex)));
                if (grantedItems.Count > 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                        SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
                }
            }
            else if (!ShouldSendSourceAckForBoosterResponse(responseType))
            {
                if (grantedItems.Count > 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                        SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
                }
            }
            else
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, responseType, CommonPacketBodyBuilder.BuildSuccessAck()));
                if (grantedItems.Count > 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                        SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
                }
            }

            var (cid, aid) = ResolveOwner(session);
            if (result.SourceRemainingStackCount <= 0)
                await SendConsumedSourceItemUpdate(session, result.SourceSlotIndex, result.SourceItemTemplateId);

            var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
            var mainUpdateBody = BuildBoosterMainItemUpdates(snapshot, result, result.SourceRemainingStackCount > 0);
            if (mainUpdateBody != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, mainUpdateBody));

            var petUpdateBody = BuildBoosterPetItemUpdates(snapshot, result);
            if (petUpdateBody != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, petUpdateBody));

            var avatarUpdateBody = BuildBoosterAvatarItemUpdates(snapshot, result);
            if (avatarUpdateBody != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, avatarUpdateBody));
        }

        internal static bool ShouldSendSourceAckForBoosterResponse(ushort responseType)
        {
            return responseType != 0x00D0 && responseType != 0x03F3;
        }

        private async Task SendConsumedSourceItemUpdate(EnhancedClientSession session, short sourceSlotIndex, int sourceItemTemplateId)
        {
            var body = ItemListUpdateBuilder.BuildCommonUpdates(new[]
            {
                CreateConsumedSourceItem(sourceSlotIndex, sourceItemTemplateId)
            });
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, body));
        }

        private static byte[] BuildBoosterMainItemUpdates(CharacterItemListSnapshot snapshot, BoosterUseResult result, bool includeSourceUpdate)
        {
            if (snapshot == null || result == null)
                return null;

            var slots = new HashSet<short>();
            foreach (var reward in result.Rewards)
            {
                if (reward.ListType == InventoryListType.Main)
                    slots.Add(reward.SlotIndex);
            }

            if (includeSourceUpdate)
                slots.Add(result.SourceSlotIndex);
            if (result.ConsumedMaterialItemTemplateId > 0)
                slots.Add(result.ConsumedMaterialSlotIndex);

            var updates = new List<CommonInventoryItem>();
            foreach (var slot in slots)
            {
                var item = snapshot.MainItems.FirstOrDefault(x => x.SlotIndex == slot);
                if (item != null)
                {
                    updates.Add(item);
                    continue;
                }

                if (slot == result.SourceSlotIndex)
                    updates.Add(CreateConsumedSourceItem(result));
                else if (slot == result.ConsumedMaterialSlotIndex && result.ConsumedMaterialItemTemplateId > 0)
                    updates.Add(CreateConsumedSourceItem(result.ConsumedMaterialSlotIndex, result.ConsumedMaterialItemTemplateId));
            }

            if (updates.Count == 0)
                return null;

            return ItemListUpdateBuilder.BuildCommonUpdates(updates);
        }

        private static byte[] BuildBoosterPetItemUpdates(CharacterItemListSnapshot snapshot, BoosterUseResult result)
        {
            if (snapshot == null || result == null)
                return null;

            return BuildPetItemUpdates(snapshot, CollectBoosterRewardSlots(result.Rewards, InventoryListType.Pet));
        }

        private static byte[] BuildBoosterAvatarItemUpdates(CharacterItemListSnapshot snapshot, BoosterUseResult result)
        {
            if (snapshot == null || result == null)
                return null;

            return BuildAvatarItemUpdates(snapshot, CollectBoosterRewardSlots(result.Rewards, InventoryListType.Avatar));
        }

        private static byte[] BuildGrantedMainItemUpdates(
            CharacterItemListSnapshot snapshot,
            IReadOnlyList<PackageGrantedItem> grantedItems,
            short sourceSlotIndex,
            int sourceItemTemplateId,
            bool includeSourceUpdate)
        {
            if (snapshot == null || grantedItems == null)
                return null;

            var slots = new HashSet<short>();
            if (includeSourceUpdate)
                slots.Add(sourceSlotIndex);
            foreach (var reward in grantedItems)
            {
                if (reward.ListType == InventoryListType.Main)
                    slots.Add(reward.SlotIndex);
            }

            var updates = new List<CommonInventoryItem>();
            foreach (var slot in slots)
            {
                var item = snapshot.MainItems.FirstOrDefault(x => x.SlotIndex == slot);
                if (item != null)
                {
                    updates.Add(item);
                    continue;
                }

                if (slot == sourceSlotIndex)
                    updates.Add(CreateConsumedSourceItem(sourceSlotIndex, sourceItemTemplateId));
            }

            if (updates.Count == 0)
                return null;

            return ItemListUpdateBuilder.BuildCommonUpdates(updates);
        }

        private static byte[] BuildGrantedPetItemUpdates(CharacterItemListSnapshot snapshot, IReadOnlyList<PackageGrantedItem> grantedItems)
        {
            if (snapshot == null || grantedItems == null)
                return null;

            return BuildPetItemUpdates(snapshot, CollectGrantedItemSlots(grantedItems, InventoryListType.Pet));
        }

        private static byte[] BuildGrantedAvatarItemUpdates(CharacterItemListSnapshot snapshot, IReadOnlyList<PackageGrantedItem> grantedItems)
        {
            if (snapshot == null || grantedItems == null)
                return null;

            return BuildAvatarItemUpdates(snapshot, CollectGrantedItemSlots(grantedItems, InventoryListType.Avatar));
        }

        private static HashSet<short> CollectBoosterRewardSlots(IEnumerable<BoosterRewardResult> rewards, InventoryListType listType)
        {
            var slots = new HashSet<short>();
            if (rewards == null)
                return slots;

            foreach (var reward in rewards)
            {
                if (reward.ListType == listType)
                    slots.Add(reward.SlotIndex);
            }

            return slots;
        }

        private static HashSet<short> CollectGrantedItemSlots(IEnumerable<PackageGrantedItem> grantedItems, InventoryListType listType)
        {
            var slots = new HashSet<short>();
            if (grantedItems == null)
                return slots;

            foreach (var item in grantedItems)
            {
                if (item.ListType == listType)
                    slots.Add(item.SlotIndex);
            }

            return slots;
        }

        private static byte[] BuildPetItemUpdates(CharacterItemListSnapshot snapshot, HashSet<short> slots)
        {
            if (snapshot == null || slots == null || slots.Count == 0)
                return null;

            var updates = new List<PetInventoryItem>();
            foreach (var slot in slots)
            {
                var item = snapshot.PetItems.FirstOrDefault(x => x.SlotIndex == slot);
                if (item != null)
                    updates.Add(item);
            }

            if (updates.Count == 0)
                return null;

            return ItemListUpdateBuilder.BuildPetUpdates(updates);
        }

        private static byte[] BuildAvatarItemUpdates(CharacterItemListSnapshot snapshot, HashSet<short> slots)
        {
            if (snapshot == null || slots == null || slots.Count == 0)
                return null;

            var updates = new List<AvatarInventoryItem>();
            foreach (var slot in slots)
            {
                var item = snapshot.AvatarItems.FirstOrDefault(x => x.SlotIndex == slot);
                if (item != null)
                    updates.Add(item);
            }

            if (updates.Count == 0)
                return null;

            return ItemListUpdateBuilder.BuildAvatarUpdates(updates);
        }

        private static CommonInventoryItem CreateConsumedSourceItem(BoosterUseResult result)
        {
            return CreateConsumedSourceItem(result.SourceSlotIndex, result.SourceItemTemplateId);
        }

        private static CommonInventoryItem CreateConsumedSourceItem(short slotIndex, int itemTemplateId)
        {
            return new CommonInventoryItem
            {
                SlotIndex = slotIndex,
                ItemTemplateId = itemTemplateId,
                CountOrInstanceValue = 0,
            };
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

        private static IReadOnlyList<int> ParseBoosterSelectionItemIds(byte[] body)
        {
            var selected = new List<int>();
            if (body == null || body.Length < 6)
                return selected;

            AddAlignedInt32Candidates(body, 4, 4, selected);
            if (body.Length >= 3)
                AddRecordCandidates(body, 3, body[2], 5, selected);
            AddAlignedInt32Candidates(body, 2, 4, selected);

            return selected;
        }

        private static IReadOnlyList<int> Parse0207ItemIds(byte[] body)
        {
            var selected = new List<int>();
            if (body == null || body.Length < 3)
                return selected;

            var itemCount = body[2];
            for (var i = 0; i < itemCount; i++)
            {
                var offset = 3 + i * 5;
                if (offset + 4 > body.Length)
                    break;

                AddItemCandidate(BitConverter.ToInt32(body, offset), selected);
            }

            return selected;
        }

        private static void AddAlignedInt32Candidates(byte[] body, int startOffset, int stride, List<int> selected)
        {
            for (var offset = startOffset; offset + 4 <= body.Length; offset += stride)
                AddItemCandidate(BitConverter.ToInt32(body, offset), selected);
        }

        private static void AddRecordCandidates(byte[] body, int startOffset, int count, int recordSize, List<int> selected)
        {
            for (var i = 0; i < count; i++)
            {
                var offset = startOffset + i * recordSize;
                if (offset + 4 > body.Length)
                    break;

                AddItemCandidate(BitConverter.ToInt32(body, offset), selected);
            }
        }

        private static void AddItemCandidate(int itemTemplateId, List<int> selected)
        {
            if (itemTemplateId >= 1000 && !selected.Contains(itemTemplateId))
                selected.Add(itemTemplateId);
        }

        private static bool TryParseSocketOpenBody(byte[] body, out short targetSlot, out int targetItemId, out short materialSlot)
        {
            targetSlot = 0;
            targetItemId = 0;
            materialSlot = 0;
            if (body == null || body.Length < 8)
                return false;

            targetSlot = BitConverter.ToInt16(body, 0);
            targetItemId = BitConverter.ToInt32(body, 2);
            materialSlot = BitConverter.ToInt16(body, 6);
            return true;
        }

        private static bool TryParseEmblemAttachBody(byte[] body, out short targetSlot, out int targetItemId, out List<EquipmentEmblemApplyRequest> emblems)
        {
            targetSlot = 0;
            targetItemId = 0;
            emblems = null;
            if (body == null || body.Length < 7)
                return false;

            targetSlot = BitConverter.ToInt16(body, 0);
            targetItemId = BitConverter.ToInt32(body, 2);
            var count = body[6];
            var offset = 7;
            emblems = new List<EquipmentEmblemApplyRequest>();
            for (var index = 0; index < count; index++)
            {
                if (offset + 7 > body.Length)
                    return false;

                emblems.Add(new EquipmentEmblemApplyRequest
                {
                    EmblemSlot = BitConverter.ToInt16(body, offset),
                    EmblemItemTemplateId = BitConverter.ToInt32(body, offset + 2),
                    SocketIndex = body[offset + 6],
                });
                offset += 7;
            }
            return true;
        }

        private static bool TryParseAvatarEmblemAttachBody(byte[] body, out short targetSlot, out int targetItemId, out List<EquipmentEmblemApplyRequest> emblems)
        {
            targetSlot = 0;
            targetItemId = 0;
            emblems = null;
            if (body == null)
                return false;

            if (body.Length >= 8 && body[0] == (byte)InventoryListType.Avatar)
                return TryParseEmblemAttachBodyAt(body, 1, out targetSlot, out targetItemId, out emblems);

            return TryParseEmblemAttachBody(body, out targetSlot, out targetItemId, out emblems);
        }

        private static bool TryParseEmblemAttachBodyAt(byte[] body, int startOffset, out short targetSlot, out int targetItemId, out List<EquipmentEmblemApplyRequest> emblems)
        {
            targetSlot = 0;
            targetItemId = 0;
            emblems = null;
            if (body == null || startOffset < 0 || body.Length < startOffset + 7)
                return false;

            targetSlot = BitConverter.ToInt16(body, startOffset);
            targetItemId = BitConverter.ToInt32(body, startOffset + 2);
            var count = body[startOffset + 6];
            var offset = startOffset + 7;
            emblems = new List<EquipmentEmblemApplyRequest>();
            for (var index = 0; index < count; index++)
            {
                if (offset + 7 > body.Length)
                    return false;

                emblems.Add(new EquipmentEmblemApplyRequest
                {
                    EmblemSlot = BitConverter.ToInt16(body, offset),
                    EmblemItemTemplateId = BitConverter.ToInt32(body, offset + 2),
                    SocketIndex = body[offset + 6],
                });
                offset += 7;
            }
            return true;
        }

        private static byte[] BuildSocketOpenAck(short targetSlot, int targetItemId, short materialSlot)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(targetSlot);
            writer.WriteInt32(targetItemId);
            writer.WriteInt16(materialSlot);
            return writer.ToArray();
        }

        private static byte[] BuildEmblemAttachAck(short targetSlot, int targetItemId, int emblemCount)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(targetSlot);
            writer.WriteInt32(targetItemId);
            writer.WriteByte((byte)Math.Max(0, Math.Min(255, emblemCount)));
            return writer.ToArray();
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

        public async Task SendSortItemLockSlotRefresh(EnhancedClientSession session, InventoryListType listType, short slotIndex)
        {
            if (!IsCommonItemUpdateListType(listType))
                return;

            var (cid, aid) = ResolveOwner(session);
            var item = _sqliteSelectCharacterDataSource.LoadCommonItemForRefresh(cid, aid, listType, slotIndex);
            if (item == null)
                item = new CommonInventoryItem { SlotIndex = slotIndex };

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000E,
                ItemListUpdateBuilder.BuildCommonUpdates(new[] { item })));
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

        private static bool IsCommonItemUpdateListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.AccountCargo
                || listType == InventoryListType.Pet;
        }

        private static bool RequiresMainListRefreshForSortItemUnlock(InventoryListType listType)
        {
            return listType == InventoryListType.PersonalCargo;
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

        public async Task Handle_SET_CLONE_TITLE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cloneTitle = (body != null && body.Length >= 4) ? BitConverter.ToInt32(body, 0) : 0;
            var ack = new byte[5];
            ack[0] = 0x01;
            BitConverter.GetBytes(cloneTitle).CopyTo(ack, 1);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0239, ack));
            var (cid, aid) = ResolveOwner(session);
            var noti2 = AppearanceService.UpdateAndBroadcast(
                session.Player, _sqliteSelectCharacterDataSource, _characterRepository, cid, aid);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, noti2));
        }

        public async Task Handle_TITLE_BOOK(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 20) return;
            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteInt32(BitConverter.ToInt32(body, 0));
            w.WriteInt32(BitConverter.ToInt32(body, 4));
            w.WriteInt32(BitConverter.ToInt32(body, 12));
            w.WriteInt32(BitConverter.ToInt32(body, 16));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, w.ToArray()));
        }


        public async Task Handle_COMPOUND_AVATAR(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {

            if (body == null || body.Length < 22)
            {
                var shortErr = new GamePacketWriter();
                shortErr.WriteByte(0x00);          
                shortErr.WriteByte(0x16);         
                shortErr.WriteByte(0x00);          
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0063, shortErr.ToArray()));
                return;
            }

            short consumeSlot = BitConverter.ToInt16(body, 0);
            short slot1 = BitConverter.ToInt16(body, 2);
            short slot2 = BitConverter.ToInt16(body, 8);
            int reqItemId = BitConverter.ToInt32(body, 14);

            var (cid, aid) = ResolveOwner(session);
            var job = _characterRepository.GetById(cid)?.Job ?? 0;
            byte newOption = 0;

            if (!_sqliteSelectCharacterDataSource.TryCompoundAvatar(cid, aid, slot1, slot2, consumeSlot,
                    (old1, old2, materialId) =>
                    {
                        var prob = CompoundAvatarProbabilityService.Resolve(job, old1, old2, materialId, reqItemId);
                        return prob.Success ? prob.NewItemIds : new List<int> { reqItemId };
                    },
                    newOption,
                    out List<int> newSlots, out int oldItemId1, out int oldItemId2, out List<int> newItemIds,
                    out int consumedItemTemplateId, out int consumedItemRemainingCount))
            {
                var err = new GamePacketWriter();
                err.WriteByte(0x00);  
                err.WriteByte(0x16);
                err.WriteByte(0x00);  
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0063, err.ToArray()));
                return;
            }

            var w = new GamePacketWriter();
            w.WriteByte(0x01);           
            w.WriteByte(0x03);            
            w.WriteByte(0x01);           
            w.WriteInt16(slot1);           
            w.WriteInt32(1);              
            w.WriteByte(0x01);
            w.WriteInt16(slot2);
            w.WriteInt32(1);
            w.WriteByte(0x00);            
            w.WriteInt16(consumeSlot);     
            w.WriteInt32(1);                
            for (int i = 0; i < 2; i++)
            {
                bool hasItem = i < newItemIds.Count;
                w.WriteInt16(hasItem ? (short)newSlots[i] : (short)-1); 
                w.WriteInt32(hasItem ? newItemIds[i] : 0);  
                w.WriteInt32(0);                 
                w.WriteInt16(newOption);          
                w.WriteInt32(30);                 
                w.WriteZeroBytes(30);            
                w.WriteInt32(4);                  
                w.WriteZeroBytes(4);               
            }

            var respBody = w.ToArray();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0063, respBody));
        }

  
        public async Task Handle_COMPOUND_AVATAR_SET(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 72)
                return;

            short consumeStackableSlot = body[13];
            int requestedItemId = BitConverter.ToInt32(body, 16);
            short option = BitConverter.ToInt16(body, 20);

            var consumeSlots = new short[8];
            var consumeSlotItemIds = new int[8];
            int off = 24;
            for (int i = 0; i < 8; i++)
            {
                consumeSlots[i] = BitConverter.ToInt16(body, off);
                consumeSlotItemIds[i] = BitConverter.ToInt32(body, off + 2);
                off += 6; // short slot + int itemTemplateId
            }

            // 防改包: 8件槽位不能重复
            if (consumeSlots.Distinct().Count() != consumeSlots.Length)
            {
                var dupErr = new GamePacketWriter();
                dupErr.WriteByte(0x00);
                dupErr.WriteByte(0x16);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x03EA, dupErr.ToArray()));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            var job = _characterRepository.GetById(cid)?.Job ?? 0;

            int ResolveNewItemId(int consumeMaterialId)
            {
                var cube = AbsoluteBindCubeService.Resolve(consumeMaterialId, job);
                if (!cube.Success)
                {
                    return -1;
                }

                foreach (var kv in cube.PartToItemId)
                {
                    if (kv.Value == requestedItemId)
                        return requestedItemId;
                }
                return -1;
            }

            if (!_sqliteSelectCharacterDataSource.TryCompoundAvatarSet(cid, aid, consumeSlots, consumeSlotItemIds, ResolveNewItemId, (byte)option,
                    consumeStackableSlot, out int newSlot, out var oldItemIds, out int newItemId, out int consumedTemplateId, out int consumedRemaining))
            {
                var err = new GamePacketWriter();
                err.WriteByte(0x00);
                err.WriteByte(0x16); // errcode 22 (物品删除失败), 与0x0063失败码一致
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x03EA, err.ToArray()));
                return;
            }

            var w2 = new GamePacketWriter();
            w2.WriteByte(0x01);                  
            w2.WriteByte(0x01); w2.WriteByte(0x00); w2.WriteByte(0x03); w2.WriteByte(0x00); 
            w2.WriteByte(0x01); w2.WriteByte(0x00); w2.WriteByte(0x00); w2.WriteByte(0x00); 
            w2.WriteInt16((short)newSlot);     
            w2.WriteInt32(newItemId);          
            w2.WriteInt16((short)option);   
            w2.WriteInt16(1);                  
            for (int i = 0; i < 8; i++)        
                w2.WriteInt16(consumeSlots[i]);
            w2.WriteZeroBytes(24);

            var respBody2 = w2.ToArray();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x03EA, respBody2));

            if (consumedTemplateId > 0)
            {
                var consumeItem = new CommonInventoryItem
                {
                    SlotIndex = consumeStackableSlot,
                    ItemTemplateId = consumedRemaining > 0 ? consumedTemplateId : -1,  // remain>0 发真ID，remain==0 发-1
                    CountOrInstanceValue = consumedRemaining,  
                };
                var consumeUpd = ItemListUpdateBuilder.BuildCommonUpdates(new List<CommonInventoryItem> { consumeItem });
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, consumeUpd));
            }
        }

        // ── 账号金库 ──────────────────────────────────────────────────────────

        private const int CargoInitialCapacity = 1;
        private static readonly int[] CargoCapacityTiers = { 1, 8, 16, 24, 32, 40, 48, 56, 64 };

        public async Task Handle_DEPOSIT_MONEY(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await HandleCargoGold(session, header.type, body, isDeposit: true);
        }

        public async Task Handle_WITHDRAW_MONEY(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await HandleCargoGold(session, header.type, body, isDeposit: false);
        }

        private async Task HandleCargoGold(EnhancedClientSession session, ushort wireType, byte[] body, bool isDeposit)
        {
            if (body == null || body.Length < 4)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, wireType, new byte[] { 0x00, 0x0A }));
                return;
            }

            int amount = BitConverter.ToInt32(body, 0);
            if (amount <= 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, wireType, new byte[] { 0x00, 0x0A }));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            var connStr = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);

            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var wallet = CurrencyService.LoadWallet(conn, tx, cid);
                    int cargoGold = LoadCargoStateField(conn, tx, aid, "value32");

                    int newCharGold, newCargoGold;
                    if (isDeposit)
                    {
                        if (wallet.Gold < amount)
                        {
                            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, wireType, new byte[] { 0x00, 0x0A }));
                            return;
                        }
                        newCharGold = wallet.Gold - amount;
                        newCargoGold = cargoGold + amount;
                    }
                    else
                    {
                        if (cargoGold < amount)
                        {
                            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, wireType, new byte[] { 0x00, 0x0A }));
                            return;
                        }
                        newCargoGold = cargoGold - amount;
                        newCharGold = wallet.Gold + amount;
                    }

                    CurrencyService.UpdateGold(conn, tx, cid, newCharGold);
                    SaveCargoGold(conn, tx, aid, newCargoGold);
                    tx.Commit();

                    var ack = new GamePacketWriter();
                    ack.WriteByte(0x01);
                    ack.WriteInt32(newCargoGold);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, wireType, ack.ToArray()));

                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                        Builders.TeleportPacketBuilder.BuildItemListUpdate(0, 0, newCharGold)));

                    FileLogger.Log($"[{ProtocolName}] {(isDeposit ? "DEPOSIT" : "WITHDRAW")}_MONEY: amount={amount} charGold={newCharGold} cargoGold={newCargoGold}");
                }
            }
        }

        private static int LoadCargoStateField(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx, int accountId, string column)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT {column} FROM account_cargo_state WHERE account_id=@aid;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                var result = cmd.ExecuteScalar();
                return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
            }
        }

        private static void SaveCargoGold(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx, int accountId, int gold)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE account_cargo_state SET value32=@gold, updated_at=CURRENT_TIMESTAMP WHERE account_id=@aid;";
                cmd.Parameters.AddWithValue("@gold", gold);
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.ExecuteNonQuery();
            }
        }
        public async Task Handle_CREATE_ACCOUNT_CARGO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = ResolveOwner(session);
            var connStr = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);

            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
            {
                conn.Open();
                int existing = LoadCargoStateField(conn, null, aid, "selection_key");
                if (existing > 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0131, new byte[] { 0x00, 0x14 }));
                    return;
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR REPLACE INTO account_cargo_state (account_id, selection_key, value32, updated_at)
VALUES (@aid, @cap, 0, CURRENT_TIMESTAMP);";
                    cmd.Parameters.AddWithValue("@aid", aid);
                    cmd.Parameters.AddWithValue("@cap", CargoInitialCapacity);
                    cmd.ExecuteNonQuery();
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0131, new byte[] { 0x01 }));
            FileLogger.Log($"[{ProtocolName}] CREATE_ACCOUNT_CARGO: aid={aid} selectionKey={CargoInitialCapacity}");
        }

        public async Task Handle_UPGRADE_ACCOUNT_CARGO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = ResolveOwner(session);
            var connStr = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);

            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
            {
                conn.Open();
                int current = LoadCargoStateField(conn, null, aid, "selection_key");
                if (current <= 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0132, new byte[] { 0x00, 0x15 }));
                    return;
                }
                int nextTierIndex = Array.IndexOf(CargoCapacityTiers, current) + 1;
                if (nextTierIndex <= 0 || nextTierIndex >= CargoCapacityTiers.Length)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0132, new byte[] { 0x00, 0x13 }));
                    return;
                }
                int newCap = CargoCapacityTiers[nextTierIndex];

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE account_cargo_state SET selection_key=@cap, updated_at=CURRENT_TIMESTAMP WHERE account_id=@aid;";
                    cmd.Parameters.AddWithValue("@cap", newCap);
                    cmd.Parameters.AddWithValue("@aid", aid);
                    cmd.ExecuteNonQuery();
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0132, new byte[] { 0x01 }));
            FileLogger.Log($"[{ProtocolName}] UPGRADE_ACCOUNT_CARGO: aid={aid} selectionKey upgraded");
        }

    }
}
