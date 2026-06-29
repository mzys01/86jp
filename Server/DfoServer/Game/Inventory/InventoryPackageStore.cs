using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryPackageStore
    {
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

                        _db.InsertAvatarItem(
                            connection,
                            transaction,
                            characterId,
                            SqliteInventoryStore.CreateDefaultAvatarItem((short)targetSlot, reward.ItemTemplateId, optionValue));
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

                    _db.InsertAvatarItem(
                        connection,
                        transaction,
                        characterId,
                        SqliteInventoryStore.CreateDefaultAvatarItem((short)targetSlot, choice.ItemTemplateId, choice.OptionValue));
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

                    _db.InsertAvatarItem(
                        connection,
                        transaction,
                        characterId,
                        SqliteInventoryStore.CreateDefaultAvatarItem((short)targetSlot, reward.ItemTemplateId, request.SelectionFlag));
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

        public bool TryUseBoosterItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, short? slotIndex, IReadOnlyList<int> selectedItemTemplateIds, out BoosterUseResult result)
        {
            result = null;

            var source = slotIndex.HasValue
                ? _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, slotIndex.Value)
                : _db.FindFirstPackageItem(connection, transaction, characterId);
            if (source == null)
                return false;

            var stackable = InventoryDbPrimitives.LoadStackableItem(source.ItemTemplateId);
            if (stackable == null)
                return false;

            var stackableType = NormalizeStackableType(stackable.StackableType);
            if (!TryResolvePackageRewards(stackable, stackableType, selectedItemTemplateIds, out var rewards))
            {
                var selectedText = selectedItemTemplateIds == null || selectedItemTemplateIds.Count == 0
                    ? "none"
                    : string.Join(",", selectedItemTemplateIds.Select(id => $"0x{id:X8}"));
                FileLogger.Log($"  [Booster] unsupported/empty item=0x{source.ItemTemplateId:X8} type={stackableType} selected={selectedText} rewards(random={stackable.BoosterRewards.Count},select={stackable.BoosterSelectionRewards.Count},package={stackable.PackageRewards.Count})");
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
                if (!_db.TryAddBoosterRewardItem(connection, transaction, characterId, accountId, reward.ItemId, reward.Count, out var rewardResult))
                    return false;

                useResult.Rewards.Add(rewardResult);
            }

            _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, source, 1);
            foreach (var reward in useResult.Rewards)
                _auditLogger.WriteBuyAuditLog(connection, transaction, characterId, reward.ItemTemplateId, reward.SlotIndex, 0, 0);
            result = useResult;
            return true;
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
                if (!_db.TryAddBoosterRewardItem(connection, transaction, characterId, accountId, reward.ItemId, reward.Count, out var rewardResult))
                    return false;

                useResult.Rewards.Add(rewardResult);
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
                var existingItem = _db.FindStackableItemByTemplateIdAndExpireTime(
                    connection,
                    transaction,
                    characterId,
                    stackListType,
                    reward.ItemTemplateId,
                    reward.ExpireTime,
                    metadata.StackLimit);
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

                    existingItem = _db.FindStackableItemByTemplateIdAndExpireTime(
                        connection,
                        transaction,
                        characterId,
                        stackListType,
                        reward.ItemTemplateId,
                        reward.ExpireTime,
                        metadata.StackLimit);
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
                        isPetConsumable ? "pet" : reward.ExpireTime > 0 ? "special" : metadata.ItemKind,
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
                    0,
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

        private static bool TryResolvePackageRewards(PvfLib.StackableItemFile stackable, string stackableType, IReadOnlyList<int> selectedItemTemplateIds, out List<PvfLib.BoosterRewardEntry> rewards)
        {
            rewards = new List<PvfLib.BoosterRewardEntry>();
            if (stackableType.Equals("[booster]", StringComparison.OrdinalIgnoreCase)
                || stackableType.Equals("[cera booster]", StringComparison.OrdinalIgnoreCase))
            {
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
                && !stackableType.Equals("[cera booster]", StringComparison.OrdinalIgnoreCase))
                return false;

            return TryResolvePackageRewards(stackable, stackableType, null, out rewards) && rewards.Count > 0;
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
                || stackableType.Equals("[cera package]", StringComparison.OrdinalIgnoreCase)
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
