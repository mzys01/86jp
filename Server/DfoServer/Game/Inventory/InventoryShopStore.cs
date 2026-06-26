using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryShopStore
    {
        private readonly ScopedStoreContext _context;
        private readonly InventoryDbPrimitives _db;
        private readonly InventoryAuditLogger _auditLogger;

        internal InventoryShopStore(ScopedStoreContext context, InventoryDbPrimitives db, InventoryAuditLogger auditLogger)
        {
            _context = context;
            _db = db;
            _auditLogger = auditLogger;
        }

        // 点券支付模式: Default=欢乐券→代币券→点券瀑布; OnlyCera=仅点券; OnlyCeraPoint=仅欢乐券+代币券。
        private enum CeraPayMode
        {
            Default,
            OnlyCera,
            OnlyCeraPoint,
        }

        private struct CeraShopPaymentPlan
        {
            public bool Ok;
            public int NewGold;
            public int NewCera;
            public int NewTokenCera;
            public int NewHappyTokenCera;
        }

        private static InventoryMutationResult ToInventoryMutationResult(BoosterRewardResult reward)
        {
            return new InventoryMutationResult
            {
                ListType = reward.ListType,
                SlotIndex = reward.SlotIndex,
                ItemTemplateId = reward.ItemTemplateId,
                RemainingStackCount = reward.StackCount,
                InstanceValue = reward.StackCount,
                AppliedCount = (short)Math.Min(short.MaxValue, Math.Max(1, reward.GrantedCount)),
                RequestedCount = (short)Math.Min(short.MaxValue, Math.Max(1, reward.GrantedCount)),
            };
        }

        // 计算扣费后的各币余额。金币与点券分别结算; 点券按 mode 在 {欢乐券, 代币券, 点券} 内瀑布扣减。
        // 若任一币种(在允许的币池内)余额不足, 返回 Ok=false 且不改动余额。
        private static CeraShopPaymentPlan ComputeCeraShopPayment(SqliteInventoryStore.WalletState w, int goldCost, int ceraCost, CeraPayMode mode)
        {
            var plan = new CeraShopPaymentPlan
            {
                Ok = false,
                NewGold = w.Gold,
                NewCera = w.Coin,
                NewTokenCera = w.TokenCera,
                NewHappyTokenCera = w.HappyTokenCera,
            };

            if (goldCost > 0)
            {
                if (w.Gold < goldCost)
                    return plan;
                plan.NewGold = w.Gold - goldCost;
            }

            if (ceraCost > 0)
            {
                var useHappy = mode != CeraPayMode.OnlyCera;          // OnlyCera 不能用欢乐/代币券
                var useToken = mode != CeraPayMode.OnlyCera;
                var useCera = mode != CeraPayMode.OnlyCeraPoint;       // OnlyCeraPoint 不能用点券

                var remaining = ceraCost;
                int happy = plan.NewHappyTokenCera, token = plan.NewTokenCera, cera = plan.NewCera;
                if (useHappy && remaining > 0) { var t = Math.Min(remaining, happy); happy -= t; remaining -= t; }
                if (useToken && remaining > 0) { var t = Math.Min(remaining, token); token -= t; remaining -= t; }
                if (useCera && remaining > 0) { var t = Math.Min(remaining, cera); cera -= t; remaining -= t; }
                if (remaining > 0)
                    return plan; // 允许的币池内不够付

                plan.NewHappyTokenCera = happy;
                plan.NewTokenCera = token;
                plan.NewCera = cera;
            }

            plan.Ok = true;
            return plan;
        }

        // 落库: 把四种货币的扣费后余额写入。
        private void ApplyCeraShopPayment(SqliteConnection connection, SqliteTransaction transaction, CeraShopPaymentPlan plan)
        {
            CurrencyService.UpdateGold(connection, transaction, _context.CharacterId, plan.NewGold);
            CurrencyService.UpdateCera(connection, transaction, _context.CharacterId, plan.NewCera);
            CurrencyService.UpdateTokenCera(connection, transaction, _context.CharacterId, plan.NewTokenCera);
            CurrencyService.UpdateHappyTokenCera(connection, transaction, _context.CharacterId, plan.NewHappyTokenCera);
        }

        public bool TryBuyItem(int itemTemplateId, int buyCount, out InventoryMutationResult result)
        {
            result = null;
            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata.ItemKind == "special")
                return false;

            if (!SqliteInventoryStore.CanMoveToListType(metadata.ItemKind, InventoryListType.Main))
                return false;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                if (metadata.IsMaterialExchange)
                {
                    var wallet = _db.LoadWallet(connection, transaction);
                    var totalGoldCost = metadata.BuyGold * buyCount;
                    var totalMaterialCost = metadata.NeedMaterialCount * buyCount;
                    if (wallet.Gold < totalGoldCost)
                    {
                        FileLogger.Log($"  [BuyItem] REJECT: need {totalGoldCost} gold, have {wallet.Gold}");
                        return false;
                    }

                    var materialItem = _db.FindItemByTemplateId(connection, transaction, InventoryListType.Main, metadata.NeedMaterialId);
                    if (materialItem == null || materialItem.StackCount < totalMaterialCost)
                    {
                        FileLogger.Log($"  [BuyItem] REJECT: need {totalMaterialCost}x item {metadata.NeedMaterialId}, have {materialItem?.StackCount ?? 0}");
                        return false;
                    }

                    short matTargetSlot;
                    var targetItem = _db.FindItemByTemplateId(connection, transaction, InventoryListType.Main, itemTemplateId);
                    if (targetItem == null)
                    {
                        int matSlotStart, matSlotEnd;
                        metadata.GetSlotRange(out matSlotStart, out matSlotEnd);
                        var emptySlot = _db.FindEmptySlot(connection, transaction, InventoryListType.Main, matSlotStart, matSlotEnd);
                        if (emptySlot < 0)
                        {
                            FileLogger.Log($"  [BuyItem] REJECT: no empty slot for material exchange item {itemTemplateId}");
                            return false;
                        }
                        _db.UpdateStackCount(connection, transaction, materialItem.ItemUid, materialItem.StackCount - totalMaterialCost);
                        _db.InsertCharacterItem(connection, transaction, InventoryListType.Main, (short)emptySlot,
                            itemTemplateId, metadata.ItemKind, buyCount, buyCount,
                            metadata.Durability, 0, 0, 0, 0, 0, "{}");
                        matTargetSlot = (short)emptySlot;
                    }
                    else
                    {
                        _db.UpdateStackCount(connection, transaction, materialItem.ItemUid, materialItem.StackCount - totalMaterialCost);
                        _db.UpdateStackCount(connection, transaction, targetItem.ItemUid, targetItem.StackCount + buyCount);
                        matTargetSlot = targetItem.SlotIndex;
                    }
                    var newMaterialCount = materialItem.StackCount - totalMaterialCost;

                    if (totalGoldCost > 0)
                        _db.UpdateWallet(connection, transaction, wallet.Gold - totalGoldCost, wallet.Coin);
                    var goldAfterBuy = wallet.Gold - totalGoldCost;
                    _auditLogger.WriteBuyAuditLog(connection, transaction, itemTemplateId, matTargetSlot, totalGoldCost, 0);
                    transaction.Commit();

                    result = new InventoryMutationResult
                    {
                        ListType = InventoryListType.Main,
                        SlotIndex = matTargetSlot,
                        ItemTemplateId = itemTemplateId,
                        RemainingStackCount = buyCount,
                        InstanceValue = buyCount,
                        Durability = 0,
                        UpdatedGold = goldAfterBuy,
                        UpdatedSp = wallet.Sp,
                        UpdatedCoin = wallet.Coin,
                        RequestedCount = (short)buyCount,
                        AppliedCount = (short)buyCount,
                        CostItemTemplateId = metadata.NeedMaterialId,
                        CostItemNewStackCount = newMaterialCount,
                        CostItemSlotIndex = materialItem.SlotIndex,
                    };
                    return true;
                }

                var walletCheck = _db.LoadWallet(connection, transaction);
                if (walletCheck.Gold < metadata.BuyGold || walletCheck.Coin < metadata.BuyCoin)
                    return false;

                // For stackable items, try to stack onto existing item first
                if (metadata.IsStackable)
                {
                    var existingItem = _db.FindItemByTemplateId(connection, transaction, InventoryListType.Main, itemTemplateId);
                    if (existingItem != null)
                    {
                        var totalCostGold = metadata.BuyGold * buyCount;
                        var totalCostCoin = metadata.BuyCoin * buyCount;
                        if (walletCheck.Gold < totalCostGold || walletCheck.Coin < totalCostCoin)
                            return false;
                        _db.UpdateStackCount(connection, transaction, existingItem.ItemUid, existingItem.StackCount + buyCount);
                        var updGold = walletCheck.Gold - totalCostGold;
                        var updCoin = walletCheck.Coin - totalCostCoin;
                        if (totalCostGold > 0 || totalCostCoin > 0)
                            _db.UpdateWallet(connection, transaction, updGold, updCoin);
                        _auditLogger.WriteBuyAuditLog(connection, transaction, itemTemplateId, existingItem.SlotIndex, totalCostGold, totalCostCoin);
                        transaction.Commit();

                        result = new InventoryMutationResult
                        {
                            ListType = InventoryListType.Main,
                            SlotIndex = existingItem.SlotIndex,
                            ItemTemplateId = itemTemplateId,
                            RemainingStackCount = buyCount,
                            InstanceValue = buyCount,
                            Durability = 0,
                            UpdatedGold = updGold,
                            UpdatedSp = walletCheck.Sp,
                            UpdatedCoin = updCoin,
                            RequestedCount = (short)buyCount,
                            AppliedCount = (short)buyCount,
                        };
                        return true;
                    }
                }

                var effectiveCount = metadata.IsStackable ? buyCount : 1;
                var totalBuyGold = metadata.BuyGold * effectiveCount;
                var totalBuyCoin = metadata.BuyCoin * effectiveCount;
                if (walletCheck.Gold < totalBuyGold || walletCheck.Coin < totalBuyCoin)
                    return false;

                int slotStart, slotEnd;
                metadata.GetSlotRange(out slotStart, out slotEnd);
                var targetSlot = _db.FindEmptySlot(connection, transaction, InventoryListType.Main, slotStart, slotEnd);
                if (targetSlot < 0)
                    return false;

                var qualitySeed = InventoryDbPrimitives.GenerateInstanceValue(itemTemplateId, targetSlot);
                var buyStackCount = metadata.IsStackable ? effectiveCount : qualitySeed;
                var buyInstanceValue = metadata.IsStackable ? effectiveCount : qualitySeed;
                _db.InsertCharacterItem(
                    connection,
                    transaction,
                    InventoryListType.Main,
                    (short)targetSlot,
                    itemTemplateId,
                    metadata.ItemKind,
                    buyStackCount,
                    buyInstanceValue,
                    metadata.Durability,
                    0,
                    0,
                    0,
                    metadata.IsStackable ? 0 : -1,
                    0,
                    "{}");

                var updatedGold = walletCheck.Gold - totalBuyGold;
                var updatedCoin = walletCheck.Coin - totalBuyCoin;
                _db.UpdateWallet(connection, transaction, updatedGold, updatedCoin);
                _auditLogger.WriteBuyAuditLog(connection, transaction, itemTemplateId, (short)targetSlot, totalBuyGold, totalBuyCoin);
                transaction.Commit();

                result = new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = (short)targetSlot,
                    ItemTemplateId = itemTemplateId,
                    RemainingStackCount = effectiveCount,
                    InstanceValue = buyInstanceValue,
                    Durability = metadata.Durability,
                    UpdatedGold = updatedGold,
                    UpdatedSp = walletCheck.Sp,
                    UpdatedCoin = updatedCoin,
                    RequestedCount = (short)effectiveCount,
                    AppliedCount = (short)effectiveCount,
                };
                return true;
            }
        }

        public bool TrySellItem(InventoryListType listType, short slotIndex, short sellCount, out InventoryMutationResult result)
        {
            result = null;
            if (!SqliteInventoryStore.IsSupportedDeleteOrSellListType(listType))
            {
                FileLogger.Log($"  [SellItem] REJECT: unsupported listType={listType}");
                return false;
            }

            var dbListType = SqliteInventoryStore.MapToDbListType(listType);
            FileLogger.Log($"  [SellItem] wireListType={listType} dbListType={dbListType} slot={slotIndex} count={sellCount}");

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var item = _db.LoadItemRecord(connection, transaction, dbListType, slotIndex);
                if (item == null)
                {
                    FileLogger.Log($"  [SellItem] FAIL: no item at dbListType={dbListType} slot={slotIndex}");
                    return false;
                }

                var metadata = ItemMetadataResolver.Resolve(item.ItemTemplateId);
                var appliedCount = SqliteInventoryStore.NormalizeRemovalCount(item, sellCount);
                if (item.ItemKind == "stackable" && appliedCount < item.StackCount)
                {
                    _db.UpdateStackCount(connection, transaction, item.ItemUid, item.StackCount - appliedCount);
                }
                else
                {
                    _db.DeleteItem(connection, transaction, item.ItemUid);
                }

                var wallet = _db.LoadWallet(connection, transaction);
                var goldDelta = metadata.SellGold * appliedCount;
                var updatedGold = wallet.Gold + goldDelta;
                _db.UpdateWallet(connection, transaction, updatedGold, wallet.Coin);
                _auditLogger.WriteSellAuditLog(connection, transaction, item, appliedCount, goldDelta);
                transaction.Commit();

                result = new InventoryMutationResult
                {
                    ListType = listType,
                    SlotIndex = slotIndex,
                    ItemTemplateId = item.ItemTemplateId,
                    RemainingStackCount = Math.Max(0, item.StackCount - appliedCount),
                    InstanceValue = item.InstanceValue,
                    Durability = item.Durability,
                    UpdatedGold = updatedGold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = wallet.Coin,
                    RequestedCount = sellCount,
                    AppliedCount = (short)appliedCount,
                };
                return true;
            }
        }

        public bool TryBuyCeraShopItem(int productId, int buyCount, out InventoryMutationResult result)
        {
            result = null;
            FileLogger.Log($"  [CeraShopBuy] clientProductId=0x{productId:X8} ({productId}) buyCount={buyCount}");

            if (buyCount <= 0)
                buyCount = 1;
            if (buyCount > 999)
                buyCount = 999;

            if (!CeraShopProductCatalog.TryResolve(productId, out var product))
            {
                FileLogger.Log($"  [CeraShopBuy] REJECT: product 0x{productId:X8} not found in cerashop.etc");
                return false;
            }

            var itemTemplateId = product.ItemTemplateId;
            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata.ItemKind == "special")
            {
                FileLogger.Log($"  [CeraShopBuy] REJECT: product=0x{productId:X8} maps to unsupported item=0x{itemTemplateId:X8} section={product.Section}");
                return false;
            }

            var itemKind = metadata.ItemKind;
            var isStackable = metadata.IsStackable;
            // 限时时装(avatar): cerashop 第3字段(product.Count)= 时长档位(1-based),
            // 时长(天)与点券价取自时装 .equ 的 [avatar type select]。
            var isAvatar = string.Equals(product.Section, "avatar", StringComparison.OrdinalIgnoreCase);
            // 宠物(creature): 装备类且 .equ 的 [equipment type]=[creature], 应进专用宠物栏(Pet 列表 7),
            // 而不是主背包装备格。判定基于物品本身的 equipment type, 不依赖 cerashop 段名(段内还混有可堆叠饲料)。
            var isCreature = !isAvatar && string.Equals(itemKind, "equipment", StringComparison.Ordinal) && SqliteInventoryStore.IsCreatureItem(itemTemplateId);
            var avatarDurationDays = 0;
            // 发货数量 = 份数 × 每份数量(cerashop count); 价格 = 每份价 × 份数 (avatar 恒为 1)
            var effectiveCount = (isStackable && !isAvatar) ? Math.Min(999, buyCount * Math.Max(1, product.Count)) : 1;
            // 价格来自 cerashop 三列: 金币 / 胜点(忽略) / 点券。金币与点券一般互斥(只一个非 0)。
            var goldPrice = Math.Max(0, product.GoldPrice);
            var ceraPrice = Math.Max(0, product.CoinPrice);
            if (isAvatar)
            {
                goldPrice = 0; // 时装走点券
                if (AvatarTypeSelectResolver.TryGetOption(itemTemplateId, Math.Max(1, product.Count), out var durDays, out var avatarPrice))
                {
                    avatarDurationDays = durDays;
                    if (avatarPrice > 0)
                        ceraPrice = avatarPrice;
                    FileLogger.Log($"  [CeraShopBuy] avatar item=0x{itemTemplateId:X8} durIndex={product.Count} -> durationDays={durDays} ceraPrice={avatarPrice}");
                }
                else
                {
                    FileLogger.Log($"  [CeraShopBuy] WARN: avatar item=0x{itemTemplateId:X8} 无 [avatar type select] 档位 {product.Count}, 点券价沿用 {ceraPrice}");
                }
            }
            var perUnit = Math.Max(1, buyCount);
            var totalGoldLong = (long)goldPrice * perUnit;
            var totalCeraLong = (long)ceraPrice * perUnit;
            if (totalGoldLong > int.MaxValue || totalCeraLong > int.MaxValue)
            {
                FileLogger.Log($"  [CeraShopBuy] REJECT: cost overflow product=0x{productId:X8} item=0x{itemTemplateId:X8} gold={goldPrice} cera={ceraPrice} buyCount={buyCount}");
                return false;
            }
            var totalGoldCost = (int)totalGoldLong;
            var totalCeraCost = (int)totalCeraLong;
            // 点券支付方式: buy only cera=仅点券; buy only cera point=仅欢乐券+代币券; 否则瀑布(欢乐→代币→点券)。
            var ceraMode = CeraShopProductCatalog.IsBuyOnlyCera(itemTemplateId) ? CeraPayMode.OnlyCera
                : CeraShopProductCatalog.IsBuyOnlyCeraPoint(itemTemplateId) ? CeraPayMode.OnlyCeraPoint
                : CeraPayMode.Default;

            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var wallet = _db.LoadWallet(connection, transaction);
                var plan = ComputeCeraShopPayment(wallet, totalGoldCost, totalCeraCost, ceraMode);
                FileLogger.Log($"  [CeraShopBuy] product=0x{productId:X8} -> item=0x{itemTemplateId:X8} section={product.Section} kind={itemKind} count={effectiveCount} gold={totalGoldCost} cera={totalCeraCost} mode={ceraMode} wallet(g={wallet.Gold},c={wallet.Coin},t={wallet.TokenCera},h={wallet.HappyTokenCera}) ok={plan.Ok}");
                if (!plan.Ok)
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: insufficient funds gold={totalGoldCost} cera={totalCeraCost} mode={ceraMode}");
                    return false;
                }
                var goldSpent = totalGoldCost > 0;

                if (InventoryPackageStore.TryResolveMallAutoOpenRewards(itemTemplateId, out var autoOpenRewards))
                {
                    var openedResults = new List<InventoryMutationResult>();
                    for (var openIndex = 0; openIndex < effectiveCount; openIndex++)
                    {
                        foreach (var reward in autoOpenRewards)
                        {
                            if (!_db.TryAddBoosterRewardItem(connection, transaction, reward.ItemId, reward.Count, out var rewardResult))
                            {
                                FileLogger.Log($"  [CeraShopBuy] auto-open failed source=0x{itemTemplateId:X8} reward=0x{reward.ItemId:X8} count={reward.Count}");
                                return false;
                            }

                            openedResults.Add(ToInventoryMutationResult(rewardResult));
                        }
                    }

                    if (openedResults.Count == 0)
                        return false;

                    ApplyCeraShopPayment(connection, transaction, plan);
                    _auditLogger.WriteBuyAuditLog(connection, transaction, itemTemplateId, 0, totalGoldCost, totalCeraCost);
                    foreach (var rewardResult in openedResults)
                        _auditLogger.WriteBuyAuditLog(connection, transaction, rewardResult.ItemTemplateId, rewardResult.SlotIndex, 0, 0);
                    transaction.Commit();

                    result = openedResults[0];
                    result.UpdatedGold = plan.NewGold;
                    result.UpdatedSp = wallet.Sp;
                    result.UpdatedCoin = plan.NewCera;
                    result.UpdatedTokenCera = plan.NewTokenCera;
                    result.UpdatedHappyTokenCera = plan.NewHappyTokenCera;
                    result.GoldSpent = goldSpent;
                    result.RequestedCount = (short)Math.Min(short.MaxValue, effectiveCount);
                    result.AppliedCount = (short)Math.Min(short.MaxValue, effectiveCount);
                    for (var i = 1; i < openedResults.Count; i++)
                        result.ExtraResults.Add(openedResults[i]);

                    FileLogger.Log($"  [CeraShopBuy] auto-open source=0x{itemTemplateId:X8} rewards={string.Join(",", openedResults.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.RemainingStackCount}@{r.SlotIndex}"))}");
                    return true;
                }

                if (isStackable)
                {
                    var existingItem = _db.FindItemByTemplateId(connection, transaction, InventoryListType.Main, itemTemplateId);
                    var stackLimit = metadata.StackLimit;
                    if (existingItem != null && (stackLimit <= 0 || existingItem.StackCount + effectiveCount <= stackLimit))
                    {
                        var newStackCount = existingItem.StackCount + effectiveCount;
                        _db.UpdateStackCount(connection, transaction, existingItem.ItemUid, newStackCount);
                        ApplyCeraShopPayment(connection, transaction, plan);
                        _auditLogger.WriteBuyAuditLog(connection, transaction, itemTemplateId, existingItem.SlotIndex, totalGoldCost, totalCeraCost);
                        transaction.Commit();

                        result = new InventoryMutationResult
                        {
                            ListType = InventoryListType.Main,
                            SlotIndex = existingItem.SlotIndex,
                            ItemTemplateId = itemTemplateId,
                            RemainingStackCount = newStackCount,
                            InstanceValue = newStackCount,
                            Durability = 0,
                            UpdatedGold = plan.NewGold,
                            UpdatedSp = wallet.Sp,
                            UpdatedCoin = plan.NewCera,
                            UpdatedTokenCera = plan.NewTokenCera,
                            UpdatedHappyTokenCera = plan.NewHappyTokenCera,
                            GoldSpent = goldSpent,
                            RequestedCount = (short)effectiveCount,
                            AppliedCount = (short)effectiveCount,
                        };
                        return true;
                    }
                }

                int slotStart;
                int slotEnd;
                var insertListType = InventoryListType.Main;
                var insertKind = itemKind;
                var expireTime = isStackable ? 0 : -1;   // -1 = 永久(装备/永久时装)
                if (isAvatar)
                {
                    insertListType = InventoryListType.Avatar;  // 时装进时装库存, 不进主库存装备槽
                    insertKind = "avatar";
                    slotStart = 0;
                    slotEnd = 500;
                    if (avatarDurationDays > 0)
                    {
                        var unixNow = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                        expireTime = (int)Math.Min(int.MaxValue, unixNow + (long)avatarDurationDays * 86400L);
                    }
                }
                else if (isCreature)
                {
                    insertListType = InventoryListType.Pet;  // 宠物进专用宠物栏(list 7), 不进主背包装备格
                    insertKind = "pet";
                    slotStart = SqliteInventoryStore.PetInventorySlotStart;
                    slotEnd = SqliteInventoryStore.PetInventorySlotEnd;
                    expireTime = 0;
                }
                else
                {
                    metadata.GetSlotRange(out slotStart, out slotEnd);
                }

                var targetSlot = _db.FindEmptySlot(connection, transaction, insertListType, slotStart, slotEnd);
                if (targetSlot < 0)
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: no empty slot product=0x{productId:X8} item=0x{itemTemplateId:X8} list={insertListType} slotRange={slotStart}-{slotEnd}");
                    return false;
                }

                var petSerial = isCreature ? _db.NextPetSerialOrHandle(connection, transaction) : 0;
                var instanceValue = isStackable ? effectiveCount : (isCreature || isAvatar ? 0 : InventoryDbPrimitives.GenerateInstanceValue(itemTemplateId, targetSlot));
                var durability = (isAvatar || isCreature) ? (ushort)0 : metadata.Durability;
                if (isAvatar)
                {
                    var avatarItem = SqliteInventoryStore.CreateDefaultAvatarItem((short)targetSlot, itemTemplateId, 0);
                    _db.InsertCharacterItem(
                        connection,
                        transaction,
                        insertListType,
                        avatarItem.SlotIndex,
                        avatarItem.AvatarItemId,
                        insertKind,
                        0,
                        0,
                        durability,
                        0,
                        avatarItem.OptionValue,
                        expireTime,
                        avatarItem.UnknownFixed30,
                        0,
                        InventoryItemCodec.SerializeAvatar(avatarItem));
                }
                else
                {
                    _db.InsertCharacterItem(
                        connection,
                        transaction,
                        insertListType,
                        (short)targetSlot,
                        itemTemplateId,
                        insertKind,
                        isCreature ? 0 : effectiveCount,
                        instanceValue,
                        durability,
                        0,
                        0,
                        expireTime,
                        0,
                        petSerial,
                        "{}");
                }

                ApplyCeraShopPayment(connection, transaction, plan);
                _auditLogger.WriteBuyAuditLog(connection, transaction, itemTemplateId, (short)targetSlot, totalGoldCost, totalCeraCost);
                transaction.Commit();

                result = new InventoryMutationResult
                {
                    ListType = insertListType,
                    SlotIndex = (short)targetSlot,
                    ItemTemplateId = itemTemplateId,
                    RemainingStackCount = effectiveCount,
                    InstanceValue = instanceValue,
                    Durability = durability,
                    UpdatedGold = plan.NewGold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = plan.NewCera,
                    UpdatedTokenCera = plan.NewTokenCera,
                    UpdatedHappyTokenCera = plan.NewHappyTokenCera,
                    GoldSpent = goldSpent,
                    RequestedCount = (short)effectiveCount,
                    AppliedCount = (short)effectiveCount,
                };
                return true;
            }
        }
    }
}
