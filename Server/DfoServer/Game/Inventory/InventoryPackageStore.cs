using DfoServer.Game.Accounts;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryPackageStore
    {
        private const int MagicHammerMaterialItemTemplateId = 10007367;
        private const int MagicHammerBoxItemTemplateId = 10007368;
        private const int MagicHammerBundleMinItemTemplateId = 10007472;
        private const int MagicHammerBundleMaxItemTemplateId = 10007477;
        private readonly InventoryDbPrimitives _db;
        private readonly InventoryAuditLogger _auditLogger;

        internal InventoryPackageStore(InventoryDbPrimitives db, InventoryAuditLogger auditLogger)
        {
            _db = db;
            _auditLogger = auditLogger;
        }

        public bool TryOpenAvatarPackage(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, AvatarPackageOpenRequest request, out AvatarPackageOpenResult result)
        {
            result = null;
            if (request == null || request.Choices.Count == 0)
                return false;

            var packageItem = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.SlotIndex);
            if (packageItem == null || packageItem.StackCount <= 0)
            {
                FileLogger.Log($"  [AvatarPackage] REJECT: no usable package at slot={request.SlotIndex}");
                return false;
            }

            if (!AvatarPackageDefinitionResolver.TryResolve(packageItem.ItemTemplateId, out var definition))
            {
                FileLogger.Log($"  [AvatarPackage] REJECT: item=0x{packageItem.ItemTemplateId:X8} is not a supported avatar package");
                return false;
            }

            if (!ValidateAvatarPackageChoices(definition, request, out var optionByItemId))
            {
                FileLogger.Log($"  [AvatarPackage] REJECT: choices do not match package item=0x{packageItem.ItemTemplateId:X8}");
                return false;
            }

            var addedAvatarCount = 0;
            var addedMainItemCount = 0;
            var addedPetCount = 0;
            var grantedItems = new List<PackageGrantedItem>();

            if (!_db.ConsumePackageItem(connection, transaction, packageItem))
                return false;

            foreach (var reward in definition.Rewards)
            {
                if (reward.IsAvatar)
                {
                    var optionValue = optionByItemId[reward.ItemTemplateId];
                    for (var i = 0; i < reward.Count; i++)
                    {
                        var targetSlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Avatar, 0, 500);
                        if (targetSlot < 0)
                        {
                            FileLogger.Log($"  [AvatarPackage] REJECT: no avatar slot for item=0x{reward.ItemTemplateId:X8}");
                            return false;
                        }

                        _db.InsertCharacterItem(
                            connection,
                            transaction,
                            characterId,
                            InventoryListType.Avatar,
                            (short)targetSlot,
                            reward.ItemTemplateId,
                            "avatar",
                            0,
                            0,
                            0,
                            0,
                            optionValue,
                            0,
                            SqliteInventoryStore.DefaultAvatarUnknownFixed30,
                            0,
                            SqliteInventoryStore.CreateDefaultAvatarExtraJson());
                        grantedItems.Add(new PackageGrantedItem
                        {
                            ListType = InventoryListType.Avatar,
                            SlotIndex = (short)targetSlot,
                            ItemTemplateId = reward.ItemTemplateId,
                            DisplayCount = 1,
                            Durability = 0,
                        });
                        addedAvatarCount++;
                    }

                    continue;
                }

                if (!TryInsertPackageReward(connection, transaction, characterId, reward, ref addedMainItemCount, ref addedPetCount, grantedItems, packageItem.SlotIndex))
                {
                    FileLogger.Log($"  [AvatarPackage] REJECT: cannot insert package reward item=0x{reward.ItemTemplateId:X8} count={reward.Count}");
                    return false;
                }
            }

            _auditLogger.WriteOpenPackageAuditLog(connection, transaction, characterId, packageItem, addedAvatarCount, addedMainItemCount, addedPetCount);

            result = new AvatarPackageOpenResult
            {
                SlotIndex = request.SlotIndex,
                PackageItemTemplateId = packageItem.ItemTemplateId,
                SourceRemainingStackCount = Math.Max(0, packageItem.StackCount - 1),
                AddedAvatarCount = addedAvatarCount,
                AddedMainItemCount = addedMainItemCount,
                AddedPetCount = addedPetCount,
            };
            result.GrantedItems.AddRange(grantedItems);
            return true;
        }

        public bool TryOpenSelectablePackage(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, SelectablePackageOpenRequest request, out SelectablePackageOpenResult result)
        {
            result = null;
            if (request == null)
                return false;

            var packageItem = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.SlotIndex);
            if (packageItem == null || packageItem.StackCount <= 0)
            {
                FileLogger.Log($"  [SelectablePackage] REJECT: no usable package at slot={request.SlotIndex}");
                return false;
            }

            if (packageItem.ExpireTime > 0 && packageItem.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
            {
                FileLogger.Log($"  [SelectablePackage] REJECT: expired package item=0x{packageItem.ItemTemplateId:X8} expire={packageItem.ExpireTime}");
                return false;
            }

            if (!SelectablePackageDefinitionResolver.TryResolve(packageItem.ItemTemplateId, out var definition))
            {
                FileLogger.Log($"  [SelectablePackage] REJECT: item=0x{packageItem.ItemTemplateId:X8} has no selectable package data");
                return false;
            }

            var addedMainItemCount = 0;
            var addedAvatarCount = 0;
            var addedPetCount = 0;
            var grantedItems = new List<PackageGrantedItem>();
            PackageRewardEntry rewardForResult = null;

            if (!_db.ConsumePackageItem(connection, transaction, packageItem))
                return false;

            if (request.HasAvatarChoices)
            {
                var seenAvatarChoices = new HashSet<int>();
                if (!DefinitionHasAvatarReward(definition))
                {
                    FileLogger.Log($"  [SelectablePackage] REJECT: package item=0x{packageItem.ItemTemplateId:X8} has no avatar rewards for long 0x00A0 body");
                    return false;
                }

                foreach (var choice in request.AvatarChoices)
                {
                    if (!seenAvatarChoices.Add(choice.ItemTemplateId))
                    {
                        FileLogger.Log($"  [SelectablePackage] REJECT: duplicate avatar choice item=0x{choice.ItemTemplateId:X8}");
                        return false;
                    }

                    if (!SelectablePackageDefinitionResolver.IsAvatarEquipment(choice.ItemTemplateId))
                    {
                        FileLogger.Log($"  [SelectablePackage] REJECT: avatar choice item=0x{choice.ItemTemplateId:X8} is not valid for package item=0x{packageItem.ItemTemplateId:X8}");
                        return false;
                    }

                    if (!definition.TryGetReward(choice.ItemTemplateId, out var avatarReward))
                    {
                        avatarReward = new PackageRewardEntry
                        {
                            ItemTemplateId = choice.ItemTemplateId,
                            Count = 1,
                            ExpireTime = SelectablePackageDefinitionResolver.ResolveItemExpirationUnixTime(choice.ItemTemplateId),
                        };
                        FileLogger.Log($"  [SelectablePackage] WARN: avatar choice item=0x{choice.ItemTemplateId:X8} accepted by equipment metadata but missing from parsed package rewards item=0x{packageItem.ItemTemplateId:X8}");
                    }

                    if (rewardForResult == null)
                        rewardForResult = avatarReward;

                    if (avatarReward.ExpireTime > 0 && avatarReward.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                    {
                        FileLogger.Log($"  [SelectablePackage] REJECT: avatar choice item=0x{choice.ItemTemplateId:X8} expired at {avatarReward.ExpireTime}");
                        return false;
                    }

                    var targetSlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Avatar, 0, 500);
                    if (targetSlot < 0)
                    {
                        FileLogger.Log($"  [SelectablePackage] REJECT: no avatar slot for selected item=0x{choice.ItemTemplateId:X8}");
                        return false;
                    }

                    _db.InsertCharacterItem(
                        connection,
                        transaction,
                        characterId,
                        InventoryListType.Avatar,
                        (short)targetSlot,
                        choice.ItemTemplateId,
                        "avatar",
                        0,
                        0,
                        0,
                        0,
                        choice.OptionValue,
                        0,
                        SqliteInventoryStore.DefaultAvatarUnknownFixed30,
                        0,
                        SqliteInventoryStore.CreateDefaultAvatarExtraJson());
                    grantedItems.Add(new PackageGrantedItem
                    {
                        ListType = InventoryListType.Avatar,
                        SlotIndex = (short)targetSlot,
                        ItemTemplateId = choice.ItemTemplateId,
                        DisplayCount = 1,
                        Durability = 0,
                    });
                    addedAvatarCount++;
                }
            }
            else
            {
                if (!definition.TryGetReward(request.SelectedItemTemplateId, out var reward))
                {
                    if (!DefinitionHasAvatarReward(definition) ||
                        !SelectablePackageDefinitionResolver.IsAvatarEquipment(request.SelectedItemTemplateId))
                    {
                        FileLogger.Log($"  [SelectablePackage] REJECT: selected item=0x{request.SelectedItemTemplateId:X8} is not in package item=0x{packageItem.ItemTemplateId:X8}");
                        return false;
                    }

                    reward = new PackageRewardEntry
                    {
                        ItemTemplateId = request.SelectedItemTemplateId,
                        Count = 1,
                        ExpireTime = SelectablePackageDefinitionResolver.ResolveItemExpirationUnixTime(request.SelectedItemTemplateId),
                    };
                    FileLogger.Log($"  [SelectablePackage] WARN: selected avatar item=0x{request.SelectedItemTemplateId:X8} accepted by equipment metadata but missing from parsed package rewards item=0x{packageItem.ItemTemplateId:X8}");
                }

                if (reward.ExpireTime > 0 && reward.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                {
                    FileLogger.Log($"  [SelectablePackage] REJECT: selected reward item=0x{reward.ItemTemplateId:X8} expired at {reward.ExpireTime}");
                    return false;
                }

                var metadata = ItemMetadataResolver.Resolve(reward.ItemTemplateId);
                if (metadata.ItemKind == "special")
                {
                    FileLogger.Log($"  [SelectablePackage] REJECT: selected reward item=0x{reward.ItemTemplateId:X8} has unsupported metadata");
                    return false;
                }

                if (SelectablePackageDefinitionResolver.IsAvatarEquipment(reward.ItemTemplateId))
                {
                    var targetSlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Avatar, 0, 500);
                    if (targetSlot < 0)
                    {
                        FileLogger.Log($"  [SelectablePackage] REJECT: no avatar slot for selected item=0x{reward.ItemTemplateId:X8}");
                        return false;
                    }

                    _db.InsertCharacterItem(
                        connection,
                        transaction,
                        characterId,
                        InventoryListType.Avatar,
                        (short)targetSlot,
                        reward.ItemTemplateId,
                        "avatar",
                        0,
                        0,
                        0,
                        0,
                        request.SelectionFlag,
                        0,
                        SqliteInventoryStore.DefaultAvatarUnknownFixed30,
                        0,
                        SqliteInventoryStore.CreateDefaultAvatarExtraJson());
                    grantedItems.Add(new PackageGrantedItem
                    {
                        ListType = InventoryListType.Avatar,
                        SlotIndex = (short)targetSlot,
                        ItemTemplateId = reward.ItemTemplateId,
                        DisplayCount = 1,
                        Durability = 0,
                    });
                    addedAvatarCount++;
                }
                else if (!TryInsertPackageReward(connection, transaction, characterId, reward, ref addedMainItemCount, ref addedPetCount, grantedItems, packageItem.SlotIndex))
                {
                    FileLogger.Log($"  [SelectablePackage] REJECT: cannot insert selected reward item=0x{reward.ItemTemplateId:X8} count={reward.Count}");
                    return false;
                }

                rewardForResult = reward;
            }

            if (rewardForResult == null)
                rewardForResult = new PackageRewardEntry { ItemTemplateId = request.SelectedItemTemplateId, Count = Math.Max(1, request.AvatarChoices.Count) };

            _auditLogger.WriteOpenSelectablePackageAuditLog(connection, transaction, characterId, packageItem, rewardForResult, addedMainItemCount, addedPetCount);

            result = new SelectablePackageOpenResult
            {
                SlotIndex = request.SlotIndex,
                PackageItemTemplateId = packageItem.ItemTemplateId,
                SourceRemainingStackCount = Math.Max(0, packageItem.StackCount - 1),
                RewardItemTemplateId = rewardForResult.ItemTemplateId,
                AddedMainItemCount = addedMainItemCount,
                AddedAvatarCount = addedAvatarCount,
                AddedPetCount = addedPetCount,
            };
            result.GrantedItems.AddRange(grantedItems);
            return true;
        }

        public bool TryUseBoosterItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, BoosterUseRequest request, out BoosterUseResult result)
        {
            result = null;
            request = request ?? new BoosterUseRequest();
            var selectedItemTemplateIds = request.SelectedItemTemplateIds ?? Array.Empty<int>();

            var source = ResolveBoosterSource(
                connection,
                transaction,
                characterId,
                request.SlotIndex,
                request.ExpectedItemTemplateId);
            if (source == null)
                return false;
            if (request.ExpectedItemTemplateId > 0 && source.ItemTemplateId != request.ExpectedItemTemplateId)
                return false;
            var requestedCount = Math.Max(1, request.RequestedCount);
            if (source.StackCount <= 0 || source.StackCount < requestedCount)
            {
                FileLogger.Log($"  [Booster] REJECT: item=0x{source.ItemTemplateId:X8} slot={source.SlotIndex} need={requestedCount}, have={source.StackCount}");
                return false;
            }

            var stackable = InventoryDbPrimitives.LoadStackableItem(source.ItemTemplateId);
            if (stackable == null)
                return false;

            var stackableType = NormalizeStackableType(stackable.StackableType);
            ResolveNeedMaterial(source.ItemTemplateId, stackable, out var materialItemTemplateId, out var materialCountPerUse);
            if (request.ExpectedMaterialItemTemplateId > 0 && materialItemTemplateId > 0 && materialItemTemplateId != request.ExpectedMaterialItemTemplateId)
                return false;

            SqliteInventoryStore.ItemRecord material = null;
            if (materialItemTemplateId > 0 && materialCountPerUse > 0)
            {
                var materialMetadata = ItemMetadataResolver.Resolve(materialItemTemplateId);
                materialMetadata.GetSlotRange(out var materialSlotStart, out var materialSlotEnd);
                material = request.MaterialSlotIndex.HasValue
                    ? _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.MaterialSlotIndex.Value)
                    : null;
                if (material == null || material.ItemTemplateId != materialItemTemplateId)
                {
                    var fallbackMaterial = _db.FindItemByTemplateIdInRange(connection, transaction, characterId, InventoryListType.Main, materialItemTemplateId, materialSlotStart, materialSlotEnd);
                    if (fallbackMaterial != null && request.MaterialSlotIndex.HasValue && fallbackMaterial.SlotIndex != request.MaterialSlotIndex.Value)
                    {
                        FileLogger.Log($"  [Booster] WARN: material slot stale requested={request.MaterialSlotIndex.Value}, actual={fallbackMaterial.SlotIndex}, item=0x{materialItemTemplateId:X8}");
                    }

                    material = fallbackMaterial;
                }
                if (material == null
                    || material.ItemTemplateId != materialItemTemplateId
                    || material.SlotIndex < materialSlotStart
                    || material.SlotIndex > materialSlotEnd)
                {
                    FileLogger.Log($"  [Booster] REJECT: need material=0x{materialItemTemplateId:X8} at slot={(request.MaterialSlotIndex.HasValue ? request.MaterialSlotIndex.Value.ToString() : "auto")} range={materialSlotStart}-{materialSlotEnd}, found=0x{material?.ItemTemplateId ?? 0:X8}@{material?.SlotIndex ?? 0}");
                    return false;
                }
            }

            var totalMaterialCountLong = (long)materialCountPerUse * requestedCount;
            if (totalMaterialCountLong > int.MaxValue)
                return false;
            var totalMaterialCount = (int)totalMaterialCountLong;
            if (material != null && material.StackCount < totalMaterialCount)
            {
                FileLogger.Log($"  [Booster] REJECT: need {totalMaterialCount}x material=0x{materialItemTemplateId:X8}, have={material.StackCount}");
                return false;
            }

            var isSeriaLuckValueSource = source.ItemTemplateId == SeriaLuckItemConstants.ItemTemplateId;
            var seriaLuckValueBefore = isSeriaLuckValueSource
                ? SqliteAccountRepository.LoadSeriaLuckValue(connection, transaction, accountId)
                : 0;
            var seriaLuckValue = seriaLuckValueBefore;
            var displayRewardEntries = new List<PvfLib.BoosterRewardEntry>();
            var doubleRewardEntries = new List<PvfLib.BoosterRewardEntry>();
            var rewardsToGrant = new List<PvfLib.BoosterRewardEntry>();
            for (var useIndex = 0; useIndex < requestedCount; useIndex++)
            {
                if (!TryResolvePackageRewards(source.ItemTemplateId, stackable, stackableType, selectedItemTemplateIds, out var rewards))
                {
                    var selectedText = selectedItemTemplateIds.Count == 0
                        ? "none"
                        : string.Join(",", selectedItemTemplateIds.Select(id => $"0x{id:X8}"));
                    FileLogger.Log($"  [Booster] unsupported/empty item=0x{source.ItemTemplateId:X8} type={stackableType} selected={selectedText} rewards(random={stackable.BoosterRewards.Count},select={stackable.BoosterSelectionRewards.Count},package={stackable.PackageRewards.Count},randombox={stackable.RandomBoxRewards.Count})");
                    return false;
                }

                var validRewards = NormalizeRewardEntries(rewards);
                if (validRewards.Count == 0)
                {
                    FileLogger.Log($"  [Booster] REJECT: item=0x{source.ItemTemplateId:X8} resolved only invalid rewards");
                    return false;
                }

                AddRewardEntries(displayRewardEntries, validRewards);
                rewardsToGrant.AddRange(validRewards);
                if (!isSeriaLuckValueSource)
                    continue;

                if (seriaLuckValue >= SqliteAccountRepository.SeriaLuckValueMax)
                {
                    AddRewardEntries(doubleRewardEntries, validRewards);
                    AddRewardEntries(rewardsToGrant, validRewards);
                    seriaLuckValue = 0;
                }

                seriaLuckValue = Math.Min(SqliteAccountRepository.SeriaLuckValueMax, seriaLuckValue + 1);
            }

            if (!TryConsumeStackableCount(connection, transaction, source, requestedCount))
                return false;
            if (material != null && material.ItemUid != source.ItemUid && !TryConsumeStackableCount(connection, transaction, material, totalMaterialCount))
                return false;

            var useResult = new BoosterUseResult
            {
                SourceSlotIndex = source.SlotIndex,
                SourceItemTemplateId = source.ItemTemplateId,
                SourceRemainingStackCount = Math.Max(0, source.StackCount - requestedCount),
                SourceInstanceValue = source.InstanceValue,
                ConsumedSourceCount = requestedCount,
                ConsumedMaterialItemTemplateId = materialItemTemplateId,
                ConsumedMaterialCount = material == null ? 0 : totalMaterialCount,
                ConsumedMaterialSlotIndex = material?.SlotIndex ?? 0,
                ConsumedMaterialRemainingStackCount = material == null ? 0 : Math.Max(0, material.StackCount - totalMaterialCount),
                IsSeriaLuckValueSource = isSeriaLuckValueSource,
                SeriaLuckValueBefore = seriaLuckValueBefore,
                SeriaLuckValueAfter = isSeriaLuckValueSource ? seriaLuckValue : 0,
                SeriaLuckValueMax = SqliteAccountRepository.SeriaLuckValueMax,
                SeriaLuckDoubleTriggered = doubleRewardEntries.Count > 0,
            };
            AddDisplayRewards(useResult.DisplayRewards, displayRewardEntries);
            AddDisplayRewards(useResult.DoubleRewards, doubleRewardEntries);

            foreach (var reward in AggregateRewards(rewardsToGrant))
            {
                if (!_db.TryAddBoosterRewardItems(connection, transaction, characterId, accountId, reward.ItemId, reward.Count, out var rewardResults))
                    return false;

                useResult.Rewards.AddRange(rewardResults);
            }

            if (isSeriaLuckValueSource)
                SqliteAccountRepository.UpdateSeriaLuckValue(connection, transaction, accountId, seriaLuckValue);

            _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, source, requestedCount);
            if (material != null && material.ItemUid != source.ItemUid)
                _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, material, totalMaterialCount);
            foreach (var reward in useResult.Rewards)
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, reward.ItemTemplateId, reward.SlotIndex, 0, 0);
            result = useResult;
            return true;
        }

        private SqliteInventoryStore.ItemRecord ResolveBoosterSource(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            short? slotIndex,
            int expectedItemTemplateId)
        {
            var source = slotIndex.HasValue
                ? _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, slotIndex.Value)
                : _db.FindFirstPackageItem(connection, transaction, characterId);
            if (source != null && (expectedItemTemplateId <= 0 || source.ItemTemplateId == expectedItemTemplateId))
                return source;

            if (expectedItemTemplateId <= 0)
                return source;

            var metadata = ItemMetadataResolver.Resolve(expectedItemTemplateId);
            metadata.GetSlotRange(out var slotStart, out var slotEnd);
            var fallback = _db.FindItemByTemplateIdInRange(connection, transaction, characterId, InventoryListType.Main, expectedItemTemplateId, slotStart, slotEnd);
            if (fallback != null && slotIndex.HasValue && fallback.SlotIndex != slotIndex.Value)
            {
                FileLogger.Log($"  [Booster] WARN: source slot stale requested={slotIndex.Value}, actual={fallback.SlotIndex}, item=0x{expectedItemTemplateId:X8}");
            }

            return fallback;
        }

        public bool TryOpenPackage0207(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, short slotIndex, IReadOnlyList<int> selectedItemTemplateIds, out BoosterUseResult result)
        {
            result = null;
            if (selectedItemTemplateIds == null || selectedItemTemplateIds.Count == 0)
                return false;

            var source = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, slotIndex);
            if (source == null)
                return false;

            var stackable = InventoryDbPrimitives.LoadStackableItem(source.ItemTemplateId);
            if (stackable == null)
                return false;

            var stackableType = NormalizeStackableType(stackable.StackableType);
            if (!stackableType.Equals("[usable cera package]", StringComparison.OrdinalIgnoreCase)
                && !stackableType.Equals("[booster selection]", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!TryResolveClientSelectedRewards(stackable, selectedItemTemplateIds, out var rewards))
            {
                FileLogger.Log($"  [OpenPkg0207] PVF validation failed source=0x{source.ItemTemplateId:X8} selected={string.Join(",", selectedItemTemplateIds.Select(id => $"0x{id:X8}"))}");
                return false;
            }

            if (!_db.ConsumeOneStackable(connection, transaction, source))
                return false;

            var useResult = new BoosterUseResult
            {
                SourceSlotIndex = source.SlotIndex,
                SourceItemTemplateId = source.ItemTemplateId,
                SourceRemainingStackCount = Math.Max(0, source.StackCount - 1),
                SourceInstanceValue = source.InstanceValue,
            };

            foreach (var reward in rewards)
            {
                if (!_db.TryAddBoosterRewardItems(connection, transaction, characterId, accountId, reward.ItemId, reward.Count, out var rewardResults))
                    return false;

                useResult.Rewards.AddRange(rewardResults);
            }

            _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, source, 1);
            foreach (var reward in useResult.Rewards)
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, reward.ItemTemplateId, reward.SlotIndex, 0, 0);
            result = useResult;
            return true;
        }

        private bool TryInsertPackageReward(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            PackageRewardEntry reward,
            ref int addedMainItemCount,
            ref int addedPetCount,
            List<PackageGrantedItem> grantedItems = null,
            short? sourceSlotToUseLast = null)
        {
            if (reward.ExpireTime > 0 && reward.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
            {
                FileLogger.Log($"  [AvatarPackage] SKIP expired reward item=0x{reward.ItemTemplateId:X8} count={reward.Count} expire={reward.ExpireTime}");
                return true;
            }

            var metadata = ItemMetadataResolver.Resolve(reward.ItemTemplateId);
            if (metadata.ItemKind == "special")
            {
                FileLogger.Log($"  [AvatarPackage] SKIP unsupported special reward item=0x{reward.ItemTemplateId:X8} count={reward.Count}");
                return true;
            }

            var isPetConsumable = ItemMetadataResolver.IsPetConsumableItem(metadata);
            var stackListType = isPetConsumable ? InventoryListType.Pet : InventoryListType.Main;
            if (metadata.IsStackable)
            {
                var remaining = reward.Count;
                var existingItem = FindPackageRewardStackTarget(
                    connection,
                    transaction,
                    characterId,
                    stackListType,
                    reward,
                    metadata);
                while (existingItem != null && remaining > 0)
                {
                    var capacity = metadata.StackLimit > 0 ? Math.Max(0, metadata.StackLimit - existingItem.StackCount) : remaining;
                    var addCount = Math.Min(remaining, capacity);
                    if (addCount > 0)
                    {
                        if (isPetConsumable)
                            _db.UpdatePetStackCount(connection, transaction, existingItem.ItemUid, existingItem.StackCount + addCount);
                        else
                            _db.UpdateStackCount(connection, transaction, existingItem.ItemUid, existingItem.StackCount + addCount);
                        grantedItems?.Add(new PackageGrantedItem
                        {
                            ListType = stackListType,
                            SlotIndex = existingItem.SlotIndex,
                            ItemTemplateId = reward.ItemTemplateId,
                            DisplayCount = addCount,
                            Durability = 0,
                        });
                        remaining -= addCount;
                    }

                    existingItem = FindPackageRewardStackTarget(
                        connection,
                        transaction,
                        characterId,
                        stackListType,
                        reward,
                        metadata);
                }

                while (remaining > 0)
                {
                    var insertCount = metadata.StackLimit > 0 ? Math.Min(metadata.StackLimit, remaining) : remaining;
                    int slotStart, slotEnd;
                    if (isPetConsumable)
                    {
                        slotStart = SqliteInventoryStore.PetConsumableSlotStart;
                        slotEnd = SqliteInventoryStore.PetConsumableSlotEnd;
                    }
                    else
                    {
                        metadata.GetSlotRange(out slotStart, out slotEnd);
                    }

                    var targetSlot = _db.FindEmptySlotPreferOther(connection, transaction, characterId, stackListType, slotStart, slotEnd, sourceSlotToUseLast);
                    if (targetSlot < 0)
                        return false;

                    _db.InsertCharacterItem(
                        connection,
                        transaction,
                        characterId,
                        stackListType,
                        (short)targetSlot,
                        reward.ItemTemplateId,
                        isPetConsumable ? "pet" : metadata.ItemKind,
                        insertCount,
                        insertCount,
                        0,
                        0,
                        0,
                        reward.ExpireTime,
                        0,
                        isPetConsumable ? insertCount : 0,
                        "{}");
                    grantedItems?.Add(new PackageGrantedItem
                    {
                        ListType = stackListType,
                        SlotIndex = (short)targetSlot,
                        ItemTemplateId = reward.ItemTemplateId,
                        DisplayCount = insertCount,
                        Durability = 0,
                    });
                    remaining -= insertCount;
                }

                if (isPetConsumable)
                    addedPetCount += reward.Count;
                else
                    addedMainItemCount += reward.Count;
                return true;
            }

            var isPetEquipment = string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal) &&
                ItemMetadataResolver.IsPetInventoryEquipment(reward.ItemTemplateId);
            var isCreature = isPetEquipment && SqliteInventoryStore.IsCreatureItem(reward.ItemTemplateId);
            var isPetArtifactEquipment = isPetEquipment && !isCreature;
            for (var i = 0; i < reward.Count; i++)
            {
                if (isCreature || isPetArtifactEquipment)
                {
                    var petSlotStart = isCreature ? SqliteInventoryStore.PetInventorySlotStart : SqliteInventoryStore.PetEquipmentSlotStart;
                    var petSlotEnd = isCreature ? SqliteInventoryStore.PetInventorySlotEnd : SqliteInventoryStore.PetEquipmentSlotEnd;
                    var petSlot = _db.FindEmptySlot(connection, transaction, characterId, InventoryListType.Pet, petSlotStart, petSlotEnd);
                    if (petSlot < 0)
                        return false;

                    _db.InsertCharacterItem(
                        connection,
                        transaction,
                        characterId,
                        InventoryListType.Pet,
                        (short)petSlot,
                        reward.ItemTemplateId,
                        "pet",
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        isCreature ? _db.NextPetSerialOrHandle(connection, transaction, characterId) : 0,
                        "{}");
                    grantedItems?.Add(new PackageGrantedItem
                    {
                        ListType = InventoryListType.Pet,
                        SlotIndex = (short)petSlot,
                        ItemTemplateId = reward.ItemTemplateId,
                        DisplayCount = 1,
                        Durability = 0,
                    });
                    addedPetCount++;
                    continue;
                }

                int slotStart, slotEnd;
                metadata.GetSlotRange(out slotStart, out slotEnd);
                var targetSlot = _db.FindEmptySlotPreferOther(connection, transaction, characterId, InventoryListType.Main, slotStart, slotEnd, sourceSlotToUseLast);
                if (targetSlot < 0)
                    return false;

                var instanceValue = InventoryDbPrimitives.GenerateInstanceValue(reward.ItemTemplateId, targetSlot);
                var pkgSealFlag = metadata.IsSealed ? (byte)1 : (byte)0;
                _db.InsertCharacterItem(
                    connection,
                    transaction,
                    characterId,
                    InventoryListType.Main,
                    (short)targetSlot,
                    reward.ItemTemplateId,
                    metadata.ItemKind,
                    instanceValue,
                    instanceValue,
                    metadata.Durability,
                    pkgSealFlag,
                    0,
                    reward.ExpireTime,
                    -1,
                    0,
                    "{}");
                grantedItems?.Add(new PackageGrantedItem
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = (short)targetSlot,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = 1,
                    Durability = metadata.Durability,
                });
                addedMainItemCount++;
            }

            return true;
        }

        private SqliteInventoryStore.ItemRecord FindPackageRewardStackTarget(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            InventoryListType stackListType,
            PackageRewardEntry reward,
            ItemMetadata metadata)
        {
            if (stackListType == InventoryListType.Main && IsQuickSlotConsumable(metadata))
            {
                var quickSlotItem = _db.FindStackableItemByTemplateIdAndExpireTime(
                    connection,
                    transaction,
                    characterId,
                    stackListType,
                    reward.ItemTemplateId,
                    reward.ExpireTime,
                    metadata.StackLimit,
                    SqliteInventoryStore.QuickSlotStart,
                    SqliteInventoryStore.QuickSlotEnd);
                if (quickSlotItem != null)
                    return quickSlotItem;
            }

            return _db.FindStackableItemByTemplateIdAndExpireTime(
                connection,
                transaction,
                characterId,
                stackListType,
                reward.ItemTemplateId,
                reward.ExpireTime,
                metadata.StackLimit);
        }

        private static bool IsQuickSlotConsumable(ItemMetadata metadata)
        {
            return metadata != null &&
                metadata.IsStackable &&
                metadata.StackableType != null &&
                metadata.StackableType.IndexOf("[waste]", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ValidateAvatarPackageChoices(
            AvatarPackageDefinition definition,
            AvatarPackageOpenRequest request,
            out Dictionary<int, byte> optionByItemId)
        {
            optionByItemId = new Dictionary<int, byte>();
            if (definition == null || request == null)
                return false;

            if (request.Choices.Count != definition.AvatarItemIds.Count)
                return false;

            foreach (var choice in request.Choices)
            {
                if (!definition.AvatarItemIds.Contains(choice.ItemTemplateId))
                    return false;
                if (optionByItemId.ContainsKey(choice.ItemTemplateId))
                    return false;

                optionByItemId[choice.ItemTemplateId] = choice.OptionValue;
            }

            return true;
        }

        private static bool DefinitionHasAvatarReward(SelectablePackageDefinition definition)
        {
            if (definition == null || definition.Rewards == null)
                return false;

            foreach (var reward in definition.Rewards)
            {
                if (SelectablePackageDefinitionResolver.IsAvatarEquipment(reward.ItemTemplateId))
                    return true;
            }

            return false;
        }

        private bool TryConsumeStackableCount(SqliteConnection connection, SqliteTransaction transaction, SqliteInventoryStore.ItemRecord item, int count)
        {
            if (item == null || count <= 0 || item.StackCount < count)
                return false;

            var remaining = item.StackCount - count;
            if (remaining > 0)
                _db.UpdateStackCount(connection, transaction, item.ItemUid, remaining);
            else
                _db.DeleteItem(connection, transaction, item.ItemUid);

            return true;
        }

        private static void ResolveNeedMaterial(int sourceItemTemplateId, PvfLib.StackableItemFile stackable, out int materialItemTemplateId, out int materialCountPerUse)
        {
            materialItemTemplateId = 0;
            materialCountPerUse = 0;

            if (stackable?.RandomBoxRemovalItems != null)
            {
                foreach (var item in stackable.RandomBoxRemovalItems)
                {
                    if (item == null || item.ItemId <= 0 || item.Count <= 0 || item.ItemId == sourceItemTemplateId)
                        continue;

                    materialItemTemplateId = item.ItemId;
                    materialCountPerUse = item.Count;
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(stackable?.NeedMaterial))
                return;

            var parts = stackable.NeedMaterial.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return;

            int.TryParse(parts[0], out materialItemTemplateId);
            int.TryParse(parts[1], out materialCountPerUse);
        }

        private static List<PvfLib.BoosterRewardEntry> AggregateRewards(IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            if (rewards == null)
                return new List<PvfLib.BoosterRewardEntry>();

            return rewards
                .Where(reward => reward != null && reward.ItemId > 0 && reward.Count > 0)
                .GroupBy(reward => reward.ItemId)
                .Select(group => new PvfLib.BoosterRewardEntry
                {
                    ItemId = group.Key,
                    Count = group.Sum(reward => Math.Max(1, reward.Count)),
                    Weight = 10000,
                })
                .ToList();
        }

        private static List<PvfLib.BoosterRewardEntry> NormalizeRewardEntries(IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            if (rewards == null)
                return new List<PvfLib.BoosterRewardEntry>();

            return rewards
                .Where(reward => reward != null && reward.ItemId > 0 && reward.Count > 0)
                .Select(reward => new PvfLib.BoosterRewardEntry
                {
                    ItemId = reward.ItemId,
                    Count = Math.Max(1, reward.Count),
                    Weight = reward.Weight,
                    Group = reward.Group,
                    DrawCount = reward.DrawCount,
                })
                .ToList();
        }

        private static void AddDisplayRewards(List<PackageGrantedItem> target, IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            if (target == null || rewards == null)
                return;

            foreach (var reward in rewards)
            {
                if (reward == null || reward.ItemId <= 0 || reward.Count <= 0)
                    continue;

                target.Add(new PackageGrantedItem
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = 0,
                    ItemTemplateId = reward.ItemId,
                    DisplayCount = Math.Max(1, reward.Count),
                    Durability = 0,
                });
            }
        }

        private static void AddRewardEntries(List<PvfLib.BoosterRewardEntry> target, IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            if (target == null || rewards == null)
                return;

            foreach (var reward in rewards)
            {
                var clone = CloneRewardEntry(reward);
                if (clone != null && clone.ItemId > 0 && clone.Count > 0)
                    target.Add(clone);
            }
        }

        private static PvfLib.BoosterRewardEntry CloneRewardEntry(PvfLib.BoosterRewardEntry reward)
        {
            if (reward == null)
                return null;

            return new PvfLib.BoosterRewardEntry
            {
                ItemId = reward.ItemId,
                Count = Math.Max(1, reward.Count),
                Weight = reward.Weight,
                Group = reward.Group,
                DrawCount = reward.DrawCount,
            };
        }

        private static bool TryResolvePackageRewards(int sourceItemTemplateId, PvfLib.StackableItemFile stackable, string stackableType, IReadOnlyList<int> selectedItemTemplateIds, out List<PvfLib.BoosterRewardEntry> rewards)
        {
            rewards = new List<PvfLib.BoosterRewardEntry>();
            if (stackableType.Equals("[booster]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[cera booster]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[booster random]", StringComparison.OrdinalIgnoreCase))
            {
                // [booster random] 与 [booster] 一样把奖励池声明在 [booster info](stackable.BoosterRewards),
                // 之前未登记 -> 开箱走不到任何分支, 静默无奖励(eg 10093384 斗兽场礼包)。
                if (TryResolveMagicHammerBundleRewards(sourceItemTemplateId, stackable, out rewards))
                    return true;

                rewards = RollBoosterRewards(stackable.BoosterRewards);
                return rewards.Count > 0;
            }

            if (stackableType.Equals("[cera package]", StringComparison.OrdinalIgnoreCase))
            {
                rewards = stackable.PackageRewards.ToList();
                return rewards.Count > 0;
            }

            if (stackableType.Equals("[usable cera package]", StringComparison.OrdinalIgnoreCase))
                return TryResolveClientSelectedRewards(stackable, selectedItemTemplateIds, out rewards);

            if (stackableType.Equals("[random upgradable legacy]", StringComparison.OrdinalIgnoreCase))
            {
                rewards = RollBoosterRewards(stackable.RandomBoxRewards);
                return rewards.Count > 0;
            }

            if (stackableType.Equals("[booster selection]", StringComparison.OrdinalIgnoreCase))
            {
                if (selectedItemTemplateIds != null && selectedItemTemplateIds.Count > 0
                    && TryResolveClientSelectedRewards(stackable, selectedItemTemplateIds, out rewards))
                {
                    return true;
                }

                if (stackable.BoosterSelectionNum <= 0)
                {
                    rewards = stackable.BoosterSelectionRewards.ToList();
                    return rewards.Count > 0;
                }

                return TryResolveClientSelectedRewards(stackable, selectedItemTemplateIds, out rewards);
            }

            return false;
        }

        private static bool TryResolveMagicHammerBundleRewards(int sourceItemTemplateId, PvfLib.StackableItemFile stackable, out List<PvfLib.BoosterRewardEntry> rewards)
        {
            rewards = new List<PvfLib.BoosterRewardEntry>();
            if (!IsMagicHammerBundle(sourceItemTemplateId) || stackable?.BoosterRewards == null)
                return false;

            var hammer = stackable.BoosterRewards.FirstOrDefault(reward => reward != null && reward.ItemId == MagicHammerMaterialItemTemplateId && reward.Count > 0);
            var box = stackable.BoosterRewards.FirstOrDefault(reward => reward != null && reward.ItemId == MagicHammerBoxItemTemplateId && reward.Count > 0);
            if (hammer == null || box == null)
                return false;

            rewards.Add(hammer);
            rewards.Add(box);
            return true;
        }

        private static bool IsMagicHammerBundle(int itemTemplateId)
        {
            return itemTemplateId >= MagicHammerBundleMinItemTemplateId && itemTemplateId <= MagicHammerBundleMaxItemTemplateId;
        }

        private static bool TryResolveClientSelectedRewards(PvfLib.StackableItemFile stackable, IReadOnlyList<int> selectedItemTemplateIds, out List<PvfLib.BoosterRewardEntry> rewards)
        {
            rewards = new List<PvfLib.BoosterRewardEntry>();
            if (selectedItemTemplateIds == null || selectedItemTemplateIds.Count == 0)
                return false;

            var candidates = stackable.BoosterSelectionRewards.Count > 0
                ? stackable.BoosterSelectionRewards
                : stackable.PackageRewards;
            if (candidates.Count == 0)
                return false;

            var rewardByItemId = candidates
                .GroupBy(r => r.ItemId)
                .ToDictionary(g => g.Key, g => g.First());
            var maxSelectionCount = stackable.BoosterSelectionNum > 0
                ? stackable.BoosterSelectionNum
                : selectedItemTemplateIds.Count;
            var seen = new HashSet<int>();

            foreach (var itemId in selectedItemTemplateIds.Where(id => id > 0))
            {
                if (!seen.Add(itemId))
                    continue;

                if (!rewardByItemId.TryGetValue(itemId, out var reward))
                    continue;

                rewards.Add(reward);
                if (rewards.Count >= maxSelectionCount)
                    break;
            }

            return rewards.Count > 0;
        }

        private static List<PvfLib.BoosterRewardEntry> RollBoosterRewards(IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            var selected = new List<PvfLib.BoosterRewardEntry>();
            foreach (var group in rewards.GroupBy(r => r.Group))
            {
                var totalWeight = group.Sum(r => Math.Max(0, r.Weight));
                if (totalWeight <= 0)
                    continue;

                var drawCount = Math.Max(1, group.Max(r => r.DrawCount));
                for (var drawIndex = 0; drawIndex < drawCount; drawIndex++)
                {
                    var roll = Random.Shared.Next(totalWeight);
                    var cumulative = 0;
                    foreach (var reward in group)
                    {
                        cumulative += Math.Max(0, reward.Weight);
                        if (roll >= cumulative)
                            continue;

                        selected.Add(reward);
                        break;
                    }
                }
            }

            return selected;
        }

        internal static bool TryResolveMallAutoOpenRewards(int itemTemplateId, out List<PvfLib.BoosterRewardEntry> rewards)
        {
            rewards = null;
            var stackable = InventoryDbPrimitives.LoadStackableItem(itemTemplateId);
            if (stackable == null)
                return false;

            var stackableType = NormalizeStackableType(stackable.StackableType);
            if (!stackableType.Equals("[cera package]", StringComparison.OrdinalIgnoreCase)
                && !stackableType.Equals("[cera booster]", StringComparison.OrdinalIgnoreCase)
                && !(stackableType.Equals("[booster]", StringComparison.OrdinalIgnoreCase) && IsMagicHammerBundle(itemTemplateId)))
                return false;

            return TryResolvePackageRewards(itemTemplateId, stackable, stackableType, null, out rewards) && rewards.Count > 0;
        }

        internal static string NormalizeStackableType(string stackableType)
        {
            if (string.IsNullOrWhiteSpace(stackableType))
                return string.Empty;

            var text = stackableType.Trim();
            var first = text.IndexOf('`');
            if (first >= 0)
            {
                var second = text.IndexOf('`', first + 1);
                if (second > first)
                    return text.Substring(first + 1, second - first - 1).Trim();
            }

            var bracketStart = text.IndexOf('[');
            if (bracketStart >= 0)
            {
                var bracketEnd = text.IndexOf(']', bracketStart + 1);
                if (bracketEnd > bracketStart)
                    return text.Substring(bracketStart, bracketEnd - bracketStart + 1).Trim();
            }

            return text.Replace("`", "").Trim();
        }

        internal static bool IsSupportedPackageType(string stackableType)
        {
            return stackableType.Equals("[booster]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[cera booster]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[booster random]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[cera package]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[random upgradable legacy]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[usable cera package]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[booster selection]", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsAvatarReward(ItemMetadata metadata)
        {
            var path = metadata?.PvfFilePath;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var normalizedPath = "/" + path.Replace('\\', '/').Trim('/');
            return normalizedPath.IndexOf("/avatar/", StringComparison.OrdinalIgnoreCase) >= 0
                || normalizedPath.IndexOf("/at_avatar/", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
