using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryShopStore
    {
        private readonly InventoryDbPrimitives _db;
        private readonly InventoryAuditLogger _auditLogger;

        internal InventoryShopStore(InventoryDbPrimitives db, InventoryAuditLogger auditLogger)
        {
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
        private void ApplyCeraShopPayment(SqliteConnection connection, SqliteTransaction transaction, int characterId, CeraShopPaymentPlan plan)
        {
            CurrencyService.UpdateGold(connection, transaction, characterId, plan.NewGold);
            CurrencyService.UpdateCera(connection, transaction, characterId, plan.NewCera);
            CurrencyService.UpdateTokenCera(connection, transaction, characterId, plan.NewTokenCera);
            CurrencyService.UpdateHappyTokenCera(connection, transaction, characterId, plan.NewHappyTokenCera);
        }

        public bool TryBuyItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, int itemTemplateId, int buyCount, out InventoryMutationResult result)
        {
            result = null;
            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata.ItemKind == "special")
                return false;

            if (!SqliteInventoryStore.CanMoveToListType(metadata.ItemKind, InventoryListType.Main))
                return false;

            // NPC shops can sell pet tab items too; route them to the same category slot ranges as cera/package rewards.
            var isPetConsumable = ItemMetadataResolver.IsPetConsumableItem(metadata);
            var isPetEquipment = string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal) &&
                ItemMetadataResolver.IsPetInventoryEquipment(itemTemplateId);
            var isCreature = isPetEquipment && SqliteInventoryStore.IsCreatureItem(itemTemplateId);
            var isPetArtifactEquipment = isPetEquipment && !isCreature;
            var targetListType = isCreature || isPetArtifactEquipment || isPetConsumable
                ? InventoryListType.Pet
                : InventoryListType.Main;
            var targetItemKind = targetListType == InventoryListType.Pet ? "pet" : metadata.ItemKind;

            if (CurrencyService.IsCubeFragment(itemTemplateId))
            {
                var wallet = _db.LoadWallet(connection, transaction, characterId);
                var totalGoldCost = metadata.BuyGold * buyCount;
                if (wallet.Gold < totalGoldCost)
                    return false;

                int materialNewCount = -1;
                short materialSlotIndex = -1;
                if (metadata.IsMaterialExchange)
                {
                    var totalMaterialCost = metadata.NeedMaterialCount * buyCount;
                    if (CurrencyService.IsCubeFragment(metadata.NeedMaterialId))
                    {
                        var cubes = CurrencyService.LoadCubeFragments(connection, transaction, accountId);
                        var have = 0;
                        foreach (var c in cubes)
                            if (c.ItemId == metadata.NeedMaterialId) have = c.Count;
                        if (have < totalMaterialCost)
                            return false;
                        CurrencyService.AddCubeFragment(connection, transaction, accountId, metadata.NeedMaterialId, -totalMaterialCost);
                        materialNewCount = have - totalMaterialCost;
                        materialSlotIndex = (short)CurrencyService.GetCubeFragmentSlot(metadata.NeedMaterialId);
                    }
                    else
                    {
                        var materialItem = _db.FindItemByTemplateId(connection, transaction, characterId, InventoryListType.Main, metadata.NeedMaterialId);
                        if (materialItem == null || materialItem.StackCount < totalMaterialCost)
                            return false;
                        _db.UpdateStackCount(connection, transaction, materialItem.ItemUid, materialItem.StackCount - totalMaterialCost);
                        materialNewCount = materialItem.StackCount - totalMaterialCost;
                        materialSlotIndex = materialItem.SlotIndex;
                    }
                }

                if (totalGoldCost > 0)
                    _db.UpdateWallet(connection, transaction, characterId, wallet.Gold - totalGoldCost, wallet.Coin);
                CurrencyService.AddCubeFragment(connection, transaction, accountId, itemTemplateId, buyCount);
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, (short)CurrencyService.GetCubeFragmentSlot(itemTemplateId), totalGoldCost, 0);
                result = new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = (short)CurrencyService.GetCubeFragmentSlot(itemTemplateId),
                    ItemTemplateId = itemTemplateId,
                    RemainingStackCount = buyCount,
                    InstanceValue = buyCount,
                    UpdatedGold = wallet.Gold - totalGoldCost,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = wallet.Coin,
                    RequestedCount = (short)buyCount,
                    AppliedCount = (short)buyCount,
                    CostItemTemplateId = metadata.IsMaterialExchange ? metadata.NeedMaterialId : 0,
                    CostItemNewStackCount = materialNewCount,
                    CostItemSlotIndex = materialSlotIndex,
                };
                return true;
            }

            if (metadata.IsMaterialExchange)
            {
                var wallet = _db.LoadWallet(connection, transaction, characterId);
                var totalGoldCost = metadata.BuyGold * buyCount;
                var totalMaterialCost = metadata.NeedMaterialCount * buyCount;
                if (wallet.Gold < totalGoldCost)
                {
                    FileLogger.Log($"  [BuyItem] REJECT: need {totalGoldCost} gold, have {wallet.Gold}");
                    return false;
                }

                var materialItem = _db.FindItemByTemplateId(connection, transaction, characterId, InventoryListType.Main, metadata.NeedMaterialId);
                if (materialItem == null || materialItem.StackCount < totalMaterialCost)
                {
                    FileLogger.Log($"  [BuyItem] REJECT: need {totalMaterialCost}x item {metadata.NeedMaterialId}, have {materialItem?.StackCount ?? 0}");
                    return false;
                }

                short matTargetSlot;
                var targetItem = isCreature || isPetArtifactEquipment
                    ? null
                    : _db.FindItemByTemplateId(connection, transaction, characterId, targetListType, itemTemplateId);
                if (targetItem == null)
                {
                    int matSlotStart, matSlotEnd;
                    if (isCreature)
                    {
                        matSlotStart = SqliteInventoryStore.PetInventorySlotStart;
                        matSlotEnd = SqliteInventoryStore.PetInventorySlotEnd;
                    }
                    else if (isPetArtifactEquipment)
                    {
                        matSlotStart = SqliteInventoryStore.PetEquipmentSlotStart;
                        matSlotEnd = SqliteInventoryStore.PetEquipmentSlotEnd;
                    }
                    else if (isPetConsumable)
                    {
                        matSlotStart = SqliteInventoryStore.PetConsumableSlotStart;
                        matSlotEnd = SqliteInventoryStore.PetConsumableSlotEnd;
                    }
                    else
                    {
                        metadata.GetSlotRange(out matSlotStart, out matSlotEnd);
                    }

                    var emptySlot = _db.FindEmptySlot(connection, transaction, characterId, targetListType, matSlotStart, matSlotEnd);
                    if (emptySlot < 0)
                    {
                        FileLogger.Log($"  [BuyItem] REJECT: no empty slot for material exchange item {itemTemplateId}");
                        return false;
                    }
                    _db.UpdateStackCount(connection, transaction, materialItem.ItemUid, materialItem.StackCount - totalMaterialCost);
                    _db.InsertCharacterItem(connection, transaction, characterId, targetListType, (short)emptySlot,
                        itemTemplateId, targetItemKind, isCreature || isPetArtifactEquipment ? 0 : buyCount, isPetEquipment ? 0 : buyCount,
                        targetListType == InventoryListType.Pet ? (ushort)0 : metadata.Durability, 0, 0, 0, 0,
                        isPetConsumable ? buyCount : isCreature ? _db.NextPetSerialOrHandle(connection, transaction, characterId) : 0,
                        "{}");
                    matTargetSlot = (short)emptySlot;
                }
                else
                {
                    _db.UpdateStackCount(connection, transaction, materialItem.ItemUid, materialItem.StackCount - totalMaterialCost);
                    if (isPetConsumable)
                        _db.UpdatePetStackCount(connection, transaction, targetItem.ItemUid, targetItem.StackCount + buyCount);
                    else
                        _db.UpdateStackCount(connection, transaction, targetItem.ItemUid, targetItem.StackCount + buyCount);
                    matTargetSlot = targetItem.SlotIndex;
                }
                var newMaterialCount = materialItem.StackCount - totalMaterialCost;

                if (totalGoldCost > 0)
                    _db.UpdateWallet(connection, transaction, characterId, wallet.Gold - totalGoldCost, wallet.Coin);
                var goldAfterBuy = wallet.Gold - totalGoldCost;
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, matTargetSlot, totalGoldCost, 0);

                result = new InventoryMutationResult
                {
                    ListType = targetListType,
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

            var walletCheck = _db.LoadWallet(connection, transaction, characterId);
            if (walletCheck.Gold < metadata.BuyGold || walletCheck.Coin < metadata.BuyCoin)
                return false;

            // For stackable items, try to stack onto existing item first
            if (metadata.IsStackable)
            {
                var existingItem = _db.FindItemByTemplateId(connection, transaction, characterId, targetListType, itemTemplateId);
                if (existingItem != null)
                {
                    var totalCostGold = metadata.BuyGold * buyCount;
                    var totalCostCoin = metadata.BuyCoin * buyCount;
                    if (walletCheck.Gold < totalCostGold || walletCheck.Coin < totalCostCoin)
                        return false;
                    var newStackCount = existingItem.StackCount + buyCount;
                    if (isPetConsumable)
                        _db.UpdatePetStackCount(connection, transaction, existingItem.ItemUid, newStackCount);
                    else
                        _db.UpdateStackCount(connection, transaction, existingItem.ItemUid, newStackCount);
                    var updGold = walletCheck.Gold - totalCostGold;
                    var updCoin = walletCheck.Coin - totalCostCoin;
                    if (totalCostGold > 0 || totalCostCoin > 0)
                        _db.UpdateWallet(connection, transaction, characterId, updGold, updCoin);
                    _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, existingItem.SlotIndex, totalCostGold, totalCostCoin);

                    result = new InventoryMutationResult
                    {
                        ListType = targetListType,
                        SlotIndex = existingItem.SlotIndex,
                        ItemTemplateId = itemTemplateId,
                        RemainingStackCount = newStackCount,
                        InstanceValue = newStackCount,
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
            if (isCreature)
            {
                slotStart = SqliteInventoryStore.PetInventorySlotStart;
                slotEnd = SqliteInventoryStore.PetInventorySlotEnd;
            }
            else if (isPetArtifactEquipment)
            {
                slotStart = SqliteInventoryStore.PetEquipmentSlotStart;
                slotEnd = SqliteInventoryStore.PetEquipmentSlotEnd;
            }
            else if (isPetConsumable)
            {
                slotStart = SqliteInventoryStore.PetConsumableSlotStart;
                slotEnd = SqliteInventoryStore.PetConsumableSlotEnd;
            }
            else
            {
                metadata.GetSlotRange(out slotStart, out slotEnd);
            }

            var targetSlot = _db.FindEmptySlot(connection, transaction, characterId, targetListType, slotStart, slotEnd);
            if (targetSlot < 0)
                return false;

            var qualitySeed = targetListType == InventoryListType.Pet ? 0 : InventoryDbPrimitives.GenerateInstanceValue(itemTemplateId, targetSlot);
            var buyStackCount = isPetEquipment ? 0 : metadata.IsStackable ? effectiveCount : qualitySeed;
            var buyInstanceValue = metadata.IsStackable ? effectiveCount : qualitySeed;
            var buyDurability = targetListType == InventoryListType.Pet ? (ushort)0 : metadata.Durability;
            var buyPetSerial = isPetConsumable ? effectiveCount : isCreature ? _db.NextPetSerialOrHandle(connection, transaction, characterId) : 0;
            _db.InsertCharacterItem(
                connection,
                transaction,
                characterId,
                targetListType,
                (short)targetSlot,
                itemTemplateId,
                targetItemKind,
                buyStackCount,
                buyInstanceValue,
                buyDurability,
                0,
                0,
                targetListType == InventoryListType.Pet ? 0 : metadata.IsStackable ? 0 : -1,
                0,
                buyPetSerial,
                "{}");

            var updatedGold = walletCheck.Gold - totalBuyGold;
            var updatedCoin = walletCheck.Coin - totalBuyCoin;
            _db.UpdateWallet(connection, transaction, characterId, updatedGold, updatedCoin);
            _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, (short)targetSlot, totalBuyGold, totalBuyCoin);

            result = new InventoryMutationResult
            {
                ListType = targetListType,
                SlotIndex = (short)targetSlot,
                ItemTemplateId = itemTemplateId,
                RemainingStackCount = effectiveCount,
                InstanceValue = buyInstanceValue,
                Durability = buyDurability,
                UpdatedGold = updatedGold,
                UpdatedSp = walletCheck.Sp,
                UpdatedCoin = updatedCoin,
                RequestedCount = (short)effectiveCount,
                AppliedCount = (short)effectiveCount,
            };
            return true;
        }

        public bool TrySellItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, InventoryListType listType, short slotIndex, short sellCount, out InventoryMutationResult result)
        {
            result = null;
            if (!SqliteInventoryStore.IsSupportedDeleteOrSellListType(listType))
            {
                FileLogger.Log($"  [SellItem] REJECT: unsupported listType={listType}");
                return false;
            }

            var dbListType = SqliteInventoryStore.MapToDbListType(listType);
            FileLogger.Log($"  [SellItem] wireListType={listType} dbListType={dbListType} slot={slotIndex} count={sellCount}");

            var item = _db.LoadItemRecord(connection, transaction, characterId, dbListType, slotIndex);
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

            var wallet = _db.LoadWallet(connection, transaction, characterId);
            var goldDelta = metadata.SellGold * appliedCount;
            var updatedGold = wallet.Gold + goldDelta;
            _db.UpdateWallet(connection, transaction, characterId, updatedGold, wallet.Coin);
            _auditLogger.WriteSellAuditLog(connection, transaction, characterId, item, appliedCount, goldDelta);

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

        public bool TryBuyCeraShopItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, int productId, int buyCount, out InventoryMutationResult result)
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
            // Pet tab is split by client category slots: creature, artifact equipment, consumables.
            var isPetEquipment = !isAvatar && string.Equals(itemKind, "equipment", StringComparison.Ordinal) && ItemMetadataResolver.IsPetInventoryEquipment(itemTemplateId);
            var isCreature = isPetEquipment && SqliteInventoryStore.IsCreatureItem(itemTemplateId);
            var isPetArtifactEquipment = isPetEquipment && !isCreature;
            var isPetConsumable = !isAvatar && ItemMetadataResolver.IsPetConsumableItem(metadata);
            var stackListType = isPetConsumable ? InventoryListType.Pet : InventoryListType.Main;
            var avatarDurationDays = 0;
            // 发货数量 = 份数 × 每份数量(cerashop count); 价格 = 每份价 × 份数 (avatar 恒为 1)
            var effectiveCount = (isStackable && !isAvatar) ? Math.Min(999, buyCount * Math.Max(1, product.Count)) : 1;
            // 价格来自 cerashop 三列: 金币 / 胜点(忽略) / 点券。金币与点券一般互斥(只一个非 0)。
            var goldPrice = Math.Max(0, product.GoldPrice);
            var ceraPrice = Math.Max(0, product.CoinPrice);
            // Keep pet shop purchases in the slot range for the page where the client displays them.
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

            var wallet = _db.LoadWallet(connection, transaction, characterId);
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
                        if (!_db.TryAddBoosterRewardItem(connection, transaction, characterId, accountId, reward.ItemId, reward.Count, out var rewardResult))
                        {
                            FileLogger.Log($"  [CeraShopBuy] auto-open failed source=0x{itemTemplateId:X8} reward=0x{reward.ItemId:X8} count={reward.Count}");
                            return false;
                        }

                        openedResults.Add(ToInventoryMutationResult(rewardResult));
                    }
                }

                if (openedResults.Count == 0)
                    return false;

                ApplyCeraShopPayment(connection, transaction, characterId, plan);
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, 0, totalGoldCost, totalCeraCost);
                foreach (var rewardResult in openedResults)
                    _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, rewardResult.ItemTemplateId, rewardResult.SlotIndex, 0, 0);

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
                var existingItem = _db.FindItemByTemplateId(connection, transaction, characterId, stackListType, itemTemplateId);
                var stackLimit = metadata.StackLimit;
                if (existingItem != null && (stackLimit <= 0 || existingItem.StackCount + effectiveCount <= stackLimit))
                {
                    var newStackCount = existingItem.StackCount + effectiveCount;
                    if (isPetConsumable)
                        _db.UpdatePetStackCount(connection, transaction, existingItem.ItemUid, newStackCount);
                    else
                        _db.UpdateStackCount(connection, transaction, existingItem.ItemUid, newStackCount);
                    ApplyCeraShopPayment(connection, transaction, characterId, plan);
                    _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, existingItem.SlotIndex, totalGoldCost, totalCeraCost);

                    result = new InventoryMutationResult
                    {
                        ListType = stackListType,
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

            if (isStackable)
            {
                var stackLimit = metadata.StackLimit;
                if (IsCeraShopStackablePackage(product.Section))
                    stackLimit = int.MaxValue;

                var existingItem = _db.FindStackableItemByTemplateIdAndExpireTime(
                    connection, transaction, characterId, stackListType, itemTemplateId, 0, stackLimit);

                if (existingItem != null && (stackLimit <= 0 || existingItem.StackCount + effectiveCount <= stackLimit))
                {
                    var newStackCount = existingItem.StackCount + effectiveCount;
                    if (isPetConsumable)
                        _db.UpdatePetStackCount(connection, transaction, existingItem.ItemUid, newStackCount);
                    else
                        _db.UpdateStackCount(connection, transaction, existingItem.ItemUid, newStackCount);
                    ApplyCeraShopPayment(connection, transaction, characterId, plan);
                    _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, existingItem.SlotIndex, totalGoldCost, totalCeraCost);

                    result = new InventoryMutationResult
                    {
                        ListType = stackListType,
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
            else if (isPetArtifactEquipment)
            {
                insertListType = InventoryListType.Pet;
                insertKind = "pet";
                slotStart = SqliteInventoryStore.PetEquipmentSlotStart;
                slotEnd = SqliteInventoryStore.PetEquipmentSlotEnd;
                expireTime = 0;
            }
            else if (isPetConsumable)
            {
                insertListType = InventoryListType.Pet;
                insertKind = "pet";
                slotStart = SqliteInventoryStore.PetConsumableSlotStart;
                slotEnd = SqliteInventoryStore.PetConsumableSlotEnd;
                expireTime = 0;
            }
            else
            {
                metadata.GetSlotRange(out slotStart, out slotEnd);
            }

            var targetSlot = _db.FindEmptySlot(connection, transaction, characterId, insertListType, slotStart, slotEnd);
            if (targetSlot < 0)
            {
                FileLogger.Log($"  [CeraShopBuy] REJECT: no empty slot product=0x{productId:X8} item=0x{itemTemplateId:X8} list={insertListType} slotRange={slotStart}-{slotEnd}");
                return false;
            }

            var usesPetInventory = isCreature || isPetArtifactEquipment || isPetConsumable;
            // For pet consumables this field is displayed as the stack count in pet-list entries.
            var petSerial = isPetConsumable ? effectiveCount : (isCreature ? _db.NextPetSerialOrHandle(connection, transaction, characterId) : 0);
            var instanceValue = isStackable ? effectiveCount : (usesPetInventory || isAvatar ? 0 : InventoryDbPrimitives.GenerateInstanceValue(itemTemplateId, targetSlot));
            var durability = (isAvatar || usesPetInventory) ? (ushort)0 : metadata.Durability;
            if (isAvatar)
            {
                var avatarItem = SqliteInventoryStore.CreateDefaultAvatarItem((short)targetSlot, itemTemplateId, 0);
                _db.InsertCharacterItem(
                    connection,
                    transaction,
                    characterId,
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
                    characterId,
                    insertListType,
                    (short)targetSlot,
                    itemTemplateId,
                    insertKind,
                    isPetEquipment ? 0 : effectiveCount,
                    instanceValue,
                    durability,
                    0,
                    0,
                    expireTime,
                    0,
                    petSerial,
                    "{}");
            }

            ApplyCeraShopPayment(connection, transaction, characterId, plan);
            _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, (short)targetSlot, totalGoldCost, totalCeraCost);

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

        private static bool IsCeraShopStackablePackage(string section)
        {
            if (string.IsNullOrWhiteSpace(section))
                return false;

            return section.Equals("package", StringComparison.OrdinalIgnoreCase)
                || section.Equals("booster", StringComparison.OrdinalIgnoreCase);
        }
    }
}
