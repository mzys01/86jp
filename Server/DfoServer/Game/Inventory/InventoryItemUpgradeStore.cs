using DfoServer.Game.Currency;
using DfoServer.Game.ItemUpgrade;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryItemUpgradeStore
    {
        private const int WeightScale = 100000;
        private const int DefaultDestroyRewardItemId = 3037;
        private const int DefaultDestroyRewardCount = 1;
        private const int MaterialBagSlotStart = 121;
        private const int MaterialBagSlotEnd = 176;
        private static readonly bool UpgradeLogEnabled = ResolveUpgradeLogEnabled();
        private readonly InventoryDbPrimitives _db;
        private readonly InventoryAuditLogger _auditLogger;

        internal InventoryItemUpgradeStore(InventoryDbPrimitives db, InventoryAuditLogger auditLogger)
        {
            _db = db;
            _auditLogger = auditLogger;
        }

        internal bool TryUpgradeItem(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            ItemUpgradeCommand command,
            out ItemUpgradeResult result)
        {
            if (command == null)
            {
                result = ItemUpgradeResult.Error(null, ItemUpgradeResult.ErrorInvalidTarget);
                return false;
            }

            result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidTarget);

            var target = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, command.TargetSlotIndex);
            if (target == null || target.ItemKind != "equipment" || target.ItemTemplateId != command.TargetItemTemplateId)
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidTarget);
                return false;
            }

            var targetView = InventoryItemView.ForCommon(target);
            var targetItem = BuildUpgradeTargetItem(targetView);

            var targetMetadata = ItemMetadataResolver.Resolve(target.ItemTemplateId);
            if (targetMetadata == null
                || !string.Equals(targetMetadata.ItemKind, "equipment", StringComparison.Ordinal)
                || IsTitle(targetMetadata))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidTarget);
                return false;
            }

            var currentLevel = targetItem.Attr;
            if (currentLevel > 30)
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorMaxLevel);
                return false;
            }

            if (IsItemLocked(connection, transaction, characterId, target))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorLocked);
                return false;
            }

            if (targetMetadata.Durability > 0 && target.Durability != targetMetadata.Durability)
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorDurability);
                return false;
            }

            if (targetMetadata.Durability == 0 && target.Durability != 0)
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorDurability);
                return false;
            }

            if (IsImpossible(command.Mode, targetMetadata))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorRestriction);
                return false;
            }

            var amplifier = ItemAmplifier.FromItem(targetItem);
            if (command.Mode == ItemUpgradeMode.Reinforce && amplifier.HasAmplifyAttribute)
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorWrongUpgradeMode);
                return false;
            }

            if (command.Mode == ItemUpgradeMode.Amplify)
            {
                if (!amplifier.HasAmplifyAttribute)
                {
                    result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorWrongUpgradeMode);
                    return false;
                }

                if (!amplifier.IsIdentified)
                {
                    result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorAmplifyNotIdentified);
                    return false;
                }
            }

            var equipmentType = EquipmentTypeInfo.ParseOrUnknown(targetMetadata.EquipmentType);
            if (!EquipmentTypeInfo.IsUpgradeTargetType(equipmentType))
            {
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidTarget);
                return false;
            }

            var tableKind = command.Mode == ItemUpgradeMode.Amplify ? ItemUpgradeTableKind.Amplify : ItemUpgradeTableKind.Normal;
            var material = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, command.MaterialSlotIndex);
            var materialStackable = LoadMaterialStackable(material);
            var materialConfig = materialStackable != null && material != null
                ? ResolveConsumable(material.ItemTemplateId, materialStackable)
                : null;

            if (!TryBuildContext(command, targetItem, targetMetadata, equipmentType, tableKind, material, materialConfig, out var context, out var row, out var errorCode))
            {
                LogUpgradeReject(FormatBuildContextFailureReason(materialConfig, currentLevel, errorCode), errorCode, command, context, row, material);
                result = ItemUpgradeResult.Error(command, errorCode);
                return false;
            }
            LogUpgradeContext(command, context, row, targetMetadata, target, material, materialStackable);

            if (!ValidateRestrictions(context, target.SealFlag, out errorCode))
            {
                LogUpgradeReject("装备未通过部位/品级/封装/等级限制检查", errorCode, command, context, row, material);
                result = ItemUpgradeResult.Error(command, errorCode);
                return false;
            }

            if (!ValidateMaterial(connection, transaction, accountId, command.MaterialSlotIndex, material, context.Cost, out errorCode))
            {
                LogUpgradeReject("材料数量或材料ID不满足费用要求", errorCode, command, context, row, material);
                result = ItemUpgradeResult.Error(command, errorCode);
                return false;
            }

            var wallet = _db.LoadWallet(connection, transaction, characterId);
            if (wallet.Gold < context.Cost.Gold)
            {
                LogUpgradeReject($"金币不足 当前金币={wallet.Gold} 需要金币={context.Cost.Gold}", ItemUpgradeResult.ErrorInsufficientGold, command, context, row, material);
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInsufficientGold);
                return false;
            }

            var chance = SelectChanceEntry(context);
            if (chance == null || chance.TargetLevel < 0)
            {
                LogUpgradeReject("没有可用的目标等级/成功率候选", ItemUpgradeResult.ErrorUnsupported, command, context, row, material);
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorUnsupported);
                return false;
            }

            var penaltyType = ResolvePenaltyType(context, row, tableKind);
            var finalWeight = CalculateFinalSuccessWeight(context, chance);
            var roll = Random.Shared.Next(WeightScale);
            var success = roll < finalWeight;
            var oldLevel = (byte)currentLevel;
            ProtectTicketSelection protectTicket = null;
            var protectedByTicket = false;
            var destroyed = false;
            var effectivePenaltyType = penaltyType;
            byte newLevel;

            if (success)
            {
                newLevel = (byte)Clamp(chance.TargetLevel, 0, 31);
            }
            else if (penaltyType == 3)
            {
                protectTicket = FindFirstProtectTicket(connection, transaction, characterId, command.Mode);
                if (protectTicket != null)
                {
                    protectedByTicket = true;
                    effectivePenaltyType = 2;
                    newLevel = (byte)Clamp(protectTicket.Config.FailureRetainLevel, 0, oldLevel);
                }
                else
                {
                    destroyed = true;
                    newLevel = 0;
                }
            }
            else
            {
                newLevel = ApplyPenalty(oldLevel, row, penaltyType, context);
            }

            var resultCode = success ? (byte)0 : (byte)Math.Max(1, effectivePenaltyType);

            if (!ConsumeMaterial(connection, transaction, characterId, accountId, command.MaterialSlotIndex, material, context.Cost, out var materialUpdate))
            {
                LogUpgradeReject("扣除材料失败", ItemUpgradeResult.ErrorInvalidMaterial, command, context, row, material);
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidMaterial);
                return false;
            }

            ItemUpgradeSlotCount protectTicketUpdate = null;
            if (protectedByTicket)
            {
                if (!ConsumeMaterial(connection, transaction, characterId, accountId, protectTicket.Item.SlotIndex, protectTicket.Item, protectTicket.Config.Cost, out protectTicketUpdate))
                {
                    LogUpgradeReject("扣除保护券失败", ItemUpgradeResult.ErrorInvalidMaterial, command, context, row, protectTicket.Item);
                    result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInvalidMaterial);
                    return false;
                }

            }

            var updatedGold = wallet.Gold - context.Cost.Gold;
            if (context.Cost.Gold > 0 && !CurrencyService.TrySpendGold(connection, transaction, characterId, context.Cost.Gold))
            {
                LogUpgradeReject($"扣除金币失败 需要金币={context.Cost.Gold}", ItemUpgradeResult.ErrorInsufficientGold, command, context, row, material);
                result = ItemUpgradeResult.Error(command, ItemUpgradeResult.ErrorInsufficientGold);
                return false;
            }

            if (destroyed)
            {
                _db.DeleteItem(connection, transaction, target.ItemUid);
                // 损坏后给客户端发空 entry，清掉原装备所在 slot。
            }
            else
            {
                targetView.Upgrade = newLevel;
                _db.UpdateItemExtraJson(connection, transaction, target.ItemUid, target.ExtraJson);
            }

            ItemUpgradeSlotCount destroyRewardUpdate = null;
            if (destroyed)
            {
                CurrencyService.AddCubeFragment(connection, transaction, accountId, DefaultDestroyRewardItemId, DefaultDestroyRewardCount);
                destroyRewardUpdate = CreateSlotCount(
                    (short)CurrencyService.GetCubeFragmentSlot(DefaultDestroyRewardItemId),
                    DefaultDestroyRewardItemId,
                    LoadCubeFragmentCount(connection, transaction, accountId, DefaultDestroyRewardItemId));
                if (materialUpdate != null
                    && materialUpdate.ItemTemplateId == DefaultDestroyRewardItemId
                    && materialUpdate.SlotIndex == destroyRewardUpdate.SlotIndex)
                    materialUpdate = destroyRewardUpdate;
            }

            if (context.Cost.MaterialCount > 0 && material != null)
                _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, material, context.Cost.MaterialCount);
            if (protectedByTicket)
                _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, protectTicket.Item, protectTicket.Config.Cost.MaterialCount);
            _auditLogger.WriteAuditLog(connection, transaction, characterId, destroyed ? "upgrade_item_destroyed" : "upgrade_item", target, InventoryListType.Main, target.SlotIndex, newLevel - oldLevel);
            LogUpgradeResult(context, row, chance, finalWeight, roll, success, penaltyType, effectivePenaltyType, resultCode, oldLevel, newLevel, wallet.Gold, updatedGold, materialUpdate, protectTicket, protectedByTicket, destroyed, destroyRewardUpdate);

            var upgradeResult = new ItemUpgradeResult
            {
                Command = command,
                Success = true,
                Mode = command.Mode,
                Scene = context.Scene,
                TargetSlotIndex = command.TargetSlotIndex,
                TargetItemTemplateId = command.TargetItemTemplateId,
                MaterialSlotIndex = command.MaterialSlotIndex,
                MaterialItemTemplateId = context.Cost != null ? context.Cost.MaterialItemId : 0,
                OptionalTicketSlotIndex = -1,
                OldLevel = oldLevel,
                NewLevel = newLevel,
                ResultCode = resultCode,
                UpgradeSucceeded = success,
                FinalSuccessWeight = finalWeight,
                MaterialRemainingStackCount = materialUpdate != null ? materialUpdate.Count : 0,
                GoldCost = context.Cost != null ? context.Cost.Gold : 0,
                UpdatedGold = updatedGold,
                NoticeRequired = success
                    ? ItemUpgradeTableProvider.IsNoticeLevel(tableKind, newLevel)
                    : ItemUpgradeTableProvider.IsNoticeLevel(tableKind, oldLevel),
            };

            AddRefreshSlot(upgradeResult.MainRefreshSlots, target.SlotIndex);
            if (materialUpdate != null)
                AddRefreshSlot(upgradeResult.MainRefreshSlots, materialUpdate.SlotIndex);
            if (protectTicketUpdate != null)
                AddRefreshSlot(upgradeResult.MainRefreshSlots, protectTicketUpdate.SlotIndex);
            if (destroyRewardUpdate != null)
                AddRefreshSlot(upgradeResult.MainRefreshSlots, destroyRewardUpdate.SlotIndex);

            if (destroyRewardUpdate != null)
            {
                upgradeResult.DestroyRewardItems.Add(new ItemUpgradeRewardItem
                {
                    SlotIndex = destroyRewardUpdate.SlotIndex,
                    ItemTemplateId = DefaultDestroyRewardItemId,
                    Count = DefaultDestroyRewardCount,
                });
            }

            result = upgradeResult;
            return true;
        }

        private static void AddRefreshSlot(ICollection<short> slots, short slotIndex)
        {
            if (slots == null || slotIndex < 0 || slots.Contains(slotIndex))
                return;

            slots.Add(slotIndex);
        }

        private static void LogUpgradeContext(
            ItemUpgradeCommand command,
            ItemUpgradeContext context,
            UpgradeTableRow row,
            ItemMetadata targetMetadata,
            SqliteInventoryStore.ItemRecord target,
            SqliteInventoryStore.ItemRecord material,
            StackableItemFile materialStackable)
        {
            if (!UpgradeLogEnabled)
                return;

            FileLogger.Log("[ItemUpgrade] ---- 强化/增幅请求 ----");
            FileLogger.Log($"[ItemUpgrade] 操作类型={FormatMode(context.Mode)}，操作场景={FormatScene(context.Scene)}");
            FileLogger.Log($"[ItemUpgrade] 目标装备名称={ResolveEquipmentName(context.TargetItemTemplateId, command?.TargetItemName)}，slot={context.TargetSlotIndex}，当前强化值={context.CurrentUpgradeLevel}，是否最大耐久={FormatBool(IsFullDurability(target, targetMetadata))}，品级rarity={context.EquipmentRarity}，使用等级={context.EquipmentLevel}，部位={FormatEquipmentType(context.EquipmentType)}");
            FileLogger.Log($"[ItemUpgrade] 请求消耗品名称={ResolveRequestMaterialName(context, materialStackable)}，slot={ResolveCostMaterialSlot(context.Cost, material, command?.MaterialSlotIndex ?? -1)}");
            FileLogger.Log($"[ItemUpgrade] 限制: 部位限制={FormatSlotRestriction(context.Restriction?.SlotRestriction ?? 0)} 品级rarity限制={FormatIntList(context.Restriction?.RarityRestrictions)} 封装限制={FormatSealRestriction(context.Restriction?.SealRestriction ?? 0)} 装备等级范围={FormatLevelRange(context.Restriction)}");
            FileLogger.Log($"[ItemUpgrade] 费用消耗：金币={context.Cost?.Gold ?? 0}，消耗材料名称={ResolveCostMaterialName(context.Cost)}，数量={context.Cost?.MaterialCount ?? 0}，slot={ResolveCostMaterialSlot(context.Cost, material, command?.MaterialSlotIndex ?? -1)}");
            FileLogger.Log($"[ItemUpgrade] 成功率加成: 固定加成={FormatWeight(context.SuccessRateAddWeight)}，百分比加成={FormatBonusMultiplier(context.SuccessRateBonusWeight)}");
        }

        private static void LogUpgradeReject(
            string reason,
            byte errorCode,
            ItemUpgradeCommand command,
            ItemUpgradeContext context,
            UpgradeTableRow row,
            SqliteInventoryStore.ItemRecord material)
        {
            if (!UpgradeLogEnabled)
                return;

            FileLogger.Log($"[ItemUpgrade] 强化结果：拒绝，原因={reason}，错误码={errorCode}，操作类型={FormatMode(command?.Mode ?? ItemUpgradeMode.Reinforce)}，目标slot={command?.TargetSlotIndex ?? -1}，材料slot={command?.MaterialSlotIndex ?? -1}，费用={FormatCost(context?.Cost)}，材料={FormatRecord(material)}，候选结果={FormatChanceEntries(context?.ChanceEntries)}，表行={FormatTableRow(row)}");
        }

        private static void LogUpgradeResult(
            ItemUpgradeContext context,
            UpgradeTableRow row,
            ItemUpgradeChanceEntry chance,
            int finalWeight,
            int roll,
            bool success,
            int penaltyType,
            int effectivePenaltyType,
            byte resultCode,
            byte oldLevel,
            byte newLevel,
            int oldGold,
            int updatedGold,
            ItemUpgradeSlotCount materialUpdate,
            ProtectTicketSelection protectTicket,
            bool protectedByTicket,
            bool destroyed,
            ItemUpgradeSlotCount destroyRewardUpdate)
        {
            if (!UpgradeLogEnabled)
                return;

            FileLogger.Log($"[ItemUpgrade] 基础成功率：{FormatWeight(chance.BaseSuccessWeight)}");
            FileLogger.Log($"[ItemUpgrade] 最终成功率：{FormatWeight(finalWeight)}");
            FileLogger.Log($"[ItemUpgrade] 目标强化等级：{chance.TargetLevel}");
            FileLogger.Log($"[ItemUpgrade] 失败惩罚: {FormatPenalty(penaltyType, effectivePenaltyType, context, row, protectedByTicket, destroyed, destroyRewardUpdate)}");
            FileLogger.Log($"[ItemUpgrade] 保护券：{FormatProtectTicket(protectTicket, protectedByTicket)}");
            FileLogger.Log($"[ItemUpgrade] 强化结果：{(success ? "成功" : "失败")}，强化值={oldLevel}->{newLevel}，结果码={resultCode}，随机roll={roll}/100000，金币={oldGold}->{updatedGold}，材料slot={materialUpdate?.SlotIndex.ToString(CultureInfo.InvariantCulture) ?? "无"}，材料剩余数量={materialUpdate?.Count.ToString(CultureInfo.InvariantCulture) ?? "无"}");
        }

        private static string ResolveEquipmentName(int itemTemplateId, string fallback)
        {
            if (ItemMetadataResolver.TryLoadEquipmentFile(itemTemplateId, out var equipment)
                && !string.IsNullOrWhiteSpace(equipment.Name))
                return equipment.Name;

            return !string.IsNullOrWhiteSpace(fallback) ? fallback : $"0x{itemTemplateId:X8}";
        }

        private static string ResolveRequestMaterialName(ItemUpgradeContext context, StackableItemFile materialStackable)
        {
            if (!string.IsNullOrWhiteSpace(materialStackable?.Name))
                return materialStackable.Name;

            return ResolveCostMaterialName(context?.Cost);
        }

        private static string ResolveCostMaterialName(ItemUpgradeCost cost)
        {
            if (cost == null || cost.MaterialItemId <= 0 || cost.MaterialCount <= 0)
                return "无";

            if (ItemMetadataResolver.TryLoadStackableFile(cost.MaterialItemId, out var stackable)
                && !string.IsNullOrWhiteSpace(stackable.Name))
                return stackable.Name;

            return $"0x{cost.MaterialItemId:X8}";
        }

        private static string ResolveCostMaterialSlot(ItemUpgradeCost cost, SqliteInventoryStore.ItemRecord material, short requestMaterialSlot)
        {
            if (cost == null || cost.MaterialItemId <= 0 || cost.MaterialCount <= 0)
                return "无";

            if (CurrencyService.IsCubeFragment(cost.MaterialItemId))
                return CurrencyService.GetCubeFragmentSlot(cost.MaterialItemId).ToString(CultureInfo.InvariantCulture);

            if (material != null)
                return material.SlotIndex.ToString(CultureInfo.InvariantCulture);

            return requestMaterialSlot >= 0
                ? requestMaterialSlot.ToString(CultureInfo.InvariantCulture)
                : "无";
        }

        private static string FormatBuildContextFailureReason(ItemUpgradeConsumableConfig materialConfig, int currentLevel, byte errorCode)
        {
            if (errorCode == ItemUpgradeResult.ErrorRestriction
                && materialConfig != null
                && materialConfig.Scene == ItemUpgradeScene.Portable
                && materialConfig.ActionTypeParams != null
                && materialConfig.ActionTypeParams.Count >= 2
                && !AllowsConsumableCurrentLevel(materialConfig, currentLevel))
            {
                return $"当前强化等级不在强化器/增幅器标签允许范围内，当前={currentLevel}，允许={materialConfig.ActionTypeParams[0]}-{materialConfig.ActionTypeParams[1]}";
            }

            return "构建UpgradeContext失败";
        }

        private static bool IsFullDurability(SqliteInventoryStore.ItemRecord target, ItemMetadata targetMetadata)
        {
            if (target == null || targetMetadata == null)
                return false;

            return targetMetadata.Durability > 0
                ? target.Durability == targetMetadata.Durability
                : target.Durability == 0;
        }

        private static string FormatPenalty(
            int penaltyType,
            int effectivePenaltyType,
            ItemUpgradeContext context,
            UpgradeTableRow row,
            bool protectedByTicket,
            bool destroyed,
            ItemUpgradeSlotCount destroyRewardUpdate)
        {
            if (context != null && context.Scene == ItemUpgradeScene.Ticket)
                return "券类失败不降级、不损坏";

            if (protectedByTicket)
                return $"失败本应损坏，保护券生效，实际惩罚类型={effectivePenaltyType}，装备保留";

            if (destroyed)
            {
                var reward = destroyRewardUpdate != null
                    ? $"{ResolveEquipmentOrStackableName(destroyRewardUpdate.ItemTemplateId)}x{DefaultDestroyRewardCount}，slot={destroyRewardUpdate.SlotIndex}，当前数量={destroyRewardUpdate.Count}"
                    : $"{DefaultDestroyRewardItemId}x{DefaultDestroyRewardCount}";
                return $"失败损坏装备，默认产物={reward}";
            }

            switch (penaltyType)
            {
                case 0:
                    return "无惩罚";
                case 1:
                    return "失败等级不变";
                case 2:
                    return $"失败降级，降级值={row?.PenaltyValue ?? 0}";
                case 3:
                    return "失败损坏装备";
                default:
                    return $"未知惩罚类型={penaltyType}";
            }
        }

        private static string FormatProtectTicket(ProtectTicketSelection protectTicket, bool protectedByTicket)
        {
            if (!protectedByTicket || protectTicket == null)
                return "无";

            return $"{ResolveEquipmentOrStackableName(protectTicket.Item.ItemTemplateId)}，slot={protectTicket.Item.SlotIndex}，失败后保留等级={protectTicket.Config.FailureRetainLevel}";
        }

        private static string ResolveEquipmentOrStackableName(int itemTemplateId)
        {
            if (ItemMetadataResolver.TryLoadStackableFile(itemTemplateId, out var stackable)
                && !string.IsNullOrWhiteSpace(stackable.Name))
                return stackable.Name;

            if (ItemMetadataResolver.TryLoadEquipmentFile(itemTemplateId, out var equipment)
                && !string.IsNullOrWhiteSpace(equipment.Name))
                return equipment.Name;

            return $"0x{itemTemplateId:X8}";
        }

        private static string FormatRecord(SqliteInventoryStore.ItemRecord item)
        {
            if (item == null)
                return "无";

            return $"uid={item.ItemUid} list={item.ListType} slot={item.SlotIndex} item=0x{item.ItemTemplateId:X8} kind={item.ItemKind} stack={item.StackCount} instance=0x{item.InstanceValue:X8} durability={item.Durability} seal_flag={item.SealFlag} expire={item.ExpireTime} marker16={item.Marker16}";
        }

        private static string FormatConsumableConfig(ItemUpgradeConsumableConfig config)
        {
            if (config == null)
                return "未识别为强化/增幅消耗品，后续按NPC强化/增幅处理";

            return $"物品=0x{config.ItemTemplateId:X8} 类型={FormatConsumableKind(config.Kind)} 适用操作={FormatMode(config.Mode)} 场景={FormatScene(config.Scene)} actionType={config.ActionTypeName ?? "无"} action参数={FormatParamList(config.ActionTypeParams)} 费用={FormatCost(config.Cost)} 限制=[部位={FormatSlotRestriction(config.Restriction?.SlotRestriction ?? 0)}, 品级rarity={FormatIntList(config.Restriction?.RarityRestrictions)}, 封装={FormatSealRestriction(config.Restriction?.SealRestriction ?? 0)}, 等级={FormatLevelRange(config.Restriction)}] 候选结果={FormatChanceEntries(config.ChanceEntries)} 固定成功权重加成={FormatWeight(config.SuccessRateAddWeight)} 百分比成功权重加成={FormatBonusMultiplier(config.SuccessRateBonusWeight)} 失败保留等级={config.FailureRetainLevel} 保护触发等级={config.ProtectTriggerLevel}";
        }

        private static string FormatCost(ItemUpgradeCost cost)
        {
            if (cost == null)
                return "无";

            return $"材料=0x{cost.MaterialItemId:X8}x{cost.MaterialCount} 金币={cost.Gold}";
        }

        private static string FormatChanceEntries(IReadOnlyList<ItemUpgradeChanceEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return "无";

            var parts = new List<string>();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                parts.Add($"#{i}:目标等级={entry.TargetLevel},基础成功权重={FormatWeight(entry.BaseSuccessWeight)},基础失败权重={FormatWeight(entry.BaseFailureWeight)}");
            }

            return string.Join("; ", parts);
        }

        private static string FormatTableRow(UpgradeTableRow row)
        {
            if (row == null)
                return "无（券类场景可能不需要升级表行）";

            var rawValues = new List<string>();
            var values = row.Values ?? Array.Empty<double>();
            for (var i = 0; i < values.Length; i++)
                rawValues.Add(values[i].ToString("0.###", CultureInfo.InvariantCulture));

            return $"tableType={row.TableType ?? "默认"} rowIndex={row.RowIndex} 目标等级={row.TargetLevel} 失败权重={FormatWeight(row.FailureWeight)} 推导成功权重={FormatWeight(row.DerivedSuccessWeight)} 失败惩罚类型={row.PenaltyType} 惩罚值={row.PenaltyValue} 表材料=0x{row.MaterialItemId:X8}x{row.MaterialCount} 原始17列=[{string.Join(",", rawValues)}]";
        }

        private static string FormatWeight(int weight)
        {
            if (weight < 0)
                return $"未设置({weight})";

            return $"{weight}/100000({(weight / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)}%)";
        }

        private static string FormatBonusMultiplier(int bonusWeight)
        {
            var multiplier = (WeightScale + bonusWeight) / (double)WeightScale;
            return $"{FormatWeight(bonusWeight)} 最终倍率={multiplier.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        private static string FormatIntList(IReadOnlyList<int> values)
        {
            if (values == null || values.Count == 0)
                return "无限制";

            var parts = new List<string>();
            for (var i = 0; i < values.Count; i++)
                parts.Add(values[i].ToString(CultureInfo.InvariantCulture));

            return string.Join(",", parts);
        }

        private static string FormatParamList(IReadOnlyList<int> values)
        {
            if (values == null || values.Count == 0)
                return "无";

            var parts = new List<string>();
            for (var i = 0; i < values.Count; i++)
                parts.Add(values[i].ToString(CultureInfo.InvariantCulture));

            return string.Join(",", parts);
        }

        private static string FormatLevelRange(ItemUpgradeRestriction restriction)
        {
            if (restriction == null || (restriction.ItemLevelMin < 0 && restriction.ItemLevelMax < 0))
                return "无限制";

            var min = restriction.ItemLevelMin >= 0 ? restriction.ItemLevelMin.ToString(CultureInfo.InvariantCulture) : "-∞";
            var max = restriction.ItemLevelMax >= 0 ? restriction.ItemLevelMax.ToString(CultureInfo.InvariantCulture) : "+∞";
            return $"{min}-{max}";
        }

        private static string FormatBool(bool value)
        {
            return value ? "是" : "否";
        }

        private static string FormatMode(ItemUpgradeMode mode)
        {
            return mode == ItemUpgradeMode.Amplify ? "增幅" : "强化";
        }

        private static string FormatScene(ItemUpgradeScene scene)
        {
            switch (scene)
            {
                case ItemUpgradeScene.Npc:
                    return "NPC";
                case ItemUpgradeScene.Ticket:
                    return "券/保护券";
                case ItemUpgradeScene.Portable:
                    return "强化器/增幅器";
                default:
                    return scene.ToString();
            }
        }

        private static string FormatConsumableKind(ItemUpgradeConsumableKind kind)
        {
            switch (kind)
            {
                case ItemUpgradeConsumableKind.None:
                    return "无（NPC）";
                case ItemUpgradeConsumableKind.ReinforcementTicket:
                    return "强化券";
                case ItemUpgradeConsumableKind.AmplifyTicket:
                    return "增幅券";
                case ItemUpgradeConsumableKind.RandomEnchantTicket:
                    return "随机强化/增幅券";
                case ItemUpgradeConsumableKind.PortableReinforcement:
                    return "强化器";
                case ItemUpgradeConsumableKind.PortableAmplify:
                    return "增幅器";
                case ItemUpgradeConsumableKind.ProtectReinforcement:
                    return "强化保护券";
                case ItemUpgradeConsumableKind.ProtectAmplify:
                    return "增幅保护券";
                default:
                    return kind.ToString();
            }
        }

        private static string FormatEquipmentType(EquipmentType type)
        {
            switch (type)
            {
                case EquipmentType.Weapon:
                    return "武器(Weapon/10)";
                case EquipmentType.Coat:
                    return "上衣(Coat/12)";
                case EquipmentType.Shoulder:
                    return "护肩(Shoulder/13)";
                case EquipmentType.Pants:
                    return "下装(Pants/14)";
                case EquipmentType.Shoes:
                    return "鞋(Shoes/15)";
                case EquipmentType.Waist:
                    return "腰带(Waist/16)";
                case EquipmentType.Amulet:
                    return "项链(Amulet/17)";
                case EquipmentType.Wrist:
                    return "手镯(Wrist/18)";
                case EquipmentType.Ring:
                    return "戒指(Ring/19)";
                case EquipmentType.Support:
                    return "辅助装备(Support/20)";
                case EquipmentType.MagicStone:
                    return "魔法石(MagicStone/21)";
                default:
                    return $"{type}({(int)type})";
            }
        }

        private static string FormatSlotRestriction(int slotRestriction)
        {
            switch (slotRestriction)
            {
                case 0:
                    return "0(无限制)";
                case 1:
                    return "1(仅武器)";
                case 2:
                    return "2(仅防具)";
                case 3:
                    return "3(仅首饰)";
                default:
                    return $"{slotRestriction}(未知限制)";
            }
        }

        private static string FormatSealRestriction(int sealRestriction)
        {
            switch (sealRestriction)
            {
                case 0:
                    return "0(不限制)";
                case 1:
                    return "1(要求seal_flag非0)";
                default:
                    return $"{sealRestriction}(未知封装限制)";
            }
        }

        private static string FormatAmplifier(ItemAmplifierState amplifier)
        {
            if (amplifier == null || !amplifier.HasAmplifyAttribute)
                return "无增幅属性";

            return $"{FormatAmplifyAttribute(amplifier.AttributeType)} 数值={amplifier.AttributeValue} 是否已鉴定={FormatBool(amplifier.IsIdentified)}";
        }

        private static string FormatAmplifyAttribute(AmplifyAttributeType type)
        {
            switch (type)
            {
                case AmplifyAttributeType.Strength:
                    return "力量";
                case AmplifyAttributeType.Intelligence:
                    return "智力";
                case AmplifyAttributeType.Vitality:
                    return "体力";
                case AmplifyAttributeType.Spirit:
                    return "精神";
                default:
                    return "无";
            }
        }

        private static bool TryBuildContext(
            ItemUpgradeCommand command,
            InvenItem targetItem,
            ItemMetadata targetMetadata,
            EquipmentType equipmentType,
            ItemUpgradeTableKind tableKind,
            SqliteInventoryStore.ItemRecord material,
            ItemUpgradeConsumableConfig materialConfig,
            out ItemUpgradeContext context,
            out UpgradeTableRow row,
            out byte errorCode)
        {
            context = null;
            row = null;
            errorCode = ItemUpgradeResult.ErrorUnsupported;

            var currentLevel = targetItem.Attr;
            var targetLevel = currentLevel + 1;
            var input = new EquipmentUpgradeCostInput
            {
                EquipmentLevel = targetMetadata.MinimumLevel,
                Rarity = targetMetadata.Rarity,
                EquipmentType = equipmentType,
                CurrentUpgradeLevel = currentLevel,
            };

            context = new ItemUpgradeContext
            {
                Mode = command.Mode,
                TargetItem = targetItem,
                TargetSlotIndex = command.TargetSlotIndex,
                TargetItemTemplateId = command.TargetItemTemplateId,
                CurrentUpgradeLevel = currentLevel,
                EquipmentType = equipmentType,
                EquipmentLevel = targetMetadata.MinimumLevel,
                EquipmentGrade = targetMetadata.Grade,
                EquipmentRarity = targetMetadata.Rarity,
            };

            if (materialConfig == null)
            {
                if (!ItemUpgradeTableProvider.TryGetRow(tableKind, targetLevel, out row))
                {
                    errorCode = ItemUpgradeResult.ErrorMaxLevel;
                    return false;
                }

                context.Scene = ItemUpgradeScene.Npc;
                context.ConsumableKind = ItemUpgradeConsumableKind.None;
                context.Cost = ItemUpgradeTableProvider.BuildCost(tableKind, row, input);
                context.ChanceEntries.Add(new ItemUpgradeChanceEntry
                {
                    TargetLevel = targetLevel,
                    BaseSuccessWeight = row.DerivedSuccessWeight,
                    BaseFailureWeight = row.FailureWeight,
                });
                return true;
            }

            if (materialConfig.Mode != command.Mode)
            {
                errorCode = ItemUpgradeResult.ErrorWrongUpgradeMode;
                return false;
            }

            if (!AllowsConsumableCurrentLevel(materialConfig, currentLevel))
            {
                errorCode = ItemUpgradeResult.ErrorRestriction;
                return false;
            }

            context.Scene = materialConfig.Scene;
            context.ConsumableKind = materialConfig.Kind;
            context.Restriction = materialConfig.Restriction ?? new ItemUpgradeRestriction();
            context.Cost = materialConfig.Cost ?? new ItemUpgradeCost();
            context.SuccessRateAddWeight = materialConfig.SuccessRateAddWeight;
            context.SuccessRateBonusWeight = materialConfig.SuccessRateBonusWeight;
            context.FailureRetainLevel = materialConfig.FailureRetainLevel;

            if (materialConfig.Scene == ItemUpgradeScene.Ticket)
            {
                foreach (var chance in materialConfig.ChanceEntries)
                    context.ChanceEntries.Add(chance);

                if (context.ChanceEntries.Count == 0)
                {
                    errorCode = ItemUpgradeResult.ErrorUnsupported;
                    return false;
                }

                return true;
            }

            if (!ItemUpgradeTableProvider.TryGetRow(tableKind, targetLevel, out row))
            {
                errorCode = ItemUpgradeResult.ErrorMaxLevel;
                return false;
            }

            context.ChanceEntries.Add(new ItemUpgradeChanceEntry
            {
                TargetLevel = targetLevel,
                BaseSuccessWeight = row.DerivedSuccessWeight,
                BaseFailureWeight = row.FailureWeight,
            });
            return true;
        }

        private static bool ValidateRestrictions(ItemUpgradeContext context, byte targetSealFlag, out byte errorCode)
        {
            errorCode = ItemUpgradeResult.ErrorRestriction;
            var restriction = context.Restriction ?? new ItemUpgradeRestriction();

            if (!restriction.AllowsEquipmentType(context.EquipmentType))
                return false;

            if (!restriction.AllowsRarity(context.EquipmentRarity))
                return false;

            if (!restriction.AllowsItemLevel(context.EquipmentLevel))
                return false;

            if (restriction.SealRestriction == 1 && targetSealFlag == 0)
                return false;

            return true;
        }

        private static bool AllowsConsumableCurrentLevel(ItemUpgradeConsumableConfig config, int currentLevel)
        {
            if (config == null || config.Scene != ItemUpgradeScene.Portable)
                return true;

            if (config.ActionTypeParams == null || config.ActionTypeParams.Count < 2)
                return true;

            return currentLevel >= config.ActionTypeParams[0] && currentLevel <= config.ActionTypeParams[1];
        }

        private static bool ValidateMaterial(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            short materialSlotIndex,
            SqliteInventoryStore.ItemRecord material,
            ItemUpgradeCost cost,
            out byte errorCode)
        {
            errorCode = ItemUpgradeResult.ErrorInvalidMaterial;
            if (cost == null || cost.MaterialItemId <= 0 || cost.MaterialCount <= 0)
                return true;

            if (CurrencyService.IsCubeFragment(cost.MaterialItemId))
            {
                var cubes = CurrencyService.LoadCubeFragments(connection, transaction, accountId);
                foreach (var cube in cubes)
                {
                    if (cube.ItemId == cost.MaterialItemId)
                    {
                        if (cube.Count >= cost.MaterialCount)
                            return true;
                        break;
                    }
                }

                return IsStackableMaterial(material, cost.MaterialItemId)
                    && material.StackCount >= cost.MaterialCount;
            }

            return IsStackableMaterial(material, cost.MaterialItemId)
                && material.StackCount >= cost.MaterialCount;
        }

        private ProtectTicketSelection FindFirstProtectTicket(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ItemUpgradeMode mode)
        {
            var items = _db.LoadItemsByListType(connection, transaction, characterId, InventoryListType.Main);
            var quick = FindFirstProtectTicketInRange(items, mode, SqliteInventoryStore.QuickSlotStart, SqliteInventoryStore.QuickSlotEnd);
            if (quick != null)
                return quick;

            return FindFirstProtectTicketInRange(items, mode, MaterialBagSlotStart, MaterialBagSlotEnd);
        }

        private static ProtectTicketSelection FindFirstProtectTicketInRange(
            IReadOnlyList<SqliteInventoryStore.ItemRecord> items,
            ItemUpgradeMode mode,
            int slotStart,
            int slotEnd)
        {
            if (items == null)
                return null;

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.SlotIndex < slotStart || item.SlotIndex > slotEnd || item.StackCount <= 0)
                    continue;

                var stackable = LoadMaterialStackable(item);
                var config = stackable != null ? ResolveConsumable(item.ItemTemplateId, stackable) : null;
                if (IsProtectTicketForMode(config, mode))
                    return new ProtectTicketSelection { Item = item, Config = config };
            }

            return null;
        }

        private static bool IsProtectTicketForMode(ItemUpgradeConsumableConfig config, ItemUpgradeMode mode)
        {
            if (config == null || config.Mode != mode)
                return false;

            return mode == ItemUpgradeMode.Amplify
                ? config.Kind == ItemUpgradeConsumableKind.ProtectAmplify
                : config.Kind == ItemUpgradeConsumableKind.ProtectReinforcement;
        }

        private bool ConsumeMaterial(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            short materialSlotIndex,
            SqliteInventoryStore.ItemRecord material,
            ItemUpgradeCost cost,
            out ItemUpgradeSlotCount materialUpdate)
        {
            materialUpdate = null;
            if (cost == null || cost.MaterialItemId <= 0 || cost.MaterialCount <= 0)
                return true;

            if (CurrencyService.IsCubeFragment(cost.MaterialItemId))
            {
                var cubes = CurrencyService.LoadCubeFragments(connection, transaction, accountId);
                foreach (var cube in cubes)
                {
                    if (cube.ItemId == cost.MaterialItemId)
                    {
                        if (cube.Count >= cost.MaterialCount)
                        {
                            CurrencyService.AddCubeFragment(connection, transaction, accountId, cost.MaterialItemId, -cost.MaterialCount);
                            materialUpdate = CreateSlotCount((short)CurrencyService.GetCubeFragmentSlot(cost.MaterialItemId), cost.MaterialItemId, cube.Count - cost.MaterialCount);
                            return true;
                        }
                        break;
                    }
                }
            }

            if (material == null || material.StackCount < cost.MaterialCount)
                return false;

            var remainingCount = Math.Max(0, material.StackCount - cost.MaterialCount);
            if (remainingCount > 0)
            {
                _db.UpdateStackCount(connection, transaction, material.ItemUid, remainingCount);
                materialUpdate = CreateSlotCount(materialSlotIndex, material.ItemTemplateId, remainingCount);
            }
            else
            {
                _db.DeleteItem(connection, transaction, material.ItemUid);
                materialUpdate = CreateSlotCount(materialSlotIndex, material.ItemTemplateId, 0);
            }

            return true;
        }

        private static ItemUpgradeChanceEntry SelectChanceEntry(ItemUpgradeContext context)
        {
            if (context.ChanceEntries == null || context.ChanceEntries.Count == 0)
                return null;

            if (context.ChanceEntries.Count == 1)
                return context.ChanceEntries[0];

            var totalWeight = 0;
            foreach (var entry in context.ChanceEntries)
                totalWeight += Math.Max(0, entry.BaseSuccessWeight);

            if (totalWeight <= 0)
                return null;

            var roll = Random.Shared.Next(totalWeight);
            var cursor = 0;
            foreach (var entry in context.ChanceEntries)
            {
                cursor += Math.Max(0, entry.BaseSuccessWeight);
                if (roll < cursor)
                    return new ItemUpgradeChanceEntry
                    {
                        TargetLevel = entry.TargetLevel,
                        BaseSuccessWeight = WeightScale,
                        BaseFailureWeight = 0,
                    };
            }

            return context.ChanceEntries[context.ChanceEntries.Count - 1];
        }

        private static int CalculateFinalSuccessWeight(ItemUpgradeContext context, ItemUpgradeChanceEntry chance)
        {
            var baseWeight = Clamp(chance.BaseSuccessWeight, 0, WeightScale);
            if (context.Scene == ItemUpgradeScene.Ticket)
                return baseWeight;

            var weight = baseWeight + context.SuccessRateAddWeight;
            weight = (int)((long)weight * (WeightScale + context.SuccessRateBonusWeight) / WeightScale);
            return Clamp(weight, 0, WeightScale);
        }

        private static int ResolvePenaltyType(ItemUpgradeContext context, UpgradeTableRow row, ItemUpgradeTableKind tableKind)
        {
            if (context.Scene == ItemUpgradeScene.Ticket)
                return 1;

            return ItemUpgradeTableProvider.GetPenaltyType(tableKind, row, context.CurrentUpgradeLevel, context.EquipmentRarity);
        }

        private static byte ApplyPenalty(byte oldLevel, UpgradeTableRow row, int penaltyType, ItemUpgradeContext context)
        {
            if (context.FailureRetainLevel >= 0 && oldLevel >= context.ProtectTriggerLevel)
                return (byte)Clamp(context.FailureRetainLevel, 0, oldLevel);

            if (penaltyType == 2 && row != null)
                return (byte)Math.Max(0, oldLevel - Math.Max(0, row.PenaltyValue));

            return oldLevel;
        }

        private static InvenItem BuildUpgradeTargetItem(InventoryItemView view)
        {
            return new InvenItem
            {
                Slot = (byte)Math.Max(0, Math.Min(255, (int)view.Entry84.SlotIndex)),
                ItemId = view.Entry84.ItemTemplateId,
                Value = unchecked((uint)view.Entry84.Value),
                Attr = view.Upgrade,
                Durability = view.Durability,
                EnchantIndex = 0,
                EnchantUpgradeCount = view.EnchantUpgradeCount,
                AmplifyType = view.AmplifyType,
                AmplifyValue = view.AmplifyValue,
            };
        }

        private static PvfLib.StackableItemFile LoadMaterialStackable(SqliteInventoryStore.ItemRecord material)
        {
            if (material == null)
                return null;

            return ItemMetadataResolver.TryLoadStackableFile(material.ItemTemplateId, out var stackable)
                ? stackable
                : null;
        }

        private static bool IsStackableMaterial(SqliteInventoryStore.ItemRecord material, int expectedItemTemplateId)
        {
            return material != null
                && material.ItemTemplateId == expectedItemTemplateId
                && ItemMetadataResolver.GetStackableEntry(material.ItemTemplateId) != null;
        }

        private static ItemUpgradeConsumableConfig ResolveConsumable(int itemTemplateId, StackableItemFile stackable)
        {
            return ItemUpgradeConsumableResolver.TryResolve(itemTemplateId, stackable, out var config)
                ? config
                : null;
        }

        private static int LoadCubeFragmentCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int itemTemplateId)
        {
            var cubes = CurrencyService.LoadCubeFragments(connection, transaction, accountId);
            foreach (var cube in cubes)
            {
                if (cube.ItemId == itemTemplateId)
                    return cube.Count;
            }

            return 0;
        }

        private static bool IsImpossible(ItemUpgradeMode mode, ItemMetadata metadata)
        {
            if (metadata.ImpossibleContents == null)
                return false;

            var token = mode == ItemUpgradeMode.Amplify ? "amplify upgrade" : "upgrade";
            foreach (var item in metadata.ImpossibleContents)
            {
                if (string.Equals(item, token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsTitle(ItemMetadata metadata)
        {
            return string.Equals(metadata.EquipmentType, "[title name]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsItemLocked(SqliteConnection connection, SqliteTransaction transaction, int characterId, SqliteInventoryStore.ItemRecord target)
        {
            if (target == null || target.EquipmentLockId == 0)
                return false;

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT COUNT(1)
FROM character_item_locks
WHERE character_id = @cid
  AND state != 0
  AND equipment_lock_id = @lockId;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@lockId", (int)target.EquipmentLockId);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        private static ItemUpgradeSlotCount CreateSlotCount(short slotIndex, int itemTemplateId, int count)
        {
            return new ItemUpgradeSlotCount
            {
                SlotIndex = slotIndex,
                ItemTemplateId = itemTemplateId,
                Count = Math.Max(0, count),
            };
        }

        private static bool ResolveUpgradeLogEnabled()
        {
            var value = Environment.GetEnvironmentVariable("DFO_ITEM_UPGRADE_LOG");
            if (string.IsNullOrWhiteSpace(value))
                return true;

            value = value.Trim();
            return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private sealed class ProtectTicketSelection
        {
            public SqliteInventoryStore.ItemRecord Item { get; set; }
            public ItemUpgradeConsumableConfig Config { get; set; }
        }
    }
}
