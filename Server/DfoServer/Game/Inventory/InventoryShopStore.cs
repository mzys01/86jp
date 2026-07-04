using DfoServer.Game.Shop;
using DfoServer.Game.Currency;
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

        internal static int NormalizeCeraShopEffectiveStackCount(int buyCount, int productCount, int metadataStackLimit)
        {
            if (buyCount <= 0)
                buyCount = 1;

            var unitCount = Math.Max(1, productCount);
            var stackLimit = ResolveCeraShopStackLimit(unitCount, metadataStackLimit);
            var requestedCount = (long)buyCount * unitCount;
            if (requestedCount <= 0)
                return 1;

            if (stackLimit > 0 && requestedCount > stackLimit)
                return stackLimit;

            return requestedCount > int.MaxValue ? int.MaxValue : (int)requestedCount;
        }

        internal static int ResolveCeraShopStackLimit(int productCount, int metadataStackLimit)
        {
            var unitCount = Math.Max(1, productCount);
            if (metadataStackLimit <= 0)
                return 0;

            return Math.Max(metadataStackLimit, unitCount);
        }

        // ============================================================
        // 装扮兑换券对照表 [grade][durIndex] = couponId
        // ============================================================
        // grade: 来自装备 PVF [grade] 字段，1=普通, 2=高级, 3=稀有。
        // durIndex: 商城档位 (product.Count), 1=7天, 2=30天, 3=永久(无期限)。
        // couponId: PVF stackable 物品 ID, 对应客户端背包中的兑换券道具, 需与客户端约定一致。
        //
        // 设计要点:
        //   稀有装扮(grade=3)仅开放永久兑换(durIndex=3), 因为稀有兑换券只有一种;
        //   TryGetAvatarCouponId 中对 grade=3 且 durIndex≠3 的情况会显式拒绝。
        //   此表为静态只读, 兑换券 ID 固定不变, 无运行时并发写入风险。
        private static readonly Dictionary<int, Dictionary<int, int>> AvatarCouponTable = new()
        {
            { 1, new() { { 1, 2681588 }, { 2, 2681589 }, { 3, 2681590 } } }, // 普通
            { 2, new() { { 1, 2681591 }, { 2, 2681592 }, { 3, 2681593 } } }, // 高级
            { 3, new() { { 3, 2681594 } } },                                   // 稀有(仅永久)
        };

        private struct CeraShopPaymentPlan
        {
            public bool Ok;
            public int NewGold;
            public int NewCera;
            public int NewTokenCera;
            public int NewHappyTokenCera;
            // 各币种实际扣减额(瀑布分摊结果), 落库用条件扣减而非绝对值覆盖
            public int SpentGold;
            public int SpentCera;
            public int SpentTokenCera;
            public int SpentHappyTokenCera;
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
        private static CeraShopPaymentPlan ComputeCeraShopPayment(WalletSnapshot w, int goldCost, int ceraCost, CeraPayMode mode)
        {
            var plan = new CeraShopPaymentPlan
            {
                Ok = false,
                NewGold = w.Gold,
                NewCera = w.Cera,
                NewTokenCera = w.TokenCera,
                NewHappyTokenCera = w.HappyTokenCera,
            };

            if (goldCost > 0)
            {
                if (w.Gold < goldCost)
                    return plan;
                plan.NewGold = w.Gold - goldCost;
                plan.SpentGold = goldCost;
            }

            if (ceraCost > 0)
            {
                var useHappy = mode != CeraPayMode.OnlyCera;          // OnlyCera 不能用欢乐/代币券
                var useToken = mode != CeraPayMode.OnlyCera;
                var useCera = mode != CeraPayMode.OnlyCeraPoint;       // OnlyCeraPoint 不能用点券

                var remaining = ceraCost;
                int happy = plan.NewHappyTokenCera, token = plan.NewTokenCera, cera = plan.NewCera;
                if (useHappy && remaining > 0) { var t = Math.Min(remaining, happy); happy -= t; remaining -= t; plan.SpentHappyTokenCera = t; }
                if (useToken && remaining > 0) { var t = Math.Min(remaining, token); token -= t; remaining -= t; plan.SpentTokenCera = t; }
                if (useCera && remaining > 0) { var t = Math.Min(remaining, cera); cera -= t; remaining -= t; plan.SpentCera = t; }
                if (remaining > 0)
                    return plan; // 允许的币池内不够付

                plan.NewHappyTokenCera = happy;
                plan.NewTokenCera = token;
                plan.NewCera = cera;
            }

            plan.Ok = true;
            return plan;
        }

        // 落库: 按计划扣减额条件扣减(WHERE col>=amt)。任一币种拒绝即返回 false, 调用方 return false 令外层事务回滚。
        // Compute 与本方法在同一事务内, 正常情况下不会失败; 失败即余额被并发改动, 宁可整单失败也不覆盖。
        private bool TryApplyCeraShopPayment(SqliteConnection connection, SqliteTransaction transaction, int characterId, CeraShopPaymentPlan plan)
        {
            if (plan.SpentGold > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, plan.SpentGold))
                return false;
            if (plan.SpentHappyTokenCera > 0 && !CurrencyService.TrySpendHappyTokenCera(connection, transaction, characterId, plan.SpentHappyTokenCera))
                return false;
            if (plan.SpentTokenCera > 0 && !CurrencyService.TrySpendTokenCera(connection, transaction, characterId, plan.SpentTokenCera))
                return false;
            if (plan.SpentCera > 0 && !CurrencyService.TrySpendCera(connection, transaction, characterId, plan.SpentCera))
                return false;
            return true;
        }

        // ============================================================
        // 兑换券扣减: 与 TryApplyCeraShopPayment 在同一事务内执行
        // ============================================================
        // 若 couponItemUid>0, 则扣减1个兑换券:
        //   - 扣后栈数>0 → UpdateStackCount
        //   - 扣后栈数=0 → DeleteItem (整行删除)
        // 此方法必须在 TryApplyCeraShopPayment 之后调用, 确保事务内顺序为:
        //   1) ComputeCeraShopPayment (计算不扣费)
        //   2) TryApplyCeraShopPayment (扣点券, 若 paymentMode=0)
        //   3) DeductCouponIfNeeded (扣兑换券, 若 paymentMode=1)
        // 若任意步骤失败, 事务回滚, 已扣点券/已扣兑换券均回滚。
        private void DeductCouponIfNeeded(SqliteConnection connection, SqliteTransaction transaction, long couponItemUid, int couponNewStackCount)
        {
            if (couponItemUid <= 0)
                return;
            if (couponNewStackCount > 0)
                _db.UpdateStackCount(connection, transaction, couponItemUid, couponNewStackCount);
            else
                _db.DeleteItem(connection, transaction, couponItemUid);
        }

        // ============================================================
        // 根据装扮等级+期限查找对应兑换券 ID
        // ============================================================
        // grade: 物品 PVF [grade] 字段值 (1=普通, 2=高级, 3=稀有)
        // durIndex: 商城档位 (product.Count, 1=7天, 2=30天, 3=永久)
        //
        // 安全约束:
        //   - 稀有装扮(grade=3)仅允许永久兑换券(durIndex=3), 因为稀有兑换券只有一种 ID(2681594)。
        //   - 若 AvatarCouponTable 中无对应映射, 返回 false 并记录日志, 购买流程终止。
        //   - 此映射表为静态只读, 无并发写入风险。
        private static bool TryGetAvatarCouponId(int grade, int durIndex, out int couponId)
        {
            couponId = 0;

            // 稀有装扮(durIndex 非 3/永久时报错)
            if (grade == 3)
            {
                if (durIndex != 3)
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: rare avatar only supports permanent coupon, grade={grade} durIndex={durIndex}");
                    return false;
                }
                couponId = 2681594;
                return true;
            }

            if (AvatarCouponTable.TryGetValue(grade, out var durMap) && durMap.TryGetValue(durIndex, out couponId))
                return true;

            FileLogger.Log($"  [CeraShopBuy] REJECT: no coupon for grade={grade} durIndex={durIndex}");
            return false;
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
                    if (!TryConsumeMaterial(connection, transaction, characterId, accountId, metadata.NeedMaterialId, totalMaterialCost, out materialNewCount, out materialSlotIndex))
                        return false;
                }

                if (totalGoldCost > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, totalGoldCost))
                    return false;
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
                    UpdatedCoin = wallet.Cera,
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

                int materialNewCount;
                short materialSlotIndex;
                if (!TryConsumeMaterial(connection, transaction, characterId, accountId, metadata.NeedMaterialId, totalMaterialCost, out materialNewCount, out materialSlotIndex))
                    return false;

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
                    _db.InsertCharacterItem(connection, transaction, characterId, targetListType, (short)emptySlot,
                        itemTemplateId, targetItemKind, isCreature || isPetArtifactEquipment ? 0 : buyCount, isPetEquipment ? 0 : buyCount,
                        targetListType == InventoryListType.Pet ? (ushort)0 : metadata.Durability, 0, 0, 0, 0,
                        isPetConsumable ? buyCount : isCreature ? _db.NextPetSerialOrHandle(connection, transaction, characterId) : 0,
                        "{}");
                    matTargetSlot = (short)emptySlot;
                }
                else
                {
                    if (isPetConsumable)
                        _db.UpdatePetStackCount(connection, transaction, targetItem.ItemUid, targetItem.StackCount + buyCount);
                    else
                        _db.UpdateStackCount(connection, transaction, targetItem.ItemUid, targetItem.StackCount + buyCount);
                    matTargetSlot = targetItem.SlotIndex;
                }

                if (totalGoldCost > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, totalGoldCost))
                    return false;
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
                    UpdatedCoin = wallet.Cera,
                    RequestedCount = (short)buyCount,
                    AppliedCount = (short)buyCount,
                    CostItemTemplateId = metadata.NeedMaterialId,
                    CostItemNewStackCount = materialNewCount,
                    CostItemSlotIndex = materialSlotIndex,
                };
                return true;
            }

            var walletCheck = _db.LoadWallet(connection, transaction, characterId);
            if (walletCheck.Gold < metadata.BuyGold || walletCheck.Cera < metadata.BuyCoin)
                return false;

            // For stackable items, try to stack onto existing item first
            if (metadata.IsStackable)
            {
                var existingItem = _db.FindItemByTemplateId(connection, transaction, characterId, targetListType, itemTemplateId);
                if (existingItem != null)
                {
                    var totalCostGold = metadata.BuyGold * buyCount;
                    var totalCostCoin = metadata.BuyCoin * buyCount;
                    if (walletCheck.Gold < totalCostGold || walletCheck.Cera < totalCostCoin)
                        return false;
                    var newStackCount = existingItem.StackCount + buyCount;
                    if (isPetConsumable)
                        _db.UpdatePetStackCount(connection, transaction, existingItem.ItemUid, newStackCount);
                    else
                        _db.UpdateStackCount(connection, transaction, existingItem.ItemUid, newStackCount);
                    var updGold = walletCheck.Gold - totalCostGold;
                    var updCoin = walletCheck.Cera - totalCostCoin;
                    if (totalCostGold > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, totalCostGold))
                        return false;
                    if (totalCostCoin > 0 && !CurrencyService.TrySpendCera(connection, transaction, characterId, totalCostCoin))
                        return false;
                    _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, existingItem.SlotIndex, totalCostGold, totalCostCoin);

                    result = new InventoryMutationResult
                    {
                        ListType = targetListType,
                        SlotIndex = existingItem.SlotIndex,
                        ItemTemplateId = itemTemplateId,
                        RemainingStackCount = newStackCount,
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
            if (walletCheck.Gold < totalBuyGold || walletCheck.Cera < totalBuyCoin)
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
            var updatedCoin = walletCheck.Cera - totalBuyCoin;
            if (totalBuyGold > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, totalBuyGold))
                return false;
            if (totalBuyCoin > 0 && !CurrencyService.TrySpendCera(connection, transaction, characterId, totalBuyCoin))
                return false;
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

            if (SqliteInventoryStore.IsEquipmentItemLocked(connection, transaction, characterId, item))
            {
                FileLogger.Log($"  [SellItem] REJECT: locked item listType={dbListType} slot={slotIndex} lockId={item.EquipmentLockId}");
                return false;
            }

            var metadata = ItemMetadataResolver.Resolve(item.ItemTemplateId);
            var isStackCountedRecord = SqliteInventoryStore.IsStackCountedRecord(item) || metadata.IsStackable;
            var appliedCount = NormalizeSellRemovalCount(item.StackCount, sellCount, isStackCountedRecord);
            var remainingCount = Math.Max(0, item.StackCount - appliedCount);
            if (isStackCountedRecord && appliedCount < item.StackCount)
            {
                if (SqliteInventoryStore.IsPetConsumableRecord(item))
                    _db.UpdatePetStackCount(connection, transaction, item.ItemUid, remainingCount);
                else
                    _db.UpdateStackCount(connection, transaction, item.ItemUid, remainingCount);
            }
            else
            {
                _db.DeleteItem(connection, transaction, item.ItemUid);
            }

            var wallet = _db.LoadWallet(connection, transaction, characterId);
            var goldDelta = metadata.SellGold * appliedCount;
            var updatedGold = wallet.Gold + goldDelta;
            CurrencyService.GrantGold(connection, transaction, characterId, goldDelta);
            _auditLogger.WriteSellAuditLog(connection, transaction, characterId, item, appliedCount, goldDelta);

            result = new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = item.ItemTemplateId,
                RemainingStackCount = remainingCount,
                InstanceValue = isStackCountedRecord ? remainingCount : item.InstanceValue,
                Durability = item.Durability,
                UpdatedGold = updatedGold,
                UpdatedSp = wallet.Sp,
                UpdatedCoin = wallet.Cera,
                RequestedCount = sellCount,
                AppliedCount = (short)appliedCount,
            };
            return true;
        }

        private static int NormalizeSellRemovalCount(int stackCount, short requestedCount, bool isStackable)
        {
            if (!isStackable)
                return 1;

            if (requestedCount <= 0 || requestedCount >= stackCount)
                return stackCount;

            return requestedCount;
        }

       public bool TryBuyCeraShopItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, int productId, int buyCount, int paymentMode, int attributeValue, out InventoryMutationResult result)
        {
            result = null;
            FileLogger.Log($"  [CeraShopBuy] clientProductId=0x{productId:X8} ({productId}) buyCount={buyCount}");

            if (buyCount <= 0)
                buyCount = 1;

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
            var ceraShopStackLimit = ResolveCeraShopStackLimit(product.Count, metadata.StackLimit);
            var effectiveCount = (isStackable && !isAvatar)
                ? NormalizeCeraShopEffectiveStackCount(buyCount, product.Count, metadata.StackLimit)
                : 1;
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

            // ============================================================
            // 兑换券支付分支 (paymentMode == 1)
            // ============================================================
            // 与点券支付互斥:
            //   - 通过 TryGetAvatarCouponId 根据装扮等级+期限查到兑换券 ID。
            //   - 在角色背包中查找该兑换券是否存在, 不存在则拒绝交易。
            //   - 将 totalCeraCost 和 ceraPrice 置零, 后续 ComputeCeraShopPayment
            //     传入 ceraCost=0, 因此不会扣任何点券, ComputeCeraShopPayment 必然 Ok。
            //     这样保证了兑换券支付与点券支付走同一套 ComputeCeraShopPayment + TryApplyCeraShopPayment
            //     流程, 仅在兑换券扣减时走 DeductCouponIfNeeded, 两条路径互不干扰。
            //   - 兑换券扣减 (DeductCouponIfNeeded) 与 TryApplyCeraShopPayment 在同一事务内,
            //     任一步骤失败整体回滚, 不会出现"券已扣但装扮未发"。
            var couponItemUid = 0L;
            var couponSlotIndex = (short)0;
            var couponNewStackCount = 0;
            var couponId = 0;
            if (paymentMode == 1)
            {
                var durIndex = product.Count;
                if (!TryGetAvatarCouponId(metadata.Grade, durIndex, out couponId))
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: couponId not resolved, grade={metadata.Grade} durIndex={durIndex} product=0x{productId:X8}");
                    return false;
                }
                // 置零点券消耗, 兑换券抵扣不涉及 Cera 扣减。后文 ComputeCeraShopPayment 收到 ceraCost=0 必然 Ok。
                totalCeraCost = 0;
                ceraPrice = 0;
                // 在主背包中查找兑换券道具。兑换券是可堆叠的 stackable 物品, 栈数>0 即存在。
                var couponItem = _db.FindItemByTemplateId(connection, transaction, characterId, InventoryListType.Main, couponId);
                if (couponItem == null)
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: no avatar coupon (0x{couponId:X8}) in inventory, product=0x{productId:X8}");
                    return false;
                }
                couponItemUid = couponItem.ItemUid;
                couponSlotIndex = couponItem.SlotIndex;
                couponNewStackCount = couponItem.StackCount - 1;
            }

            var wallet = _db.LoadWallet(connection, transaction, characterId);
            var plan = ComputeCeraShopPayment(wallet, totalGoldCost, totalCeraCost, ceraMode);
            FileLogger.Log($"  [CeraShopBuy] product=0x{productId:X8} -> item=0x{itemTemplateId:X8} section={product.Section} kind={itemKind} count={effectiveCount} gold={totalGoldCost} cera={totalCeraCost} mode={ceraMode} payMode={paymentMode} wallet(g={wallet.Gold},c={wallet.Cera},t={wallet.TokenCera},h={wallet.HappyTokenCera}) ok={plan.Ok}");
            if (!plan.Ok)
            {
                FileLogger.Log($"  [CeraShopBuy] REJECT: insufficient funds gold={totalGoldCost} cera={totalCeraCost} mode={ceraMode}");
                return false;
            }
            var goldSpent = totalGoldCost > 0;

            if (Premium.PremiumCatalog.Load().TryGetValue(itemTemplateId, out _, out _))
            {
                if (!TryApplyCeraShopPayment(connection, transaction, characterId, plan))
                    return false;
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, 0, totalGoldCost, totalCeraCost);
                result = new InventoryMutationResult
                {
                    ItemTemplateId = itemTemplateId,
                    ConsumedOnPurchase = true,
                    UpdatedGold = plan.NewGold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = plan.NewCera,
                    UpdatedTokenCera = plan.NewTokenCera,
                    UpdatedHappyTokenCera = plan.NewHappyTokenCera,
                    GoldSpent = goldSpent,
                    RequestedCount = 1,
                    AppliedCount = 1,
                };
                FileLogger.Log($"  [CeraShopBuy] premium item consumed on purchase: item=0x{itemTemplateId:X8}");
                return true;
            }

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

                if (!TryApplyCeraShopPayment(connection, transaction, characterId, plan))
                    return false;
                DeductCouponIfNeeded(connection, transaction, couponItemUid, couponNewStackCount);
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, 0, totalGoldCost, totalCeraCost);
                foreach (var rewardResult in openedResults)
                    _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, rewardResult.ItemTemplateId, rewardResult.SlotIndex, 0, 0);

                result = openedResults[0];
                if (couponItemUid > 0)
                {
                    result.ExtraResults.Add(new InventoryMutationResult
                    {
                        ListType = InventoryListType.Main,
                        SlotIndex = couponSlotIndex,
                        ItemTemplateId = couponId,
                        RemainingStackCount = couponNewStackCount,
                        InstanceValue = couponNewStackCount,
                    });
                }
                result.UpdatedGold = plan.NewGold;
                result.UpdatedSp = wallet.Sp;
                result.UpdatedCoin = plan.NewCera;
                result.UpdatedTokenCera = plan.NewTokenCera;
                result.UpdatedHappyTokenCera = plan.NewHappyTokenCera;
                result.GoldSpent = goldSpent;
                for (var i = 1; i < openedResults.Count; i++)
                    result.ExtraResults.Add(openedResults[i]);

                FileLogger.Log($"  [CeraShopBuy] auto-open source=0x{itemTemplateId:X8} rewards={string.Join(",", openedResults.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.RemainingStackCount}@{r.SlotIndex}"))}");
                return true;
            }

            if (isStackable)
            {
                var existingItem = _db.FindItemByTemplateId(connection, transaction, characterId, stackListType, itemTemplateId);
                var stackLimit = ceraShopStackLimit;
                if (existingItem != null && stackLimit > 0 && existingItem.StackCount + effectiveCount > stackLimit)
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: stack limit reached product=0x{productId:X8} item=0x{itemTemplateId:X8} slot={existingItem.SlotIndex} current={existingItem.StackCount} add={effectiveCount} limit={stackLimit}");
                    return false;
                }

                if (existingItem != null)
                {
                    var newStackCount = existingItem.StackCount + effectiveCount;
                    if (isPetConsumable)
                        _db.UpdatePetStackCount(connection, transaction, existingItem.ItemUid, newStackCount);
                    else
                        _db.UpdateStackCount(connection, transaction, existingItem.ItemUid, newStackCount);
                    if (!TryApplyCeraShopPayment(connection, transaction, characterId, plan))
                        return false;
                    DeductCouponIfNeeded(connection, transaction, couponItemUid, couponNewStackCount);
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
                    if (couponItemUid > 0)
                    {
                        result.ExtraResults.Add(new InventoryMutationResult
                        {
                            ListType = InventoryListType.Main,
                            SlotIndex = couponSlotIndex,
                            ItemTemplateId = couponId,
                            RemainingStackCount = couponNewStackCount,
                            InstanceValue = couponNewStackCount,
                        });
                    }
                    return true;
                }
            }

            if (isStackable && ceraShopStackLimit > 0 && effectiveCount > ceraShopStackLimit)
            {
                FileLogger.Log($"  [CeraShopBuy] REJECT: stack limit exceeded product=0x{productId:X8} item=0x{itemTemplateId:X8} count={effectiveCount} limit={ceraShopStackLimit}");
                return false;
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
                var avatarItem = SqliteInventoryStore.CreateDefaultAvatarItem((short)targetSlot, itemTemplateId, (byte)attributeValue);
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

            if (!TryApplyCeraShopPayment(connection, transaction, characterId, plan))
                return false;
            DeductCouponIfNeeded(connection, transaction, couponItemUid, couponNewStackCount);
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
            if (couponItemUid > 0)
            {
                result.ExtraResults.Add(new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = couponSlotIndex,
                    ItemTemplateId = couponId,
                    RemainingStackCount = couponNewStackCount,
                    InstanceValue = couponNewStackCount,
                });
            }
            return true;
        }

        /// <summary>
        /// 检查并扣减材料。支持立方体碎片（accounts表）和普通背包物品。
        /// </summary>
        private bool TryConsumeMaterial(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int accountId, int materialId, int cost,
            out int newCount, out short slotIndex)
        {
            newCount = -1;
            slotIndex = -1;

            if (CurrencyService.IsCubeFragment(materialId))
            {
                var cubes = CurrencyService.LoadCubeFragments(connection, transaction, accountId);
                var have = 0;
                foreach (var c in cubes)
                    if (c.ItemId == materialId) have = c.Count;
                if (have < cost)
                {
                    FileLogger.Log($"  [BuyItem] REJECT: need {cost}x cube fragment {materialId}, have {have}");
                    return false;
                }
                CurrencyService.AddCubeFragment(connection, transaction, accountId, materialId, -cost);
                newCount = have - cost;
                slotIndex = (short)CurrencyService.GetCubeFragmentSlot(materialId);
            }
            else
            {
                var materialItem = _db.FindItemByTemplateId(connection, transaction, characterId, InventoryListType.Main, materialId);
                if (materialItem == null || materialItem.StackCount < cost)
                {
                    FileLogger.Log($"  [BuyItem] REJECT: need {cost}x item {materialId}, have {materialItem?.StackCount ?? 0}");
                    return false;
                }
                _db.UpdateStackCount(connection, transaction, materialItem.ItemUid, materialItem.StackCount - cost);
                newCount = materialItem.StackCount - cost;
                slotIndex = materialItem.SlotIndex;
            }
            return true;
        }
    }
}
