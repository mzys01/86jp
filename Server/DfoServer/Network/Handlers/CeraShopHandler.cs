using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.CeraShop;
using DfoServer.Network.Parsers.CeraShop;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class CeraShopHandler
    {
        // 购买/刷新走共享 store; 门面仅保留 premium 刷新用的全量选角快照 Load
        private readonly Game.Inventory.IInventoryStore _inventoryStore;
        private readonly SqliteSelectCharacterDataSource _sqliteSelectCharacterDataSource;
        private readonly InventoryRefreshSender _refresh;

        public string ProtocolName => "GameProtocol";

        public CeraShopHandler(Game.Inventory.IInventoryStore inventoryStore, SqliteSelectCharacterDataSource sqliteSelectCharacterDataSource, InventoryRefreshSender refresh)
        {
            _inventoryStore = inventoryStore ?? throw new ArgumentNullException(nameof(inventoryStore));
            _sqliteSelectCharacterDataSource = sqliteSelectCharacterDataSource ?? throw new ArgumentNullException(nameof(sqliteSelectCharacterDataSource));
            _refresh = refresh;
        }

        public async Task HandleCeraShopPurchase(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY raw body({body?.Length ?? 0}): {(body != null ? BitConverter.ToString(body) : "null")}");
            if (!CeraShopPurchaseRequest.TryParse(body, out var request))
            {
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: parse failed");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0040, CeraShopPurchaseAckBuilder.BuildError()));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY parsed: {request.CommodityNos.Count} item(s) [{string.Join(", ", request.CommodityNos)}] paymentMode={request.PaymentMode}");
            var cid = session.Player?.CharacterId ?? 0;
            var aid = session.Account?.AccountId ?? 0;
            if (cid <= 0 || aid <= 0)
            {
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: invalid owner cid={cid} aid={aid}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0040, CeraShopPurchaseAckBuilder.BuildError(request)));
                return;
            }

            // 购物车: 逐件结算, 每个 commodityNo 买 1 份(份内数量由商品定义)
            var results = new System.Collections.Generic.List<InventoryMutationResult>();
            var successItems = new System.Collections.Generic.List<System.Tuple<int, InventoryMutationResult>>();
            var contractItems = new List<(int itemTemplateId, int count)>();
            var skillTreeExpansionUnlocked = false;
            for (int idx = 0; idx < request.CommodityNos.Count; idx++)
            {
                var commodityNo = request.CommodityNos[idx];
                var attrValue = idx < request.AttributeValues.Count ? request.AttributeValues[idx] : (byte)0;

                var (dcOk, dcResult) = await Game.Premium.PremiumService.TryBuyDevilContractSlot(session, commodityNo, _sqliteSelectCharacterDataSource);
                if (dcOk)
                {
                    successItems.Add(System.Tuple.Create(commodityNo, dcResult));
                    results.Add(dcResult);
                    continue;
                }

                if (_inventoryStore.TryBuyCeraShopItem(cid, aid, commodityNo, 1, request.PaymentMode, attrValue, out var result))
                {
                    FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: OK commodityNo={commodityNo} slot={result.SlotIndex} item=0x{result.ItemTemplateId:X8} count={result.AppliedCount} coin={result.UpdatedCoin} extra={result.ExtraResults.Count}");
                    results.Add(result);
                    successItems.Add(System.Tuple.Create(commodityNo, result));
                    if (result.ConsumedOnPurchase
                        && result.ItemTemplateId == Game.Skills.SkillTreeExpansionState.ExpansionItemTemplateId)
                        skillTreeExpansionUnlocked = true;
                    if (Game.Premium.PremiumService.IsContractItem(result.ItemTemplateId))
                        contractItems.Add((result.ItemTemplateId, 1));
                }
                else
                {
                    FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: FAILED commodityNo={commodityNo}");
                }
            }

            if (results.Count == 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0040, CeraShopPurchaseAckBuilder.BuildError(request)));
                return;
            }

            var last = results[results.Count - 1];

            // CMD 64 购买应答(成功): 客户端按 ACK 中的 commodityNo 清购物车项,
            // 因此多件购物车需要逐件回 ACK, 但库存/点券刷新仍合并发送。
            foreach (var item in successItems)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0040,
                    CeraShopPurchaseAckBuilder.BuildSuccess(item.Item1, item.Item2)));
            }

            // 普通商品走 NOTI 0x0E。多商品购物车时合并成一个 UPDATE_ITEM_LIST,
            // 与地下城翻牌/任务掉落一致, 避免连续多个 0x0E 时客户端漏刷新 slot0 金币。
            var avatarSlots = new HashSet<short>();
            var petSlots = new HashSet<short>();
            var itemUpdateResults = new System.Collections.Generic.List<InventoryMutationResult>();
            InventoryMutationResult goldResult = null;
            var refreshAccountCargo = false;
            var refreshPersonalCargo = false;
            foreach (var result in results)
            {
                var resultGroup = new System.Collections.Generic.List<InventoryMutationResult> { result };
                resultGroup.AddRange(result.ExtraResults);

                foreach (var updateResult in resultGroup)
                {
                    if (updateResult.ConsumedOnPurchase)
                    {
                        if (updateResult.ListType == InventoryListType.AccountCargo)
                            refreshAccountCargo = true;
                        else if (updateResult.ListType == InventoryListType.PersonalCargo)
                            refreshPersonalCargo = true;
                        continue;
                    }
                    if (updateResult.ListType == InventoryListType.Avatar)
                    {
                        avatarSlots.Add(updateResult.SlotIndex);
                        continue;
                    }
                    if (updateResult.ListType == InventoryListType.Pet)
                    {
                        petSlots.Add(updateResult.SlotIndex);
                        continue;
                    }
                    itemUpdateResults.Add(updateResult);
                }

                if (result.GoldSpent)
                    goldResult = result;
            }

            if (goldResult != null)
            {
                itemUpdateResults.Add(new InventoryMutationResult
                {
                    SlotIndex = 0,
                    ItemTemplateId = 0,
                    RemainingStackCount = goldResult.UpdatedGold,
                });
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: gold refresh queued gold={goldResult.UpdatedGold}");
            }

            if (refreshAccountCargo)
            {
                var snapshot = _inventoryStore.LoadCharacterItemListSnapshot(cid, aid);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0132,
                    CommonPacketBodyBuilder.BuildSuccessAck()));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000D,
                    ItemListPacketBuilder.BuildBody(snapshot, InventoryListType.AccountCargo)));
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: account cargo upgrade ACK and ITEM_LIST refresh sent");
            }

            if (refreshPersonalCargo)
            {
                var snapshot = _inventoryStore.LoadCharacterItemListSnapshot(cid, aid);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0198,
                    CommonPacketBodyBuilder.BuildSuccessAck()));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000D,
                    ItemListPacketBuilder.BuildBody(snapshot, InventoryListType.PersonalCargo)));
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: personal cargo upgrade ACK and ITEM_LIST refresh sent");
            }

            if (itemUpdateResults.Count > 0)
            {
                var snapshot = _inventoryStore.LoadCharacterItemListSnapshot(cid, aid);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                    ItemListUpdateBuilder.BuildCommonUpdates(BuildCommonItemUpdates(itemUpdateResults, snapshot))));
            }

            if (avatarSlots.Count > 0 || petSlots.Count > 0)
            {
                if (avatarSlots.Count > 0)
                {
                    await SendAvatarItemListUpdate(session, cid, aid, avatarSlots);
                    FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: avatar ITEM_LIST update sent count={avatarSlots.Count}");
                }
                if (petSlots.Count > 0)
                {
                    await SendPetItemListUpdate(session, cid, aid, petSlots);
                    FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: pet ITEM_LIST update sent count={petSlots.Count}");
                }
            }

            if (_refresh != null && results.Exists(r => r.NameTagEquipped))
            {
                _refresh.ReloadSubtype0Tail(session);
                await _refresh.SendNoti2AppearanceUpdate(session);
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: name tag appearance refresh sent");
            }

            if (contractItems.Count > 0)
                await Game.Premium.PremiumService.ActivateAndNotify(session, contractItems, _sqliteSelectCharacterDataSource);

            if (skillTreeExpansionUnlocked)
            {
                var refreshed = _sqliteSelectCharacterDataSource.Load(cid, aid);
                var record = refreshed?.CharacterRecord;
                if (record != null)
                {
                    session.Player.Subtype0Tail = record.Subtype0Tail;
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x0002,
                        UserInfoSubtype0Builder.BuildNotificationBody(record)));
                    FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: skill-tree expansion USERINFO subtype0 refresh sent cid={cid}");
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0035,
                CeraUpdateBuilder.Build(last.UpdatedCoin, last.UpdatedTokenCera, last.UpdatedHappyTokenCera)));
        }

        private async Task SendAvatarItemListUpdate(EnhancedClientSession session, int characterId, int accountId, IReadOnlyCollection<short> slots)
        {
            var updates = new List<AvatarInventoryItem>();
            var emptySlots = new List<short>();
            foreach (var slot in slots)
            {
                var item = _inventoryStore.LoadAvatarItemForRefresh(characterId, slot);
                if (item != null)
                    updates.Add(item);
                else
                    emptySlots.Add(slot);
            }

            if (updates.Count > 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x000E,
                    ItemListUpdateBuilder.BuildAvatarUpdates(updates)));
            }

            if (emptySlots.Count > 0)
            {
                // 时装空槽刷新先按通用空 entry 测试。
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x000E,
                    ItemListUpdateBuilder.BuildEmptyUpdates(InventoryListType.Avatar, emptySlots)));
            }
        }

        private async Task SendPetItemListUpdate(EnhancedClientSession session, int characterId, int accountId, IReadOnlyCollection<short> slots)
        {
            var updates = new List<PetInventoryItem>();
            var emptySlots = new List<short>();
            foreach (var slot in slots)
            {
                var item = _inventoryStore.LoadPetItemForRefresh(characterId, slot);
                if (item != null)
                    updates.Add(item);
                else
                    emptySlots.Add(slot);
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
                // 宠物空槽刷新先按通用空 entry 测试。
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x000E,
                    ItemListUpdateBuilder.BuildEmptyUpdates(InventoryListType.Pet, emptySlots)));
            }
        }

        private static List<CommonInventoryItem> BuildCommonItemUpdates(System.Collections.Generic.IReadOnlyList<InventoryMutationResult> updates, CharacterItemListSnapshot snapshot)
        {
            var items = new List<CommonInventoryItem>();
            if (updates == null)
                return items;

            foreach (var update in updates)
            {
                var common = FindUpdatedCommonItem(snapshot, update);
                if (common != null)
                {
                    items.Add(common);
                    continue;
                }

                items.Add(ItemListUpdateBuilder.CreateCommonSlotUpdate(
                    update.SlotIndex,
                    update.ItemTemplateId,
                    update.RemainingStackCount));
            }
            return items;
        }

        private static CommonInventoryItem FindUpdatedCommonItem(CharacterItemListSnapshot snapshot, InventoryMutationResult update)
        {
            if (snapshot == null || update == null)
                return null;

            if (update.ListType != InventoryListType.Main || update.ItemTemplateId <= 0)
                return null;

            return snapshot.MainItems.Find(item => item.SlotIndex == update.SlotIndex);
        }

    }
}
