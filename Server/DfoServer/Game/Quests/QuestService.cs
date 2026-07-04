using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    public sealed class ActiveQuest
    {
        public int Slot;
        public ushort QuestId;
        public uint TriggerValue;
    }

    public static class QuestService
    {
        private const int MaxActiveQuests = 20;

        public static List<ActiveQuest> LoadActiveQuests(string connStr, int characterId)
        {
            var list = new List<ActiveQuest>();
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT slot, quest_id, trigger_value FROM character_active_quests WHERE character_id=@cid ORDER BY slot", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                            list.Add(new ActiveQuest { Slot = r.GetInt32(0), QuestId = (ushort)r.GetInt32(1), TriggerValue = (uint)r.GetInt64(2) });
                    }
                }
            }
            return list;
        }

        public static void SaveActiveQuests(string connStr, int characterId, List<ActiveQuest> quests)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var q in quests)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT OR REPLACE INTO character_active_quests (character_id, slot, quest_id, trigger_value) VALUES (@cid, @s, @qid, @tv)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@s", q.Slot);
                            cmd.Parameters.AddWithValue("@qid", (int)q.QuestId);
                            cmd.Parameters.AddWithValue("@tv", (long)q.TriggerValue);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        public static ActiveQuest FindByQuestId(List<ActiveQuest> active, ushort questId)
        {
            foreach (var q in active)
                if (q.QuestId == questId) return q;
            return null;
        }

        public static int FindFreeSlot(List<ActiveQuest> active)
        {
            var used = new HashSet<int>();
            foreach (var q in active) used.Add(q.Slot);
            for (int i = 0; i < MaxActiveQuests; i++)
                if (!used.Contains(i)) return i;
            return -1;
        }

        public static byte[] HandleAcceptQuest(
            string connStr,
            int characterId,
            byte[] body,
            IAssetService assetService = null,
            int accountId = 0)
        {
            if (body == null || body.Length < 2) return BuildFailAck(23);
            ushort questId = BitConverter.ToUInt16(body, 0);

            var active = LoadActiveQuests(connStr, characterId);
            if (FindByQuestId(active, questId) != null) return BuildFailAck(18);

            bool repeatable = GameWorld.QuestData.IsRepeatableQuest(questId);
            if (IsQuestCleared(connStr, characterId, questId) && !repeatable)
                return BuildFailAck(18);

            if (!CheckPreRequiredQuests(connStr, characterId, questId))
                return BuildFailAck(21);

            var collisions = GameWorld.QuestData.GetCollisionQuests(questId);
            foreach (var colQid in collisions)
            {
                if (colQid > 0 && FindByQuestId(active, (ushort)colQid) != null)
                    return BuildFailAck(21);
            }

            int slot = FindFreeSlot(active);
            if (slot < 0) return BuildFailAck(4);

            uint initTrigger = GameWorld.QuestData.GetInitTrigger(questId);
            var eventItems = GameWorld.QuestData.GetEventItems(questId);
            var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(questId);
            var eventSlots = new List<ushort>(eventItems.Count);

            if (assetService != null && (eventItems.Count > 0 || seekItems.Count > 0))
            {
                if (accountId <= 0)
                    accountId = GetAccountIdByConnStr(connStr, characterId);

                using (var scope = assetService.OpenScope(characterId, accountId))
                {
                    EnsureActiveQuestTable(scope.Connection, scope.Transaction);
                    if (GameWorld.QuestData.IsQuestClearQuest(questId))
                        initTrigger = ComputeQuestClearTrigger(scope.Connection, scope.Transaction, characterId, questId);

                    // The accept ACK advertises event items; persist them so later quest checks see the same state.
                    foreach (var item in eventItems)
                    {
                        if (item.ItemId <= 0 || item.Count <= 0)
                        {
                            eventSlots.Add(0);
                            continue;
                        }

                        short assignedSlot;
                        if (!assetService.TryAddItem(scope, item.ItemId, item.Count, out assignedSlot))
                            return BuildFailAck(4);

                        eventSlots.Add((ushort)assignedSlot);
                    }

                    // A quest can be accepted after its seek item already exists in inventory.
                    if (seekItems.Count > 0)
                        initTrigger = ApplySeekingItemProgress(
                            initTrigger,
                            seekItems,
                            itemId => assetService.CountItem(scope, itemId));

                    InsertActiveQuest(scope.Connection, scope.Transaction, characterId, slot, questId, initTrigger);
                    if (repeatable)
                        DeleteQuestClearedFlag(scope.Connection, scope.Transaction, characterId, questId);
                    scope.Commit();
                }
            }
            else
            {
                using (var conn = new SqliteConnection(connStr))
                {
                    conn.Open();
                    EnsureActiveQuestTable(conn, null);
                    if (GameWorld.QuestData.IsQuestClearQuest(questId))
                        initTrigger = ComputeQuestClearTrigger(conn, null, characterId, questId);

                    InsertActiveQuest(conn, null, characterId, slot, questId, initTrigger);
                    if (repeatable)
                    {
                        DeleteQuestClearedFlag(conn, null, characterId, questId);
                    }
                }

                for (int i = 0; i < eventItems.Count; i++)
                    eventSlots.Add(0);
            }

            var w = new Network.GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(questId);
            w.WriteUInt32(initTrigger);
            w.WriteByte((byte)eventItems.Count);
            for (int i = 0; i < eventItems.Count; i++)
            {
                w.WriteUInt16(i < eventSlots.Count ? eventSlots[i] : (ushort)0);
                w.WriteUInt32((uint)eventItems[i].ItemId);
                w.WriteUInt32((uint)eventItems[i].Count);
            }
            FileLogger.Log($"[QuestService] ACCEPT quest={questId} slot={slot} initTrigger={initTrigger} eventItems={eventItems.Count}");
            return w.ToArray();
        }

        public static byte[] HandleGiveupQuest(string connStr, int characterId, byte[] body)
        {
            if (body == null || body.Length < 2) return BuildFailAck(19);
            ushort questId = BitConverter.ToUInt16(body, 0);

            var active = LoadActiveQuests(connStr, characterId);
            var q = FindByQuestId(active, questId);
            if (q == null) return BuildFailAck(19);
            if (!GameWorld.QuestData.CanGiveup(questId)) return BuildFailAck(20);

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "DELETE FROM character_active_quests WHERE character_id=@cid AND slot=@s", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@s", q.Slot);
                    cmd.ExecuteNonQuery();
                }
            }

            var w = new Network.GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(questId);
            FileLogger.Log($"[QuestService] GIVEUP quest={questId}");
            return w.ToArray();
        }

        public static byte[] HandleSetTrigger(string connStr, int characterId, byte[] body)
        {
            // Body after 2B wire-type echo strip: u16 questId + u8 triggerType + u8 isIncrement
            // triggerType is a channel bitmask: 0x10=ch0, 0x20=ch1, 0x40=ch2, 0=raw decrement
            // isIncrement: 0=decrement channel, 1=simple ++trigger, nonzero=increment channel
            // Server computes new trigger and echoes back
            if (body == null || body.Length < 3) return BuildFailAck(22);
            ushort questId = BitConverter.ToUInt16(body, 0);
            byte triggerType = body[2];
            bool isIncrement = body.Length >= 4 && body[3] != 0;

            var active = LoadActiveQuests(connStr, characterId);
            var q = FindByQuestId(active, questId);
            if (q == null)
            {
                FileLogger.Log($"[QuestService] SET_TRIGGER quest={questId} not in active list, echo back");
                var w2 = new Network.GamePacketWriter();
                w2.WriteByte(0x01);
                w2.WriteUInt16(questId);
                w2.WriteUInt32(0);
                return w2.ToArray();
            }

            uint oldTrigger = q.TriggerValue;
            uint newTrigger;

            if (triggerType == 1)
            {
                newTrigger = oldTrigger + 1;
            }
            else if (isIncrement)
            {
                newTrigger = IncrementTriggerChannel(oldTrigger, triggerType);
            }
            else
            {
                if (oldTrigger == 0) { newTrigger = 0; }
                else { newTrigger = DecrementTriggerChannel(oldTrigger, triggerType); }
            }

            q.TriggerValue = newTrigger;
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "UPDATE character_active_quests SET trigger_value=@tv WHERE character_id=@cid AND slot=@s", conn))
                {
                    cmd.Parameters.AddWithValue("@tv", (long)newTrigger);
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@s", q.Slot);
                    cmd.ExecuteNonQuery();
                }
            }

            FileLogger.Log($"[QuestService] SET_TRIGGER quest={questId} type=0x{triggerType:X2} inc={isIncrement} trigger={oldTrigger}→{newTrigger}");
            var w = new Network.GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(questId);
            w.WriteUInt32(newTrigger);
            return w.ToArray();
        }

        public static bool SyncMonsterRewardItemProgress(
            string connStr,
            int characterId,
            IAssetService assetService,
            int accountId,
            ICollection<int> itemFilter)
        {
            var active = LoadActiveQuests(connStr, characterId);
            if (active.Count == 0 || assetService == null)
                return false;

            var itemCountCache = new Dictionary<int, int>();
            Func<int, int> getHeldCount = itemId =>
            {
                int count;
                if (itemCountCache.TryGetValue(itemId, out count))
                    return count;

                try
                {
                    using (var scope = assetService.OpenScope(characterId, accountId))
                        count = assetService.CountItem(scope, itemId);
                }
                catch
                {
                    count = 0;
                }

                itemCountCache[itemId] = count;
                return count;
            };

            var changed = new List<ActiveQuest>();
            bool matchedQuestItem = false;

            foreach (var q in active)
            {
                var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(q.QuestId);
                if (seekItems.Count == 0)
                    continue;

                var relevantItems = new List<GameWorld.QuestRewardItem>();
                bool matchesFilter = itemFilter == null || itemFilter.Count == 0;
                foreach (var si in seekItems)
                {
                    if (si.ItemId <= 0 || si.Count <= 0)
                        continue;

                    relevantItems.Add(si);
                    if (!matchesFilter && itemFilter.Contains(si.ItemId))
                        matchesFilter = true;
                }

                if (relevantItems.Count == 0 || !matchesFilter)
                    continue;

                matchedQuestItem = true;
                var persistTrigger = ApplySeekingItemProgress(q.TriggerValue, relevantItems, getHeldCount);

                if (q.TriggerValue != persistTrigger)
                {
                    q.TriggerValue = persistTrigger;
                    changed.Add(q);
                }
            }

            if (changed.Count > 0)
            {
                using (var conn = new SqliteConnection(connStr))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        foreach (var q in changed)
                        {
                            using (var cmd = new SqliteCommand(
                                "UPDATE character_active_quests SET trigger_value=@tv WHERE character_id=@cid AND slot=@s", conn, tx))
                            {
                                cmd.Parameters.AddWithValue("@tv", (long)q.TriggerValue);
                                cmd.Parameters.AddWithValue("@cid", characterId);
                                cmd.Parameters.AddWithValue("@s", q.Slot);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        tx.Commit();
                    }
                }
            }

            return matchedQuestItem;
        }

        public static bool SyncClearMapQuestProgress(
            string connStr,
            int characterId,
            int dungeonId,
            int mapId)
        {
            var changed = SyncClearMapQuestProgressCore(
                connStr,
                characterId,
                dungeonId,
                mapId,
                (questId, targetDungeonId, targetMapId) =>
                    GameWorld.QuestData.MatchesClearMapTarget(questId, targetDungeonId, targetMapId));

            if (changed > 0)
                FileLogger.Log($"[QuestService] CLEAR_MAP progress: cid={characterId} dungeon={dungeonId} map={mapId} changed={changed}");
            return changed > 0;
        }

        internal static int SyncClearMapQuestProgressCore(
            string connStr,
            int characterId,
            int dungeonId,
            int mapId,
            Func<ushort, int, int, bool> matchesClearMapQuest)
        {
            if (string.IsNullOrWhiteSpace(connStr) || characterId <= 0 || matchesClearMapQuest == null)
                return 0;

            var active = LoadActiveQuests(connStr, characterId);
            if (active.Count == 0)
                return 0;

            var changed = new List<ActiveQuest>();
            foreach (var q in active)
            {
                if (q.TriggerValue == 0)
                    continue;

                bool matched;
                try
                {
                    matched = matchesClearMapQuest(q.QuestId, dungeonId, mapId);
                }
                catch
                {
                    matched = false;
                }

                if (!matched)
                    continue;

                q.TriggerValue = 0;
                changed.Add(q);
            }

            if (changed.Count == 0)
                return 0;

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var q in changed)
                    {
                        using (var cmd = new SqliteCommand(
                            "UPDATE character_active_quests SET trigger_value=0 WHERE character_id=@cid AND slot=@s", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@s", q.Slot);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }

            return changed.Count;
        }

        private static uint ApplySeekingItemProgress(
            uint trigger,
            List<GameWorld.QuestRewardItem> seekItems,
            Func<int, int> getHeldCount)
        {
            if (seekItems == null || seekItems.Count == 0 || getHeldCount == null)
                return trigger;

            long missingHeld = 0;
            foreach (var si in seekItems)
            {
                if (si.ItemId <= 0 || si.Count <= 0)
                    continue;

                int required = Math.Max(1, si.Count);
                int held = Math.Max(0, getHeldCount(si.ItemId));
                missingHeld += Math.Max(0, required - held);
            }

            return GameWorld.QuestData.ReplaceTriggerChannel(trigger, 0, missingHeld);
        }

        private static void EnsureMissingCarryForwardEventItems(
            IAssetService assetService,
            DbScope scope,
            List<GameWorld.QuestRewardItem> eventItems,
            List<InsertedItemEntry> insertedEntries)
        {
            if (assetService == null || scope == null || eventItems == null || eventItems.Count == 0)
                return;

            // Repair old active quests that missed the accept-time grant, but only for carry-forward items.
            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId <= 0 || eventItem.Count <= 0)
                    continue;

                int held = Math.Max(0, assetService.CountItem(scope, eventItem.ItemId));
                int missing = Math.Max(0, eventItem.Count - held);
                if (missing <= 0)
                    continue;

                short assignedSlot;
                if (!assetService.TryAddItem(scope, eventItem.ItemId, missing, out assignedSlot))
                    continue;

                var meta = ItemMetadataResolver.Resolve(eventItem.ItemId);
                insertedEntries.Add(meta.IsStackable
                    ? new InsertedItemEntry { SlotIndex = (ushort)assignedSlot, ItemId = eventItem.ItemId, CountOrSeed = (uint)missing }
                    : new InsertedItemEntry { SlotIndex = (ushort)assignedSlot, ItemId = eventItem.ItemId, IsEquipment = true, CountOrSeed = 999999998u, EquipDurability = (ushort)meta.Durability });
            }
        }

        private static void ConsumeNonCarryForwardEventItems(
            IAssetService assetService,
            DbScope scope,
            List<GameWorld.QuestRewardItem> eventItems,
            List<GameWorld.QuestRewardItem> seekItems,
            List<GameWorld.QuestRewardItem> carryForwardEventItems,
            List<ConsumedItemEntry> consumedEntries)
        {
            if (assetService == null || scope == null || eventItems == null || eventItems.Count == 0)
                return;

            // Event items that are neither seek requirements nor carry-forward items should not remain after finish.
            var seekItemIds = new HashSet<int>();
            if (seekItems != null)
            {
                foreach (var seekItem in seekItems)
                {
                    if (seekItem.ItemId > 0 && seekItem.Count > 0)
                        seekItemIds.Add(seekItem.ItemId);
                }
            }

            var carryForwardItemIds = new HashSet<int>();
            if (carryForwardEventItems != null)
            {
                foreach (var carryForwardItem in carryForwardEventItems)
                {
                    if (carryForwardItem.ItemId > 0 && carryForwardItem.Count > 0)
                        carryForwardItemIds.Add(carryForwardItem.ItemId);
                }
            }

            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId <= 0 || eventItem.Count <= 0)
                    continue;
                if (seekItemIds.Contains(eventItem.ItemId) || carryForwardItemIds.Contains(eventItem.ItemId))
                    continue;

                short slot;
                int remaining;
                if (assetService.TryRemoveItem(scope, eventItem.ItemId, eventItem.Count, out slot, out remaining))
                    consumedEntries.Add(new ConsumedItemEntry { UpdateType = 0, SlotIndex = (ushort)slot, RemainingCount = (uint)remaining });
            }
        }

        private static void EnsureActiveQuestTable(SqliteConnection conn, SqliteTransaction tx)
        {
        }

        private static void InsertActiveQuest(SqliteConnection conn, SqliteTransaction tx, int characterId, int slot, ushort questId, uint triggerValue)
        {
            using (var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO character_active_quests (character_id, slot, quest_id, trigger_value) VALUES (@cid, @s, @qid, @tv)",
                conn,
                tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@s", slot);
                cmd.Parameters.AddWithValue("@qid", (int)questId);
                cmd.Parameters.AddWithValue("@tv", (long)triggerValue);
                cmd.ExecuteNonQuery();
            }
        }

        private static void DeleteQuestClearedFlag(SqliteConnection conn, SqliteTransaction tx, int characterId, ushort questId)
        {
            using (var cmd = new SqliteCommand(
                "DELETE FROM character_invisible_falgs WHERE character_id=@cid AND slot_index=@idx",
                conn,
                tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@idx", (int)questId);
                cmd.ExecuteNonQuery();
            }
        }

        private static bool CanFinishQuestClearQuest(string connStr, int characterId, ushort questId)
        {
            if (!GameWorld.QuestData.IsQuestClearQuest(questId))
                return false;

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                return ComputeQuestClearTrigger(conn, null, characterId, questId) == 0;
            }
        }

        private static void SyncQuestClearParentProgress(SqliteConnection conn, SqliteTransaction tx, int characterId)
        {
            var parents = new List<ActiveQuest>();
            using (var cmd = new SqliteCommand(
                "SELECT slot, quest_id, trigger_value FROM character_active_quests WHERE character_id=@cid ORDER BY slot",
                conn,
                tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var questId = (ushort)r.GetInt32(1);
                        if (!GameWorld.QuestData.IsQuestClearQuest(questId))
                            continue;

                        parents.Add(new ActiveQuest
                        {
                            Slot = r.GetInt32(0),
                            QuestId = questId,
                            TriggerValue = (uint)r.GetInt64(2)
                        });
                    }
                }
            }

            foreach (var parent in parents)
            {
                var nextTrigger = ComputeQuestClearTrigger(conn, tx, characterId, parent.QuestId);
                if (nextTrigger == parent.TriggerValue)
                    continue;

                using (var cmd = new SqliteCommand(
                    "UPDATE character_active_quests SET trigger_value=@tv WHERE character_id=@cid AND slot=@slot",
                    conn,
                    tx))
                {
                    cmd.Parameters.AddWithValue("@tv", (long)nextTrigger);
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@slot", parent.Slot);
                    cmd.ExecuteNonQuery();
                }

                FileLogger.Log($"[QuestService] QUEST_CLEAR sync parent={parent.QuestId} trigger={parent.TriggerValue}->{nextTrigger}");
            }
        }

        private static uint ComputeQuestClearTrigger(SqliteConnection conn, SqliteTransaction tx, int characterId, ushort questId)
        {
            var requiredQuestIds = GameWorld.QuestData.GetQuestClearRequiredQuestIds(questId);
            if (requiredQuestIds.Count == 0)
                return 1;

            var missing = 0;
            foreach (var requiredQuestId in requiredQuestIds)
            {
                if (!IsQuestCleared(conn, tx, characterId, requiredQuestId))
                    missing++;
            }

            return (uint)missing;
        }

        private static uint DecrementTriggerChannel(uint trigger, byte triggerType)
        {
            if (triggerType == 0) return trigger > 0 ? trigger - 1 : 0;
            if ((triggerType & 0x10) != 0) trigger = AdjustChannel(trigger, 0, -1);
            if ((triggerType & 0x20) != 0) trigger = AdjustChannel(trigger, 9, -1);
            if ((triggerType & 0x40) != 0) trigger = AdjustChannel(trigger, 18, -1);
            return trigger;
        }

        private static uint IncrementTriggerChannel(uint trigger, byte triggerType)
        {
            if (triggerType == 0) return trigger + 1;
            if ((triggerType & 0x10) != 0) trigger = AdjustChannel(trigger, 0, 1);
            if ((triggerType & 0x20) != 0) trigger = AdjustChannel(trigger, 9, 1);
            if ((triggerType & 0x40) != 0) trigger = AdjustChannel(trigger, 18, 1);
            return trigger;
        }

        private static uint AdjustChannel(uint trigger, int shift, int delta)
        {
            uint channel = (trigger >> shift) & 0x1FF;
            int next = (int)channel + delta;
            if (next < 0) next = 0;
            channel = (uint)next & 0x1FF;
            return (trigger & ~(0x1FFu << shift)) | (channel << shift);
        }

        public static byte[] HandleFinishQuest(string connStr, int characterId, byte[] body, IAssetService assetService)
        {
            if (body == null || body.Length < 2) return BuildFailAck(22);
            ushort questId = BitConverter.ToUInt16(body, 0);
            ushort rewardSelectIdx = (body.Length >= 4) ? BitConverter.ToUInt16(body, 2) : (ushort)0;
            bool hasRewardSelectIdx = body.Length >= 4 && rewardSelectIdx != ushort.MaxValue;
            ushort multiplier = (body.Length >= 6) ? BitConverter.ToUInt16(body, 4) : (ushort)1;
            if (multiplier == 0) multiplier = 1;

            var active = LoadActiveQuests(connStr, characterId);
            var q = FindByQuestId(active, questId);
            int clearedFlagValue = 1;
            if (GameWorld.QuestData.IsQuestClearQuest(questId))
            {
                if (!CanFinishQuestClearQuest(connStr, characterId, questId))
                    return BuildFailAck(22);

                if (q != null)
                    q.TriggerValue = 0;
            }
            else if (GameWorld.QuestData.IsQuestionQuest(questId))
            {
                if (!TryResolveQuestionQuestClearFlagValue(questId, q, hasRewardSelectIdx, rewardSelectIdx, out clearedFlagValue))
                    return BuildFailAck(22);
            }
            else if (q != null && q.TriggerValue != 0)
            {
                return BuildFailAck(22);
            }

            int playerLevel = GetCharacterLevel(connStr, characterId);
            int playerJob = GetCharacterJob(connStr, characterId);
            int playerGrowType = GetCharacterGrowType(connStr, characterId);
            var reward = GameWorld.QuestData.GetRewardExp(questId, rewardSelectIdx, playerLevel, playerJob, playerGrowType);
            var consumedEntries = new List<ConsumedItemEntry>();
            var insertedEntries = new List<InsertedItemEntry>();

            uint goldReward;
            int accountId = GetAccountIdByConnStr(connStr, characterId);

            using (var scope = assetService.OpenScope(characterId, accountId))
            {
                if (q != null)
                {
                    using (var cmd = new SqliteCommand(
                        "DELETE FROM character_active_quests WHERE character_id=@cid AND slot=@s", scope.Connection, scope.Transaction))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@s", q.Slot);
                        cmd.ExecuteNonQuery();
                    }
                }

                if (reward.ConsumeItems != null)
                {
                    foreach (var ci in reward.ConsumeItems)
                    {
                        short slot;
                        int remaining;
                        if (assetService.TryRemoveItem(scope, ci.ItemId, ci.Count, out slot, out remaining))
                            consumedEntries.Add(new ConsumedItemEntry { UpdateType = 0, SlotIndex = (ushort)slot, RemainingCount = (uint)remaining });
                    }
                }

                var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(questId);
                var eventItems = GameWorld.QuestData.GetEventItems(questId);
                var carryForwardEventItems = GameWorld.QuestData.GetCarryForwardEventItems(questId);
                foreach (var si in seekItems)
                {
                    if (si.ItemId <= 0 || si.Count <= 0) continue;
                    short slot;
                    int remaining;
                    if (assetService.TryRemoveItem(scope, si.ItemId, si.Count, out slot, out remaining))
                        consumedEntries.Add(new ConsumedItemEntry { UpdateType = 0, SlotIndex = (ushort)slot, RemainingCount = (uint)remaining });
                }

                ConsumeNonCarryForwardEventItems(
                    assetService,
                    scope,
                    eventItems,
                    seekItems,
                    carryForwardEventItems,
                    consumedEntries);

                EnsureMissingCarryForwardEventItems(
                    assetService,
                    scope,
                    carryForwardEventItems,
                    insertedEntries);

                goldReward = reward.Gold * multiplier;
                if (goldReward > 0)
                    assetService.GrantGold(scope, (int)goldReward);

                if (reward.ChainType == 0)
                {
                    if (goldReward > 0)
                        insertedEntries.Add(new InsertedItemEntry { SlotIndex = 0, ItemId = 0, CountOrSeed = goldReward });

                    if (reward.Items != null)
                    {
                        foreach (var ri in reward.Items)
                        {
                            if (ri.ItemId <= 0) continue;
                            int count = ri.Count * multiplier;
                            short assignedSlot;
                            if (assetService.TryAddItem(scope, ri.ItemId, count, out assignedSlot))
                            {
                                var meta = ItemMetadataResolver.Resolve(ri.ItemId);
                                insertedEntries.Add(meta.IsStackable
                                    ? new InsertedItemEntry { SlotIndex = (ushort)assignedSlot, ItemId = ri.ItemId, CountOrSeed = (uint)count }
                                    : new InsertedItemEntry { SlotIndex = (ushort)assignedSlot, ItemId = ri.ItemId, IsEquipment = true, CountOrSeed = 999999998u, EquipDurability = (ushort)meta.Durability });
                            }
                        }
                    }
                }
                else if (reward.ChainType == 1 || reward.ChainType == 2)
                {
                    UpdateGrowType(scope.Connection, scope.Transaction, characterId, reward.ChainType, reward.GrowNumber);
                }
                else if (reward.ChainType == 20)
                {
                    UpdateExpertJob(scope.Connection, scope.Transaction, characterId, reward.GrowNumber);
                }
                else if (reward.ChainType == GameWorld.QuestData.ChainTypeSlotExpansion)
                {
                    UpdateSlotExpansion(scope.Connection, scope.Transaction, characterId, reward.GrowNumber);
                }

                if (!GameWorld.QuestData.IsRepeatableQuest(questId))
                    MarkQuestCleared(scope.Connection, scope.Transaction, characterId, questId, clearedFlagValue);
                SyncQuestClearParentProgress(scope.Connection, scope.Transaction, characterId);
                scope.Commit();
            }

            // Build ACK
            var w = new Network.GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(questId);
            w.WriteByte(0x00); // completionType=0 (type 0/25)
            w.WriteUInt32(reward.Exp * multiplier);
            w.WriteUInt32(reward.Gold * multiplier);

            // Phase 3: consumed items
            w.WriteByte((byte)consumedEntries.Count);
            foreach (var ce in consumedEntries)
            {
                w.WriteByte(ce.UpdateType);
                w.WriteUInt16(ce.SlotIndex);
                w.WriteUInt32(ce.RemainingCount);
            }

            // Phase 4: chainType + reward data
            w.WriteByte((byte)reward.ChainType);
            if (reward.ChainType == 0)
            {
                w.WriteByte((byte)insertedEntries.Count);
                foreach (var ie in insertedEntries)
                {
                    w.WriteUInt16(ie.SlotIndex);
                    w.WriteUInt32((uint)ie.ItemId);
                    w.WriteUInt32(ie.CountOrSeed);
                    w.WriteByte(0);   // upgradeLevel
                    w.WriteUInt16(ie.EquipDurability);
                    w.WriteUInt32(0); // reserved
                    w.WriteByte(0);   // extraFlags
                }
            }
            else if (reward.ChainType == 1 || reward.ChainType == 2)
            {
                w.WriteByte((byte)reward.GrowNumber);
                w.WriteByte(0); // npcCount layer 1
                w.WriteByte(0); // npcCount layer 2
            }
            else if (reward.ChainType == 20)
            {
                w.WriteByte((byte)reward.GrowNumber); // expert job type
                w.WriteByte(0); // npcCount layer 1
                w.WriteByte(0); // npcCount layer 2
            }
            else if (reward.ChainType == GameWorld.QuestData.ChainTypeSlotExpansion)
            {
                w.WriteByte((byte)reward.GrowNumber); // expanded equipment slot id (21=support, 22=magic stone)
                w.WriteByte(0); // npcCount layer 1
                w.WriteByte(0); // npcCount layer 2
            }

            FileLogger.Log($"[QuestService] FINISH quest={questId} rewardIdx={rewardSelectIdx} mult={multiplier} flag={clearedFlagValue} gold={reward.Gold * multiplier} consumed={consumedEntries.Count} rewarded={insertedEntries.Count}");
            return w.ToArray();
        }

        private static bool TryResolveQuestionQuestClearFlagValue(
            ushort questId,
            ActiveQuest activeQuest,
            bool hasRewardSelectIdx,
            ushort rewardSelectIdx,
            out int flagValue)
        {
            flagValue = 1;
            int answerCount = GameWorld.QuestData.GetQuestionAnswerCount(questId);
            if (answerCount <= 0)
                return activeQuest == null || activeQuest.TriggerValue == 0;

            if (activeQuest != null && TryResolveQuestionQuestFlagValueFromTrigger(activeQuest.TriggerValue, answerCount, out flagValue))
                return true;

            if (hasRewardSelectIdx && rewardSelectIdx < answerCount)
            {
                flagValue = GameWorld.QuestData.GetRequiredQuestAnswerFlagValue(rewardSelectIdx);
                return true;
            }

            uint trigger = activeQuest != null ? activeQuest.TriggerValue : uint.MaxValue;
            FileLogger.Log($"[QuestService] Question quest finish rejected: quest={questId} trigger={trigger} answerCount={answerCount}");
            return false;
        }

        private static bool TryResolveQuestionQuestFlagValueFromTrigger(uint trigger, int answerCount, out int flagValue)
        {
            if (trigger == 0)
            {
                flagValue = GameWorld.QuestData.GetRequiredQuestAnswerFlagValue(0);
                return true;
            }

            if (trigger <= (uint)answerCount)
            {
                flagValue = (int)trigger;
                return true;
            }

            flagValue = 1;
            return false;
        }

        private static void MarkQuestCleared(SqliteConnection conn, SqliteTransaction tx, int characterId, ushort questId, int flagValue = 1)
        {
            if (flagValue == 0)
                flagValue = 1;

            using (var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO character_invisible_falgs (character_id, slot_index, flag_value) VALUES (@cid, @idx, @flag)", conn, tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@idx", (int)questId);
                cmd.Parameters.AddWithValue("@flag", flagValue);
                cmd.ExecuteNonQuery();
            }

            uint requiredLen = (uint)(questId + 1);
            using (var cmd = new SqliteCommand(
                "UPDATE character_init_flags SET charac_invisible_falgs_payload_len = MAX(charac_invisible_falgs_payload_len, @len) WHERE character_id = @cid", conn, tx))
            {
                cmd.Parameters.AddWithValue("@len", (long)requiredLen);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        public static bool IsQuestCleared(string connStr, int characterId, ushort questId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                return IsQuestCleared(conn, null, characterId, questId);
            }
        }

        private static bool IsQuestCleared(SqliteConnection conn, SqliteTransaction tx, int characterId, int questId)
        {
            return ReadQuestClearedFlagValue(conn, tx, characterId, questId) != 0;
        }

        private static int ReadQuestClearedFlagValue(SqliteConnection conn, SqliteTransaction tx, int characterId, int questId)
        {
            using (var cmd = new SqliteCommand(
                "SELECT flag_value FROM character_invisible_falgs WHERE character_id=@cid AND slot_index=@idx",
                conn,
                tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@idx", questId);
                var result = cmd.ExecuteScalar();
                return result != null ? Convert.ToInt32(result) : 0;
            }
        }

        private static ConsumedItemEntry DeleteItemByTemplateId(SqliteConnection conn, SqliteTransaction tx, int characterId, int itemTemplateId, int count)
        {
            var result = Game.Inventory.InventoryDbPrimitives.RemoveItemByTemplateId(conn, tx, characterId, itemTemplateId, count);
            if (result == null) return null;
            var (slotIndex, removedCount, remainingCount) = result.Value;
            return new ConsumedItemEntry { UpdateType = 0, SlotIndex = (ushort)slotIndex, RemainingCount = (uint)removedCount };
        }

        private static bool CheckPreRequiredQuests(string connStr, int characterId, int questId)
        {
            var qst = GameWorld.QuestData.GetQuestFile(questId);
            if (qst == null) return true;

            bool preQuestOk = true;
            if (qst.PreRequiredQuestGroups != null && qst.PreRequiredQuestGroups.Count > 0)
            {
                preQuestOk = false;
                foreach (var group in qst.PreRequiredQuestGroups)
                {
                    var ids = GameWorld.QuestData.ParseIntList(group);
                    bool groupOk = true;
                    foreach (var pq in ids)
                    {
                        if (pq > 0 && !IsQuestCleared(connStr, characterId, (ushort)pq))
                        { groupOk = false; break; }
                    }
                    if (groupOk) { preQuestOk = true; break; }
                }
            }
            else
            {
                var preReqs = GameWorld.QuestData.GetPreRequiredQuests(questId);
                if (preReqs.Count > 0)
                {
                    foreach (var preQid in preReqs)
                    {
                        if (preQid > 0 && !IsQuestCleared(connStr, characterId, (ushort)preQid))
                        {
                            preQuestOk = false;
                            break;
                        }
                    }
                }
            }

            return preQuestOk && CheckPreRequiredQuestAnswers(connStr, characterId, qst);
        }

        private static bool CheckPreRequiredQuestAnswers(string connStr, int characterId, PvfLib.QuestFile qst)
        {
            var preReqAns = GameWorld.QuestData.ParseIntList(qst.PreRequiredQuestAnswer);
            if (preReqAns.Count == 0)
                return true;

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                for (int i = 0; i + 1 < preReqAns.Count; i += 2)
                {
                    int reqQid = preReqAns[i];
                    int reqAnswer = preReqAns[i + 1];
                    if (reqQid <= 0)
                        continue;

                    int expectedFlag = GameWorld.QuestData.GetRequiredQuestAnswerFlagValue(reqAnswer);
                    int actualFlag = ReadQuestClearedFlagValue(conn, null, characterId, reqQid);
                    if (expectedFlag <= 0 || actualFlag != expectedFlag)
                        return false;
                }
            }

            return true;
        }

        private static int GetCharacterLevel(string connStr, int characterId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT level FROM characters WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    var result = cmd.ExecuteScalar();
                    return (result != null) ? Convert.ToInt32(result) : 1;
                }
            }
        }

        private static int GetCharacterJob(string connStr, int characterId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT job FROM characters WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    var result = cmd.ExecuteScalar();
                    return (result != null) ? Convert.ToInt32(result) : -1;
                }
            }
        }

        private static int GetCharacterGrowType(string connStr, int characterId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT grow_type FROM characters WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    var result = cmd.ExecuteScalar();
                    return (result != null) ? Convert.ToInt32(result) : 0;
                }
            }
        }

        private static void UpdateGrowType(SqliteConnection conn, SqliteTransaction tx, int characterId, int chainType, int growNumber)
        {
            byte currentGrowType = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT grow_type FROM characters WHERE character_id = @cid";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var val = cmd.ExecuteScalar();
                if (val != null) currentGrowType = (byte)Convert.ToInt32(val);
            }

            int firstGrow = currentGrowType & 0xF;
            int secondGrow = (currentGrowType >> 4) & 0xF;

            if (chainType == 1)
                firstGrow = growNumber;
            else if (chainType == 2)
                secondGrow = growNumber;

            byte newGrowType = (byte)((secondGrow << 4) | (firstGrow & 0xF));

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE characters SET grow_type = @grow WHERE character_id = @cid";
                cmd.Parameters.AddWithValue("@grow", (int)newGrowType);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }

            FileLogger.Log($"[QuestService] UpdateGrowType: cid={characterId} chain={chainType} growNumber={growNumber} old=0x{currentGrowType:X2} new=0x{newGrowType:X2}");
        }

        private static void UpdateExpertJob(SqliteConnection conn, SqliteTransaction tx, int characterId, int expertJobType)
        {
            byte[] expertJobBlob = BuildExpertJobBlob(1, 1, expertJobType);

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO character_subtype0_fields (character_id, expert_job_type) VALUES (@cid, @ejt)
                    ON CONFLICT(character_id) DO UPDATE SET expert_job_type=@ejt;
                    INSERT INTO character_init_flags (character_id, expert_job_blob) VALUES (@cid, @blob)
                    ON CONFLICT(character_id) DO UPDATE SET expert_job_blob=@blob;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@ejt", expertJobType);
                cmd.Parameters.AddWithValue("@blob", expertJobBlob);
                cmd.ExecuteNonQuery();
            }

            FileLogger.Log($"[QuestService] UpdateExpertJob: cid={characterId} expertJobType={expertJobType}");
        }

        private static void UpdateSlotExpansion(SqliteConnection conn, SqliteTransaction tx, int characterId, int slotId)
        {
            // Special equipment slots are stored as bits in ex_equip_slot_stat: 21=support, 22=magic stone, 23=earring.
            var flag = ResolveSlotExpansionFlag(slotId);
            if (flag == 0)
            {
                FileLogger.Log($"[QuestService] UpdateSlotExpansion skipped: cid={characterId} unsupported slotId={slotId}");
                return;
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE characters
                    SET ex_equip_slot_stat = (ex_equip_slot_stat | @flag),
                        updated_at = CURRENT_TIMESTAMP
                    WHERE character_id = @cid;";
                cmd.Parameters.AddWithValue("@flag", flag);
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }

            FileLogger.Log($"[QuestService] UpdateSlotExpansion: cid={characterId} slotId={slotId} flag=0x{flag:X2}");
        }

        private static int ResolveSlotExpansionFlag(int slotId)
        {
            // Slot ids follow the client equipment index; the persisted flag is zero-based from support equipment.
            if (slotId < 21 || slotId > 23)
                return 0;
            return 1 << (slotId - 21);
        }

        private static byte[] BuildExpertJobBlob(byte state0, byte mode, int expertJobType)
        {
            var list = new List<byte>();
            list.Add(state0);
            list.Add(mode);
            list.AddRange(BitConverter.GetBytes(0));
            list.AddRange(BitConverter.GetBytes(0));
            list.Add((byte)1);
            list.AddRange(BitConverter.GetBytes(expertJobType));
            return list.ToArray();
        }

        private static int GetAccountIdByConnStr(string connStr, int characterId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT account_id FROM characters WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    var result = cmd.ExecuteScalar();
                    return result != null ? Convert.ToInt32(result) : 1;
                }
            }
        }

        private static byte[] BuildFailAck(byte errorCode)
        {
            return new byte[] { 0x00, errorCode };
        }
    }

    internal sealed class ConsumedItemEntry
    {
        public byte UpdateType;
        public ushort SlotIndex;
        public uint RemainingCount;
    }

    internal sealed class InsertedItemEntry
    {
        public ushort SlotIndex;
        public int ItemId;
        public bool IsEquipment;
        public uint CountOrSeed;
        public ushort EquipDurability;
    }
}
