using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
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

                    if (!_inventoryStore.TryDeleteItem(cid, aid, listType, slotIndex, deleteCount, out var result))
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

            if (!_inventoryStore.TryDeleteItem(cid, aid, lt, si, ic, out var simpleResult))
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
            if (!_inventoryStore.TryBuyItem(cid, aid, itemTemplateId, buyCount, out var result))
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
                var updBody = ItemListUpdateBuilder.BuildCommonSlotUpdate(result.CostItemSlotIndex, result.CostItemTemplateId, result.CostItemNewStackCount);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, updBody));
                FileLogger.Log($"[{ProtocolName}] BUY_ITEM: NOTI 14 cost update slot={result.CostItemSlotIndex} id=0x{result.CostItemTemplateId:X8} newCount={result.CostItemNewStackCount}");
            }

            if (result.ListType == InventoryListType.Pet)
            {
                await _refresh.SendUpdateItemList(session, InventoryListType.Pet, result.SlotIndex);
                FileLogger.Log($"[{ProtocolName}] BUY_ITEM: pet ITEM_LIST update sent slot={result.SlotIndex}");
            }
        }

        public async Task Handle_ENUM_CMDPACKET_SELL_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] SELL_ITEM raw body({body?.Length ?? 0}): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseDeleteOrSellRequest(body, out var listType, out var slotIndex, out var sellCount))
                return;

            FileLogger.Log($"[{ProtocolName}] SELL_ITEM: listType={listType}({(byte)listType}) slot={slotIndex} count={sellCount}");

            var (cid, aid) = ResolveOwner(session);
            if (!_inventoryStore.TrySellItem(cid, aid, listType, slotIndex, sellCount, out var result))
            {
                FileLogger.Log($"[{ProtocolName}] SELL_ITEM: FAILED listType={listType} slot={slotIndex} count={sellCount}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0016, SellItemBuilder.BuildError(0x11)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] SELL_ITEM: OK gold={result.UpdatedGold} applied={result.AppliedCount}");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0016, SellItemBuilder.Build((byte)listType, result.SlotIndex, result.AppliedCount, result.UpdatedGold)));
        }

        public async Task Handle_SET_CLONE_TITLE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var cloneTitle = (body != null && body.Length >= 4) ? BitConverter.ToInt32(body, 0) : 0;
            var (cid, _) = ResolveOwner(session);
            Game.Appearance.AppearanceService.SaveCloneTitleItemId(cid, cloneTitle);
            if (session.Player != null)
            {
                var tail = session.Player.Subtype0Tail ?? new Game.SelectCharacter.UserInfoMinimumTailSnapshot();
                tail.CloneTitleItemId = (uint)(cloneTitle > 0 ? cloneTitle : 0);
                session.Player.Subtype0Tail = tail;
            }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0239,
                Game.Appearance.AppearanceService.BuildCloneTitleAckBody(cloneTitle)));
            FileLogger.Log($"[{ProtocolName}] SET_CLONE_TITLE: cloneTitle=0x{cloneTitle:X8} persisted, ackExtra=00-00");
        }

        public async Task Handle_TITLE_BOOK(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 20)
                return;

            var itemSpaceRaw = BitConverter.ToInt32(body, 0);
            var slot = (short)BitConverter.ToInt32(body, 4);
            var itemId = BitConverter.ToInt32(body, 8);
            var category = BitConverter.ToInt32(body, 12);
            var index = BitConverter.ToInt32(body, 16);

            if (!TryParseInventoryListType(itemSpaceRaw, out var itemSpace))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, header.type, BuildTitleBookFailure(itemSpaceRaw, category, 0x0A)));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            bool ok;
            Game.TitleBook.TitleBookMutationResult result;
            if (header.type == 0x019C)
                ok = _sqliteSelectCharacterDataSource.TryPutTitleBook(cid, aid, itemSpace, slot, itemId, category, index, out result);
            else
                ok = _sqliteSelectCharacterDataSource.TryGetTitleBook(cid, aid, itemSpace, slot, itemId, category, index, out result);

            if (!ok)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, header.type, BuildTitleBookFailure(itemSpaceRaw, category, result != null ? result.ErrorCode : (byte)0x0A)));
                return;
            }

            if (result.EquipmentChanged)
            {
                if (ShouldSuppressSelfUserInfoRefresh(session))
                {
                    FileLogger.Log($"[{ProtocolName}] TITLE_BOOK: skipped self NOTI 2 appearance refresh");
                }
                else
                {
                    _refresh.ReloadSubtype0Tail(session);
                    await _refresh.SendSubtype0PetStateRefresh(session);
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01, header.type, BuildTitleBookSuccess(itemSpaceRaw, slot, result.Category, result.BookIndex)));

            if (result.ItemLockChanged)
                await _refresh.SendAllEquipmentItemLockListRefresh(session);
        }

        public async Task Handle_ACHIEVEMENT_TRIGGER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 10)
                return;

            var questId = BitConverter.ToInt32(body, 0);
            var delta1 = BitConverter.ToUInt16(body, 4);
            var delta2 = BitConverter.ToUInt16(body, 6);
            var delta3 = BitConverter.ToUInt16(body, 8);
            var (cid, _) = ResolveOwner(session);

            if (!_sqliteSelectCharacterDataSource.TryTriggerAchievement(cid, questId, delta1, delta2, delta3, out var result))
            {
                var fail = new GamePacketWriter();
                fail.WriteByte(0);
                fail.WriteByte(result != null ? result.ErrorCode : (byte)0x0A);
                fail.WriteInt32(questId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, fail.ToArray()));
                return;
            }

            var w = new GamePacketWriter();
            w.WriteByte(1);
            w.WriteInt32(result.QuestId);
            w.WriteUInt16(result.Remain1);
            w.WriteUInt16(result.Remain2);
            w.WriteUInt16(result.Remain3);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, w.ToArray()));

            if (result.Completed && result.TitleItemId > 0)
            {
                var complete = new GamePacketWriter();
                complete.WriteInt32(result.QuestId);
                complete.WriteInt32(result.Category);
                complete.WriteInt32(result.BookIndex);
                complete.WriteInt32(result.TitleItemId);
                complete.WriteUInt16((ushort)Math.Max(0, result.BookIndex));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0168, complete.ToArray()));
                await SendTitleBookCategoryRefresh(session, cid, result.Category);
            }
        }

        private static byte[] BuildTitleBookSuccess(int itemSpace, short slot, int category, int index)
        {
            var w = new GamePacketWriter();
            w.WriteByte(1);
            w.WriteInt32(itemSpace);
            w.WriteInt32(slot);
            w.WriteInt32(category);
            w.WriteInt32(index);
            return w.ToArray();
        }

        private static byte[] BuildTitleBookFailure(int itemSpace, int category, byte errorCode)
        {
            var w = new GamePacketWriter();
            w.WriteByte(0);
            w.WriteByte(errorCode);
            w.WriteInt32(itemSpace);
            w.WriteInt32(category);
            return w.ToArray();
        }

        private async Task SendTitleBookCategoryRefresh(EnhancedClientSession session, int characterId, int category)
        {
            var snapshot = _sqliteSelectCharacterDataSource.LoadTitleBookSnapshot(characterId, category);
            if (snapshot == null)
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0166,
                TitleBookListBodyBuilder.BuildCategoryBody(snapshot)));
        }

        private static bool TryParseInventoryListType(int value, out InventoryListType listType)
        {
            if (value >= byte.MinValue && value <= byte.MaxValue && Enum.IsDefined(typeof(InventoryListType), (byte)value))
            {
                listType = (InventoryListType)(byte)value;
                return true;
            }

            listType = InventoryListType.Main;
            return false;
        }

        // ── 账号金库 ──────────────────────────────────────────────────────────
        // SQL 已下沉 SqliteInventoryStore.Cargo.cs; handler 只留解析+ACK。

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
            int newCharGold, newCargoGold;
            var ok = isDeposit
                ? _inventoryStore.TryDepositCargoGold(cid, aid, amount, out newCharGold, out newCargoGold)
                : _inventoryStore.TryWithdrawCargoGold(cid, aid, amount, out newCharGold, out newCargoGold);
            if (!ok)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, wireType, new byte[] { 0x00, 0x0A }));
                return;
            }

            var ack = new GamePacketWriter();
            ack.WriteByte(0x01);
            ack.WriteInt32(newCargoGold);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, wireType, ack.ToArray()));

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                ItemListUpdateBuilder.BuildGoldUpdate(newCharGold)));

            FileLogger.Log($"[{ProtocolName}] {(isDeposit ? "DEPOSIT" : "WITHDRAW")}_MONEY: amount={amount} charGold={newCharGold} cargoGold={newCargoGold}");
        }

        public async Task Handle_CREATE_ACCOUNT_CARGO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = ResolveOwner(session);
            if (!_inventoryStore.TryCreateAccountCargo(cid, aid, out var costResult, out var errorCode))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0131, new byte[] { 0x00, errorCode }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0131, new byte[] { 0x01 }));
            await SendAccountCargoCostRefresh(session, costResult);
            await _refresh.SendItemListRefresh(session, InventoryListType.AccountCargo);
            FileLogger.Log($"[{ProtocolName}] CREATE_ACCOUNT_CARGO: aid={aid} cargo created costGold={costResult?.GoldSpent == true} costItem=0x{costResult?.CostItemTemplateId ?? 0:X8}");
        }

        public async Task Handle_UPGRADE_ACCOUNT_CARGO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = ResolveOwner(session);
            if (!_inventoryStore.TryUpgradeAccountCargo(cid, aid, out var costResult, out var errorCode))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0132, new byte[] { 0x00, errorCode }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0132, new byte[] { 0x01 }));
            await SendAccountCargoCostRefresh(session, costResult);
            await _refresh.SendItemListRefresh(session, InventoryListType.AccountCargo);
            FileLogger.Log($"[{ProtocolName}] UPGRADE_ACCOUNT_CARGO: aid={aid} selectionKey upgraded costGold={costResult?.GoldSpent == true} costItem=0x{costResult?.CostItemTemplateId ?? 0:X8} costItemNew={costResult?.CostItemNewStackCount ?? 0} coin={costResult?.UpdatedCoin ?? 0}");
        }

        private async Task SendAccountCargoCostRefresh(EnhancedClientSession session, InventoryMutationResult costResult)
        {
            if (costResult == null)
                return;

            if (costResult.GoldSpent)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                    ItemListUpdateBuilder.BuildGoldUpdate(costResult.UpdatedGold)));
                return;
            }

            if (costResult.CostItemTemplateId > 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                    ItemListUpdateBuilder.BuildCommonSlotUpdate(
                        costResult.CostItemSlotIndex,
                        costResult.CostItemTemplateId,
                        costResult.CostItemNewStackCount)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0035,
                CeraUpdateBuilder.Build(
                    costResult.UpdatedCoin,
                    costResult.UpdatedTokenCera,
                    costResult.UpdatedHappyTokenCera)));
        }

        public async Task Handle_UPGRADE_CARGO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = ResolveOwner(session);
            if (!_inventoryStore.TryUpgradePersonalCargo(cid, aid, out var newListParam16, out var errorCode))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00, errorCode }));
                FileLogger.Log($"[{ProtocolName}] UPGRADE_CARGO: failed cid={cid} aid={aid} error=0x{errorCode:X2} rawBody({body?.Length ?? 0}B)={(body != null ? BitConverter.ToString(body) : "null")}");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x01 }));
            await _refresh.SendItemListRefresh(session, InventoryListType.PersonalCargo);
            FileLogger.Log($"[{ProtocolName}] UPGRADE_CARGO: cid={cid} aid={aid} personalCargoListParam16={newListParam16} rawBody({body?.Length ?? 0}B)={(body != null ? BitConverter.ToString(body) : "null")}");
        }
    }
}
