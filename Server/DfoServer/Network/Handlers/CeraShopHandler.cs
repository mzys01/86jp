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
        private readonly SqliteSelectCharacterDataSource _sqliteSelectCharacterDataSource;

        public string ProtocolName => "GameProtocol";

        public CeraShopHandler(SqliteSelectCharacterDataSource sqliteSelectCharacterDataSource)
        {
            _sqliteSelectCharacterDataSource = sqliteSelectCharacterDataSource ?? throw new ArgumentNullException(nameof(sqliteSelectCharacterDataSource));
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
            var boughtExpertContract = false;
            var boughtDevilContract = false;
            var premiumNotifications = new List<(int premiumType, long remaining)>();
            for (int idx = 0; idx < request.CommodityNos.Count; idx++)
            {
                var commodityNo = request.CommodityNos[idx];
                var attrValue = idx < request.AttributeValues.Count ? request.AttributeValues[idx] : (byte)0;

                if (Game.Premium.PremiumService.TryBuyDevilContractSlot(aid, commodityNo, out var dcResult))
                {
                    successItems.Add(System.Tuple.Create(commodityNo, dcResult));
                    results.Add(dcResult);
                    boughtDevilContract = true;
                    continue;
                }

                if (_sqliteSelectCharacterDataSource.TryBuyCeraShopItem(cid, aid, commodityNo, 1, request.PaymentMode, attrValue, out var result))
                {
                    FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: OK commodityNo={commodityNo} slot={result.SlotIndex} item=0x{result.ItemTemplateId:X8} count={result.AppliedCount} coin={result.UpdatedCoin} extra={result.ExtraResults.Count}");
                    results.Add(result);
                    successItems.Add(System.Tuple.Create(commodityNo, result));
                    if (result.ItemTemplateId == 44 || result.ItemTemplateId == 45)
                        boughtExpertContract = true;
                    var activated = Game.Premium.PremiumService.TryActivateContract(aid, result.ItemTemplateId);
                    if (activated != null)
                        premiumNotifications.Add(activated.Value);
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
            foreach (var result in results)
            {
                var resultGroup = new System.Collections.Generic.List<InventoryMutationResult> { result };
                resultGroup.AddRange(result.ExtraResults);

                foreach (var updateResult in resultGroup)
                {
                    if (updateResult.ConsumedOnPurchase)
                        continue;
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

            if (itemUpdateResults.Count > 0)
            {
                var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                    BuildItemListUpdate(itemUpdateResults, snapshot)));
            }

            if (avatarSlots.Count > 0 || petSlots.Count > 0)
            {
                var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
                if (avatarSlots.Count > 0)
                {
                    var avatarUpdateBody = BuildAvatarItemListUpdate(snapshot, avatarSlots);
                    if (avatarUpdateBody != null)
                    {
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, avatarUpdateBody));
                        FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: avatar ITEM_LIST update sent count={avatarSlots.Count}");
                    }
                }
                if (petSlots.Count > 0)
                {
                    var petUpdateBody = BuildPetItemListUpdate(snapshot, petSlots);
                    if (petUpdateBody != null)
                    {
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, petUpdateBody));
                        FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: pet ITEM_LIST update sent count={petSlots.Count}");
                    }
                }
            }

            foreach (var pn in premiumNotifications)
            {
                var nw = new GamePacketWriter();
                nw.WriteUInt16(2);
                nw.WriteByte((byte)pn.premiumType);
                nw.WriteBytes(BitConverter.GetBytes(pn.remaining));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0042, nw.ToArray()));
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: premium notify type={pn.premiumType} remaining={pn.remaining}");
            }

            if (boughtExpertContract || boughtDevilContract)
                await SendPremiumServiceRefresh(session, cid, aid);

            if (boughtDevilContract)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var maxExpire = Game.Premium.PremiumService.LoadDevilContractMaxExpire(
                    Infrastructure.SqliteDatabaseBootstrap.Initialize(
                        Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath),
                    aid);
                if (maxExpire > now)
                {
                    var nw = new GamePacketWriter();
                    nw.WriteUInt16(2);
                    nw.WriteByte((byte)Game.Premium.DevilContractCatalog.ActivationPremiumType);
                    nw.WriteBytes(BitConverter.GetBytes(maxExpire - now));
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0042, nw.ToArray()));
                    FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: devil contract notify type=58 remaining={maxExpire - now}");
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0035,
                BuildCeraUpdate(last.UpdatedCoin, last.UpdatedTokenCera, last.UpdatedHappyTokenCera)));
        }

        private async Task SendPremiumServiceRefresh(EnhancedClientSession session, int characterId, int accountId)
        {
            try
            {
                var snapshot = _sqliteSelectCharacterDataSource.Load(characterId, accountId);
                var initSnap = snapshot?.InitializationSnapshot;
                if (initSnap?.PremiumServiceData == null)
                    return;

                var writer = new GamePacketWriter();
                writer.WriteByte(1);
                writer.WriteUInt16(initSnap.PremiumServiceType);
                writer.WriteBytes(initSnap.PremiumServiceData);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0312, writer.ToArray()));
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: premium service refresh sent char={characterId} account={accountId}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: premium service refresh failed: {ex.Message}");
            }
        }

        private static byte[] BuildItemListUpdate(System.Collections.Generic.IReadOnlyList<InventoryMutationResult> updates, CharacterItemListSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteUInt16((ushort)updates.Count);
            foreach (var update in updates)
            {
                var common = FindUpdatedCommonItem(snapshot, update);
                if (common != null)
                {
                    ItemListPacketBuilder.WriteCommonEntry(writer, common);
                    continue;
                }

                WriteCompactItemListUpdate(writer, update);
            }
            return writer.ToArray();
        }

        private static CommonInventoryItem FindUpdatedCommonItem(CharacterItemListSnapshot snapshot, InventoryMutationResult update)
        {
            if (snapshot == null || update == null)
                return null;

            if (update.ListType != InventoryListType.Main || update.ItemTemplateId <= 0)
                return null;

            return snapshot.MainItems.Find(item => item.SlotIndex == update.SlotIndex);
        }

        private static byte[] BuildAvatarItemListUpdate(CharacterItemListSnapshot snapshot, HashSet<short> slots)
        {
            if (snapshot == null || slots == null || slots.Count == 0)
                return null;

            var updates = new List<AvatarInventoryItem>();
            foreach (var slot in slots)
            {
                var item = snapshot.AvatarItems.Find(x => x.SlotIndex == slot);
                if (item != null)
                    updates.Add(item);
            }

            if (updates.Count == 0)
                return null;

            return ItemListUpdateBuilder.BuildAvatarUpdates(updates);
        }

        private static byte[] BuildPetItemListUpdate(CharacterItemListSnapshot snapshot, HashSet<short> slots)
        {
            if (snapshot == null || slots == null || slots.Count == 0)
                return null;

            var updates = new List<PetInventoryItem>();
            foreach (var slot in slots)
            {
                var item = snapshot.PetItems.Find(x => x.SlotIndex == slot);
                if (item != null)
                    updates.Add(item);
            }

            if (updates.Count == 0)
                return null;

            return ItemListUpdateBuilder.BuildPetUpdates(updates);
        }

        private static void WriteCompactItemListUpdate(GamePacketWriter writer, InventoryMutationResult update)
        {
            var itemTemplateId = update.RemainingStackCount > 0 ? update.ItemTemplateId : 0;
            var remainingStackCount = update.RemainingStackCount > 0 ? update.RemainingStackCount : 0;
            writer.WriteInt16(update.SlotIndex);
            writer.WriteInt32(itemTemplateId);
            writer.WriteInt32(remainingStackCount);
            writer.WriteZeroBytes(0x4A);
        }

        private static byte[] BuildCeraUpdate(int cera, int tokenCera, int happyTokenCera)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);       // [0]    ok
            writer.WriteInt32(cera);   // [1..4] cera point
            writer.WriteInt32(tokenCera);      // [5..8] token/event cera point
            writer.WriteInt32(happyTokenCera); // [9..12] happy token/event cera point
            return writer.ToArray();
        }

    }
}
