using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
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

        public string ProtocolName => "GameProtocol";

        public InventoryHandler(SqliteSelectCharacterDataSource sqliteSelectCharacterDataSource, ICharacterRepository characterRepository)
        {
            _sqliteSelectCharacterDataSource = sqliteSelectCharacterDataSource ?? throw new ArgumentNullException(nameof(sqliteSelectCharacterDataSource));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
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

                for (int i = 0; i < arrayCount && offset + 12 <= body.Length; i++)
                {
                    var deleteCount = BitConverter.ToInt16(body, offset);
                    var slotIndex = BitConverter.ToInt16(body, offset + 2);
                    var clientInstanceValue = BitConverter.ToInt32(body, offset + 8);
                    offset += 12;

                    if (!_sqliteSelectCharacterDataSource.TryDeleteItem(cid, aid, listType, slotIndex, deleteCount, out var result))
                    {
                        FileLogger.Log($"[{ProtocolName}] DELETE_ITEM(ext): failed at listType={listType} slot={slotIndex} count={deleteCount}");
                        var errAck = new byte[] { 0x00, 0x17, (byte)listType };
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0012, errAck));
                        continue;
                    }

                    
                    result.RemainingStackCount = clientInstanceValue;
                    result.AppliedCount = deleteCount;
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0012, DeleteItemAckBuilder.Build(result)));
                    FileLogger.Log($"[{ProtocolName}] DELETE_ITEM(ext): slot={slotIndex} applied={deleteCount} remaining={clientInstanceValue}");
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

                    var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
                    var mainUpdateBody = BuildGrantedMainItemUpdates(snapshot, result.GrantedItems, result.SlotIndex, result.PackageItemTemplateId);
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

                    var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
                    var mainUpdateBody = BuildGrantedMainItemUpdates(snapshot, result.GrantedItems, result.SlotIndex, result.PackageItemTemplateId);
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
            if (!_sqliteSelectCharacterDataSource.TryUseBoosterItem(cid, aid, slotIndex, selectedItemTemplateIds, out var result))
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
            var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
            var mainUpdateBody = BuildBoosterMainItemUpdates(snapshot, result);
            if (mainUpdateBody != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, mainUpdateBody));

            var petUpdateBody = BuildBoosterPetItemUpdates(snapshot, result);
            if (petUpdateBody != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, petUpdateBody));

            var avatarUpdateBody = BuildBoosterAvatarItemUpdates(snapshot, result);
            if (avatarUpdateBody != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, avatarUpdateBody));
        }

        private static byte[] BuildBoosterMainItemUpdates(CharacterItemListSnapshot snapshot, BoosterUseResult result)
        {
            if (snapshot == null || result == null)
                return null;

            var slots = new HashSet<short>();
            foreach (var reward in result.Rewards)
            {
                if (reward.ListType == InventoryListType.Main)
                    slots.Add(reward.SlotIndex);
            }

            slots.Add(result.SourceSlotIndex);

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
            int sourceItemTemplateId)
        {
            if (snapshot == null || grantedItems == null)
                return null;

            var slots = new HashSet<short>();
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

        // 合并装扮(时装合成) CMD 0x63(99)
        // 请求 body 布局(22字节): off0 short consumeSlot, off2 short slot1, off8 short slot2,
        // off14 int reqItemId(请求的目标时装), 其余字段未用到。
        public async Task Handle_COMPOUND_AVATAR(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] COMPOUND_AVATAR raw body({body?.Length ?? 0}): {(body != null ? BitConverter.ToString(body) : "null")}");
            if (body == null || body.Length < 22)
                return;

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
                        if (!prob.Success)
                            FileLogger.Log($"  [CompoundAvatar] probability resolve failed: {prob.FailReason}, falling back to requested item {reqItemId}");
                        return prob.Success ? prob.NewItemIds : new List<int> { reqItemId };
                    },
                    newOption,
                    out List<int> newSlots, out int oldItemId1, out int oldItemId2, out List<int> newItemIds,
                    out int consumedItemTemplateId, out int consumedItemRemainingCount))
            {
                // 失败回包: 成功标志=0, 不删不加。客户端据此把放进去的2件时装还原。
                var err = new GamePacketWriter();
                err.WriteByte(0x00);
                err.WriteByte(0x16); // errcode 22 (物品删除失败), 与台服一致
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0063, err.ToArray()));
                FileLogger.Log($"[{ProtocolName}] COMPOUND_AVATAR: FAILED slot1={slot1} slot2={slot2} consumeSlot={consumeSlot}");
                return;
            }

            var w = new GamePacketWriter();
            w.WriteByte(0x01);              // 成功标志
            w.WriteByte(0x03);              // count = 2件被删时装 + 1个被扣消耗品
            // 被删时装1
            w.WriteByte(0x01);              // listType=1(时装)
            w.WriteInt16(slot1);            // slot
            w.WriteInt32(1);                // 固定1
            // 被删时装2
            w.WriteByte(0x01);
            w.WriteInt16(slot2);
            w.WriteInt32(1);
            // 被扣消耗品(合成器): 第三个字段是本次消耗数量, 客户端据此对槛位做增量扣减。
            // 86JP合成器消耗规则恒为每次1个。
            w.WriteByte(0x00);              // listType=0(消耗品/event item)
            w.WriteInt16(consumeSlot);      // slot
            w.WriteInt32(1);                // 本次消耗数量
            // 新时装信息: 客户端固定读2组, 未命中稀有池时第2组填占位(slot=-1, itemId=0)。
            for (int i = 0; i < 2; i++)
            {
                bool hasItem = i < newItemIds.Count;
                w.WriteInt16(hasItem ? (short)newSlots[i] : (short)-1); // 新时装真实slot(找到的空位), 占位=-1
                w.WriteInt32(hasItem ? newItemIds[i] : 0);  // 新时装itemId, 占位=0
                w.WriteInt32(0);                   // RemainDate(剩余期限天, 0=永久)
                w.WriteInt16(newOption);           // option(附加属性index)
                w.WriteInt32(30);                  // 镶嵌孔数据长度
                w.WriteZeroBytes(30);              // JewelSocketData(暂全0)
                w.WriteInt32(4);                   // 扩展信息长度
                w.WriteZeroBytes(4);               // ExpansionInfo(暂全0)
            }

            var respBody = w.ToArray();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0063, respBody));
            FileLogger.Log($"[{ProtocolName}] COMPOUND_AVATAR: OK slot1={slot1}(item {oldItemId1}) slot2={slot2}(item {oldItemId2}) " +
                           $"consumeSlot={consumeSlot}(template {consumedItemTemplateId}, remain {consumedItemRemainingCount}) -> " +
                           $"newSlots=[{string.Join(",", newSlots)}] newItemIds=[{string.Join(",", newItemIds)}]");
            FileLogger.Log($"[{ProtocolName}] COMPOUND_AVATAR resp body({respBody.Length}): {BitConverter.ToString(respBody)}");
        }
    }
}
