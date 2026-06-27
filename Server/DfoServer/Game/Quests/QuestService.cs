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
                using (var tc = new SqliteCommand(
                    "CREATE TABLE IF NOT EXISTS character_active_quests (character_id INTEGER NOT NULL, slot INTEGER NOT NULL, quest_id INTEGER NOT NULL, trigger_value INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (character_id, slot))", conn))
                    tc.ExecuteNonQuery();
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
                using (var tc = new SqliteCommand(
                    "CREATE TABLE IF NOT EXISTS character_active_quests (character_id INTEGER NOT NULL, slot INTEGER NOT NULL, quest_id INTEGER NOT NULL, trigger_value INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (character_id, slot))", conn))
                    tc.ExecuteNonQuery();
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

        public static byte[] HandleAcceptQuest(string connStr, int characterId, byte[] body)
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

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "INSERT OR REPLACE INTO character_active_quests (character_id, slot, quest_id, trigger_value) VALUES (@cid, @s, @qid, @tv)", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@s", slot);
                    cmd.Parameters.AddWithValue("@qid", (int)questId);
                    cmd.Parameters.AddWithValue("@tv", (long)initTrigger);
                    cmd.ExecuteNonQuery();
                }
                if (repeatable)
                {
                    using (var cmd = new SqliteCommand(
                        "DELETE FROM character_invisible_falgs WHERE character_id=@cid AND slot_index=@idx", conn))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@idx", (int)questId);
                        cmd.ExecuteNonQuery();
                    }
                }
            }

            var eventItems = GameWorld.QuestData.GetEventItems(questId);

            var w = new Network.GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(questId);
            w.WriteUInt32(initTrigger);
            w.WriteByte((byte)eventItems.Count);
            for (int i = 0; i < eventItems.Count; i++)
            {
                w.WriteUInt16(0);                       // slotIndex
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
            ushort multiplier = (body.Length >= 6) ? BitConverter.ToUInt16(body, 4) : (ushort)1;
            if (multiplier == 0) multiplier = 1;

            var active = LoadActiveQuests(connStr, characterId);
            var q = FindByQuestId(active, questId);
            if (q != null && q.TriggerValue != 0) return BuildFailAck(22);

            int playerLevel = GetCharacterLevel(connStr, characterId);
            int playerJob = GetCharacterJob(connStr, characterId);
            var reward = GameWorld.QuestData.GetRewardExp(questId, rewardSelectIdx, playerLevel, playerJob);
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
                foreach (var si in seekItems)
                {
                    if (si.ItemId <= 0 || si.Count <= 0) continue;
                    short slot;
                    int remaining;
                    if (assetService.TryRemoveItem(scope, si.ItemId, si.Count, out slot, out remaining))
                        consumedEntries.Add(new ConsumedItemEntry { UpdateType = 0, SlotIndex = (ushort)slot, RemainingCount = (uint)remaining });
                }

                goldReward = reward.Gold * multiplier;
                if (goldReward > 0)
                    assetService.AddGold(scope, (int)goldReward);

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

                if (!GameWorld.QuestData.IsRepeatableQuest(questId))
                    MarkQuestCleared(scope.Connection, scope.Transaction, characterId, questId);
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

            FileLogger.Log($"[QuestService] FINISH quest={questId} rewardIdx={rewardSelectIdx} mult={multiplier} gold={reward.Gold * multiplier} consumed={consumedEntries.Count} rewarded={insertedEntries.Count}");
            return w.ToArray();
        }

        private static void MarkQuestCleared(SqliteConnection conn, SqliteTransaction tx, int characterId, ushort questId)
        {
            using (var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO character_invisible_falgs (character_id, slot_index, flag_value) VALUES (@cid, @idx, 1)", conn, tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@idx", (int)questId);
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
                using (var cmd = new SqliteCommand(
                    "SELECT flag_value FROM character_invisible_falgs WHERE character_id=@cid AND slot_index=@idx", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@idx", (int)questId);
                    var result = cmd.ExecuteScalar();
                    return result != null && Convert.ToInt32(result) != 0;
                }
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

            if (qst.PreRequiredQuestGroups != null && qst.PreRequiredQuestGroups.Count > 0)
            {
                foreach (var group in qst.PreRequiredQuestGroups)
                {
                    var ids = GameWorld.QuestData.ParseIntList(group);
                    bool groupOk = true;
                    foreach (var pq in ids)
                    {
                        if (pq > 0 && !IsQuestCleared(connStr, characterId, (ushort)pq))
                        { groupOk = false; break; }
                    }
                    if (groupOk) return true;
                }
                return false;
            }

            var preReqs = GameWorld.QuestData.GetPreRequiredQuests(questId);
            if (preReqs.Count > 0)
            {
                foreach (var preQid in preReqs)
                {
                    if (preQid > 0 && !IsQuestCleared(connStr, characterId, (ushort)preQid))
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
