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
        private static bool IsAccountCargoUpgradeTool(int itemTemplateId)
        {
            return SqliteInventoryStore.IsAccountCargoUpgradeToolItem(itemTemplateId);
        }

        private static bool TryResolvePersonalCargoUpgradeToolTarget(int itemTemplateId, out ushort targetListParam16)
        {
            return SqliteInventoryStore.TryResolvePersonalCargoUpgradeTarget(itemTemplateId, out targetListParam16);
        }

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

            // NPC 商店也卖宠物页物品; 容器/槽段/字段编码统一走入库核(ItemIntake)。
            var placement = ItemIntake.ResolvePlacement(itemTemplateId, metadata);

            if (placement.ListType == InventoryListType.Main
                && !CharmInventoryPolicy.CanEnterMain(connection, transaction, characterId, itemTemplateId))
                return false;

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
                var matInstanceValue = buyCount;
                ushort matDurability = 0;
                if (!(metadata.IsStackable
                        && ItemIntake.TryMergeStack(
                            _db, connection, transaction, characterId, placement,
                            itemTemplateId, buyCount, metadata.StackLimit,
                            out matTargetSlot, out _))
                    && !ItemIntake.TryInsertNewRow(
                        _db, connection, transaction, characterId, placement,
                        itemTemplateId, metadata, buyCount,
                        out matTargetSlot, out matInstanceValue, out matDurability))
                {
                    FileLogger.Log($"  [BuyItem] REJECT: no empty slot for material exchange item {itemTemplateId}");
                    return false;
                }

                if (totalGoldCost > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, totalGoldCost))
                    return false;
                var goldAfterBuy = wallet.Gold - totalGoldCost;
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, matTargetSlot, totalGoldCost, 0);

                result = new InventoryMutationResult
                {
                    ListType = placement.ListType,
                    SlotIndex = matTargetSlot,
                    ItemTemplateId = itemTemplateId,
                    RemainingStackCount = buyCount,
                    InstanceValue = matInstanceValue,
                    Durability = matDurability,
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

            // 可堆叠先尝试并入既有堆(超上限时落到下方新行)。
            if (metadata.IsStackable
                && ItemIntake.TryMergeStack(
                    _db, connection, transaction, characterId, placement,
                    itemTemplateId, buyCount, metadata.StackLimit,
                    out var mergedSlot, out var mergedStackCount))
            {
                var totalCostGold = metadata.BuyGold * buyCount;
                var totalCostCoin = metadata.BuyCoin * buyCount;
                if (walletCheck.Gold < totalCostGold || walletCheck.Cera < totalCostCoin)
                    return false;
                var updGold = walletCheck.Gold - totalCostGold;
                var updCoin = walletCheck.Cera - totalCostCoin;
                if (totalCostGold > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, totalCostGold))
                    return false;
                if (totalCostCoin > 0 && !CurrencyService.TrySpendCera(connection, transaction, characterId, totalCostCoin))
                    return false;
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, mergedSlot, totalCostGold, totalCostCoin);

                result = new InventoryMutationResult
                {
                    ListType = placement.ListType,
                    SlotIndex = mergedSlot,
                    ItemTemplateId = itemTemplateId,
                    RemainingStackCount = mergedStackCount,
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

            var effectiveCount = metadata.IsStackable ? buyCount : 1;
            var totalBuyGold = metadata.BuyGold * effectiveCount;
            var totalBuyCoin = metadata.BuyCoin * effectiveCount;
            if (walletCheck.Gold < totalBuyGold || walletCheck.Cera < totalBuyCoin)
                return false;

            if (!ItemIntake.TryInsertNewRow(
                    _db, connection, transaction, characterId, placement,
                    itemTemplateId, metadata, effectiveCount,
                    out var targetSlot, out var buyInstanceValue, out var buyDurability))
                return false;

            var updatedGold = walletCheck.Gold - totalBuyGold;
            var updatedCoin = walletCheck.Cera - totalBuyCoin;
            if (totalBuyGold > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, totalBuyGold))
                return false;
            if (totalBuyCoin > 0 && !CurrencyService.TrySpendCera(connection, transaction, characterId, totalBuyCoin))
                return false;
            _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, targetSlot, totalBuyGold, totalBuyCoin);

            result = new InventoryMutationResult
            {
                ListType = placement.ListType,
                SlotIndex = targetSlot,
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
            // PVF 将 821 定义为普通 [etc] 堆叠物；商城购买时按准确物品 ID
            // 作为立即生效的角色扩展处理，不发放到背包。
            var isSkillTreeExpansion = itemTemplateId
                == Game.Skills.SkillTreeExpansionState.ExpansionItemTemplateId;
            if (metadata.ItemKind == "special" && !isSkillTreeExpansion)
            {
                FileLogger.Log($"  [CeraShopBuy] REJECT: product=0x{productId:X8} maps to unsupported item=0x{itemTemplateId:X8} section={product.Section}");
                return false;
            }

            var itemKind = metadata.ItemKind;
            var isStackable = metadata.IsStackable;
            // 限时时装(avatar): cerashop 第3字段(product.Count)= 时长档位(1-based),
            // 时长(天)与点券价取自时装 .equ 的 [avatar type select]。
            var isAvatar = string.Equals(product.Section, "avatar", StringComparison.OrdinalIgnoreCase);
            // 非时装物品的容器/槽段/字段编码统一走入库核(ItemIntake);
            // 时装与名牌是特殊容器, 在下方各自分流, 不进标准入库。
            var placement = ItemIntake.ResolvePlacement(itemTemplateId, metadata);
            var isNameTag = !isAvatar && !placement.IsPet
                && string.Equals(itemKind, "equipment", StringComparison.Ordinal)
                && ItemMetadataResolver.IsNameTagItem(itemTemplateId);
            if (!isAvatar && placement.ListType == InventoryListType.Main
                && !CharmInventoryPolicy.CanEnterMain(connection, transaction, characterId, itemTemplateId))
            {
                FileLogger.Log($"  [CeraShopBuy] REJECT: main inventory already contains a charm item=0x{itemTemplateId:X8}");
                return false;
            }
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

            if (isSkillTreeExpansion)
            {
                if (buyCount != 1 || paymentMode != 0)
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: skill-tree expansion requires one direct purchase product=0x{productId:X8}");
                    return false;
                }

                if (!Game.Skills.SkillTreeExpansionService.TryUnlock(
                    connection, transaction, characterId))
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: skill-tree expansion already owned or initialization failed cid={characterId}");
                    return false;
                }

                if (!TryApplyCeraShopPayment(connection, transaction, characterId, plan))
                    return false;
                DeductCouponIfNeeded(connection, transaction, couponItemUid, couponNewStackCount);
                _auditLogger.WriteBuyAuditLog(
                    connection, transaction, characterId, itemTemplateId, -1,
                    totalGoldCost, totalCeraCost);

                result = new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = -1,
                    ItemTemplateId = itemTemplateId,
                    RemainingStackCount = 0,
                    UpdatedGold = plan.NewGold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = plan.NewCera,
                    UpdatedTokenCera = plan.NewTokenCera,
                    UpdatedHappyTokenCera = plan.NewHappyTokenCera,
                    GoldSpent = goldSpent,
                    RequestedCount = 1,
                    AppliedCount = 1,
                    ConsumedOnPurchase = true,
                };
                FileLogger.Log($"  [CeraShopBuy] skill-tree expansion unlocked cid={characterId} item=0x{itemTemplateId:X8} cera={totalCeraCost}");
                return true;
            }

            if (TryResolvePersonalCargoUpgradeToolTarget(itemTemplateId, out var personalCargoTargetListParam16))
            {
                if (effectiveCount != 1)
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: personal cargo upgrade only supports single purchase product=0x{productId:X8} item=0x{itemTemplateId:X8} count={effectiveCount}");
                    return false;
                }

                if (!SqliteInventoryStore.TryUpgradePersonalCargoToCapacityCore(connection, transaction, characterId, accountId, personalCargoTargetListParam16, out var previousListParam16, out var newListParam16, out var personalCargoErrorCode))
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: personal cargo upgrade product=0x{productId:X8} item=0x{itemTemplateId:X8} target={personalCargoTargetListParam16} error=0x{personalCargoErrorCode:X2}");
                    return false;
                }

                if (!TryApplyCeraShopPayment(connection, transaction, characterId, plan))
                    return false;
                DeductCouponIfNeeded(connection, transaction, couponItemUid, couponNewStackCount);
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, -1, totalGoldCost, totalCeraCost);

                result = new InventoryMutationResult
                {
                    ListType = InventoryListType.PersonalCargo,
                    SlotIndex = -1,
                    ItemTemplateId = itemTemplateId,
                    RemainingStackCount = newListParam16,
                    InstanceValue = newListParam16,
                    UpdatedGold = plan.NewGold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = plan.NewCera,
                    UpdatedTokenCera = plan.NewTokenCera,
                    UpdatedHappyTokenCera = plan.NewHappyTokenCera,
                    GoldSpent = goldSpent,
                    ConsumedOnPurchase = true,
                    RequestedCount = 1,
                    AppliedCount = 1,
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

                FileLogger.Log($"  [CeraShopBuy] personal cargo upgrade consumed on purchase: product=0x{productId:X8} item=0x{itemTemplateId:X8} personalCargo={previousListParam16}->{newListParam16}");
                return true;
            }

            if (IsAccountCargoUpgradeTool(itemTemplateId))
            {
                if (effectiveCount != 1)
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: account cargo upgrade only supports single purchase product=0x{productId:X8} item=0x{itemTemplateId:X8} count={effectiveCount}");
                    return false;
                }

                if (!SqliteInventoryStore.TryUpgradeAccountCargoCore(connection, transaction, accountId, out var previousSelectionKey, out var newSelectionKey, out var accountCargoErrorCode))
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: account cargo upgrade product=0x{productId:X8} item=0x{itemTemplateId:X8} error=0x{accountCargoErrorCode:X2}");
                    return false;
                }

                if (!TryApplyCeraShopPayment(connection, transaction, characterId, plan))
                    return false;
                DeductCouponIfNeeded(connection, transaction, couponItemUid, couponNewStackCount);
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, -1, totalGoldCost, totalCeraCost);

                result = new InventoryMutationResult
                {
                    ListType = InventoryListType.AccountCargo,
                    SlotIndex = -1,
                    ItemTemplateId = itemTemplateId,
                    RemainingStackCount = newSelectionKey,
                    InstanceValue = newSelectionKey,
                    UpdatedGold = plan.NewGold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = plan.NewCera,
                    UpdatedTokenCera = plan.NewTokenCera,
                    UpdatedHappyTokenCera = plan.NewHappyTokenCera,
                    GoldSpent = goldSpent,
                    ConsumedOnPurchase = true,
                    RequestedCount = 1,
                    AppliedCount = 1,
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

                FileLogger.Log($"  [CeraShopBuy] account cargo upgrade consumed on purchase: product=0x{productId:X8} item=0x{itemTemplateId:X8} selectionKey={previousSelectionKey}->{newSelectionKey}");
                return true;
            }

            if (Premium.PremiumService.IsContractItem(itemTemplateId))
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
                        if (!_db.TryAddBoosterRewardItems(connection, transaction, characterId, accountId, reward.ItemId, reward.Count, out var rewardResults))
                        {
                            FileLogger.Log($"  [CeraShopBuy] auto-open failed source=0x{itemTemplateId:X8} reward=0x{reward.ItemId:X8} count={reward.Count}");
                            return false;
                        }

                        foreach (var rewardResult in rewardResults)
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

            // 可堆叠先并入既有堆; 超上限(如 limit=1 礼盒已拥有)不拒绝, 落到下方新行,
            // 保持"limit=1 商品可重复购买各占一格"的既有约定。
            if (isStackable && !isAvatar
                && ItemIntake.TryMergeStack(
                    _db, connection, transaction, characterId, placement,
                    itemTemplateId, effectiveCount, ceraShopStackLimit,
                    out var ceraMergedSlot, out var ceraMergedStack))
            {
                {
                    var newStackCount = ceraMergedStack;
                    if (!TryApplyCeraShopPayment(connection, transaction, characterId, plan))
                        return false;
                    DeductCouponIfNeeded(connection, transaction, couponItemUid, couponNewStackCount);
                    _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, ceraMergedSlot, totalGoldCost, totalCeraCost);

                    result = new InventoryMutationResult
                    {
                        ListType = placement.ListType,
                        SlotIndex = ceraMergedSlot,
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

            if (isNameTag)
            {
                var durationDays = product.DurationDays > 0 ? product.DurationDays : Math.Max(1, product.Count);
                var unixNow = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                var nameTagExpireTime = (int)Math.Min(int.MaxValue, unixNow + (long)durationDays * 86400L);
                if (!TryApplyCeraShopPayment(connection, transaction, characterId, plan))
                    return false;
                DeductCouponIfNeeded(connection, transaction, couponItemUid, couponNewStackCount);
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, 28, totalGoldCost, totalCeraCost);
                UpsertNameTagEquippedEntry(connection, transaction, characterId, itemTemplateId, nameTagExpireTime);
                UpdateNameTagSubtype1Fields(connection, transaction, characterId, (uint)itemTemplateId, (uint)nameTagExpireTime);
                result = new InventoryMutationResult
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = 0,
                    ItemTemplateId = itemTemplateId,
                    RemainingStackCount = 0,
                    UpdatedGold = plan.NewGold,
                    UpdatedSp = wallet.Sp,
                    UpdatedCoin = plan.NewCera,
                    UpdatedTokenCera = plan.NewTokenCera,
                    UpdatedHappyTokenCera = plan.NewHappyTokenCera,
                    GoldSpent = goldSpent,
                    RequestedCount = 1,
                    AppliedCount = 1,
                    ConsumedOnPurchase = true,
                    NameTagEquipped = true,
                };
                FileLogger.Log($"  [CeraShopBuy] name tag equipped: item=0x{itemTemplateId:X8} expire={nameTagExpireTime} duration={durationDays}d");
                return true;
            }

            short targetSlot;
            int instanceValue;
            ushort durability;
            var insertListType = isAvatar ? InventoryListType.Avatar : placement.ListType;
            if (isAvatar)
            {
                // 时装进时装库存(限时档位写真实到期时间), 属性/socket 等特殊字段编码
                // 不走标准入库核, 保持独立插入。
                var avatarExpireTime = -1;   // -1 = 永久
                if (avatarDurationDays > 0)
                {
                    var unixNow = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                    avatarExpireTime = (int)Math.Min(int.MaxValue, unixNow + (long)avatarDurationDays * 86400L);
                }

                var avatarSlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Avatar, 0, 500);
                if (avatarSlot < 0)
                {
                    FileLogger.Log($"  [CeraShopBuy] REJECT: no empty slot product=0x{productId:X8} item=0x{itemTemplateId:X8} list={InventoryListType.Avatar} slotRange=0-500");
                    return false;
                }

                targetSlot = (short)avatarSlot;
                instanceValue = 0;
                durability = 0;
                _db.InsertCharacterItem(
                    connection,
                    transaction,
                    characterId,
                    InventoryListType.Avatar,
                    targetSlot,
                    itemTemplateId,
                    "avatar",
                    0,
                    0,
                    durability,
                    0,
                    (byte)attributeValue,
                    avatarExpireTime,
                    SqliteInventoryStore.DefaultAvatarUnknownFixed30,
                    0,
                    SqliteInventoryStore.CreateDefaultAvatarExtraJson(itemTemplateId));
            }
            else if (!ItemIntake.TryInsertNewRow(
                    _db, connection, transaction, characterId, placement,
                    itemTemplateId, metadata, effectiveCount,
                    out targetSlot, out instanceValue, out durability))
            {
                FileLogger.Log($"  [CeraShopBuy] REJECT: no empty slot product=0x{productId:X8} item=0x{itemTemplateId:X8} list={placement.ListType} slotRange={placement.SlotStart}-{placement.SlotEnd}");
                return false;
            }

            if (!TryApplyCeraShopPayment(connection, transaction, characterId, plan))
                return false;
            DeductCouponIfNeeded(connection, transaction, couponItemUid, couponNewStackCount);
            _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, itemTemplateId, targetSlot, totalGoldCost, totalCeraCost);

            result = new InventoryMutationResult
            {
                ListType = insertListType,
                SlotIndex = targetSlot,
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

        private static void UpsertNameTagEquippedEntry(SqliteConnection connection, SqliteTransaction transaction, int characterId, int itemTemplateId, int expireTime)
        {
            const int nameTagSlot = 28;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
DELETE FROM character_equipped_entries
WHERE character_id = @cid AND slot = @slot;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", nameTagSlot);
                cmd.ExecuteNonQuery();
            }

            var instanceValue = unchecked((uint)InventoryDbPrimitives.GenerateInstanceValue(itemTemplateId, nameTagSlot));
            var fields = new MakeEquipListCodec.DisplayFields { InstanceValue = instanceValue };
            var raw = MakeEquipListCodec.BuildEntryFromDisplayFields(nameTagSlot, itemTemplateId, fields);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
INSERT INTO character_equipped_entries(character_id, slot, item_id, raw_entry, expire_time)
VALUES(@cid, @slot, @itemId, @raw, @expire);";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@slot", nameTagSlot);
                cmd.Parameters.AddWithValue("@itemId", itemTemplateId);
                cmd.Parameters.AddWithValue("@raw", raw);
                cmd.Parameters.AddWithValue("@expire", expireTime);
                cmd.ExecuteNonQuery();
            }
            FileLogger.Log($"  [NameTag] equipped slot={nameTagSlot} item=0x{itemTemplateId:X8} expire={expireTime}");
        }

        private static void UpdateNameTagSubtype1Fields(SqliteConnection connection, SqliteTransaction transaction, int characterId, uint itemId, uint expireTime)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
UPDATE character_subtype1_fields
SET name_tag_item_id = @itemId, name_tag_expire_time = @expire
WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@itemId", (long)itemId);
                cmd.Parameters.AddWithValue("@expire", (long)expireTime);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
