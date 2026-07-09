using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonEntryCostService
    {
        private readonly IAssetService _assetService;

        internal DungeonEntryCostService(IAssetService assetService)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
        }

        internal EntryCostResult TryConsumeAbyssPartyTicket(
            int characterId, int accountId,
            WorldMapArea area, int dungeonMinLevel)
        {
            var result = new EntryCostResult();
            if (characterId <= 0)
                return result.Fail("invalid character");

            if (area == null)
                return result.Fail("worldmap area missing");

            if (!area.HellDungeon)
                return result.Fail("area is not hell dungeon");

            if (!CheckHellQuestRequirement(characterId, area, out var missingQuestId))
                return result.Fail($"hell quest not cleared quest={missingQuestId}");

            try
            {
                using (var scope = _assetService.OpenScope(characterId, accountId))
                {
                    foreach (var ticket in area.HellFreePassItems)
                    {
                        if (ticket.ItemId <= 0 || ticket.Count <= 0)
                            continue;

                        if (_assetService.CountItem(scope, ticket.ItemId) < ticket.Count)
                            continue;

                        if (_assetService.TryRemoveItem(scope, ticket.ItemId, ticket.Count, out var slot, out var remaining))
                        {
                            scope.Commit();
                            result.Success = true;
                            result.IsFreePass = true;
                            result.ConsumedItems.Add(new ItemConsumeUpdate
                            {
                                ItemId = ticket.ItemId,
                                Count = ticket.Count,
                                SlotIndex = slot,
                                RemainingCount = remaining,
                            });
                            return result;
                        }
                    }

                    var normalNeedCount = WorldMap.GetHellNormalTicketNeedCount(dungeonMinLevel);
                    if (normalNeedCount <= 0)
                        return result.Fail($"dungeon min level too low minLevel={dungeonMinLevel}");

                    var normalTicketItemIds = area.HellNormalTicketItemIds;
                    if (normalTicketItemIds.Count == 0)
                        return result.Fail("normal ticket item missing");

                    var selectedNormalTicketItemId = 0;
                    foreach (var itemId in normalTicketItemIds)
                    {
                        if (itemId > 0 && _assetService.CountItem(scope, itemId) >= normalNeedCount)
                        {
                            selectedNormalTicketItemId = itemId;
                            break;
                        }
                    }

                    if (selectedNormalTicketItemId <= 0)
                        return result.Fail($"ticket missing normalNeed={normalNeedCount}");

                    if (_assetService.TryRemoveItem(scope, selectedNormalTicketItemId, normalNeedCount, out var normalSlot, out var normalRemaining))
                    {
                        result.Success = true;
                        result.IsFreePass = false;
                        result.ConsumedItems.Add(new ItemConsumeUpdate
                        {
                            ItemId = selectedNormalTicketItemId,
                            Count = normalNeedCount,
                            SlotIndex = normalSlot,
                            RemainingCount = normalRemaining,
                        });
                    }
                    else
                    {
                        return result.Fail($"ticket delete failed item={selectedNormalTicketItemId} normalNeed={normalNeedCount}");
                    }

                    scope.Commit();
                    return result;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonEntryCost] TryConsumeAbyssPartyTicket ERROR: {ex.Message}");
                return result.Fail(ex.Message);
            }
        }

        private static bool CheckHellQuestRequirement(int characterId, WorldMapArea area, out int missingQuestId)
        {
            missingQuestId = 0;
            if (area.HellQuestIds.Count == 0)
                return true;

            var connStr = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            foreach (var questId in area.HellQuestIds)
            {
                if (questId <= 0)
                    continue;

                if (questId > ushort.MaxValue || !new QuestRepository(connStr).IsQuestCleared(characterId, (ushort)questId))
                {
                    missingQuestId = questId;
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed class EntryCostResult
    {
        public bool Success;
        public bool IsFreePass;
        public string FailReason;
        public List<ItemConsumeUpdate> ConsumedItems { get; } = new List<ItemConsumeUpdate>();

        internal EntryCostResult Fail(string reason)
        {
            FailReason = reason;
            return this;
        }
    }

    internal sealed class ItemConsumeUpdate
    {
        public int ItemId { get; set; }
        public int Count { get; set; }
        public short SlotIndex { get; set; }
        public int RemainingCount { get; set; }
    }
}
