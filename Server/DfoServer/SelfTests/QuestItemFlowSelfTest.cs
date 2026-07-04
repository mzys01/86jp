using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class QuestItemFlowSelfTest
    {
        private const int CharacterId = 135001;
        private const int AccountId = 135001;
        private const ushort GiveLetterQuestId = 2042;
        private const ushort UseLetterQuestId = 2043;
        private const int AganzoLetterItemId = 10089292;
        private const ushort NonCarryEventQuestId = 2578;
        private const int NonCarryEventItemId = 10100257;
        private const ushort GreenStoneQuestId = 1849;
        private const int GreenStonePassiveObjectCode = 52853;
        private const int ChessboardDespairDungeonId = 160;
        private const int GreenLightStoneFragmentItemId = 10099811;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_ITEM_FLOW selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var dbPath = Path.Combine(tempDir, "quest-item-flow.db");
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            var schemaPath = ServerPaths.SchemaFilePath;
            var characterRepository = new SqliteCharacterRepository(dbPath, schemaPath);
            SeedAccount(dbPath);
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = CharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-item-flow-test"),
                Job = 0,
                GrowType = 0,
                Level = 49,
            });

            var assetService = new SqliteAssetService(dbPath, schemaPath);
            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            MarkQuestCleared(connStr, 2041);
            var failures = 0;

            var greenStoneQuest = QuestData.GetQuestFile(GreenStoneQuestId);
            Check("green stone quest parses passive object reward",
                greenStoneQuest != null
                    && greenStoneQuest.EnemyRewardItems.Exists(e =>
                        e.EnemyCode == GreenStonePassiveObjectCode
                        && e.EnemyType == QuestDropProvider.EnemyTypePassiveObject
                        && e.DungeonId == ChessboardDespairDungeonId
                        && e.ItemId == GreenLightStoneFragmentItemId
                        && e.Count == 1
                        && e.DropRate == 100
                        && e.MaxStack == 5),
                ref failures);

            var greenStonePassiveCandidates = QuestDropProvider.CheckEnemyDrop(
                new[] { (int)GreenStoneQuestId },
                ChessboardDespairDungeonId,
                0,
                GreenStonePassiveObjectCode,
                QuestDropProvider.EnemyTypePassiveObject);
            Check("green stone passive object reward matches",
                greenStonePassiveCandidates != null
                    && greenStonePassiveCandidates.Count == 1
                    && greenStonePassiveCandidates[0].ItemId == GreenLightStoneFragmentItemId
                    && greenStonePassiveCandidates[0].Count == 1
                    && greenStonePassiveCandidates[0].DropRate == 100
                    && greenStonePassiveCandidates[0].MaxStack == 5,
                ref failures);

            var greenStoneMonsterCandidates = QuestDropProvider.CheckMonsterDrop(
                new[] { (int)GreenStoneQuestId },
                ChessboardDespairDungeonId,
                0,
                GreenStonePassiveObjectCode);
            Check("green stone passive object is not monster reward",
                greenStoneMonsterCandidates == null,
                ref failures);

            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = GiveLetterQuestId, TriggerValue = 0 },
            });
            var legacyFinish2042 = QuestService.HandleFinishQuest(
                connStr,
                CharacterId,
                BuildQuestBody(GiveLetterQuestId),
                assetService);
            Check("legacy active 2042 finish succeeds", IsSuccessAck(legacyFinish2042), ref failures);
            Check("legacy active 2042 finish grants missing letter", CountItem(assetService, AganzoLetterItemId) == 1, ref failures);
            Check("legacy active 2042 finish ack inserts letter",
                TryReadFinishInsertedItem(legacyFinish2042, out _, out var finishItemId, out var finishCount)
                    && finishItemId == AganzoLetterItemId
                    && finishCount == 1,
                ref failures);

            ClearIssue135State(connStr);

            var accept2042 = QuestService.HandleAcceptQuest(
                connStr,
                CharacterId,
                BuildQuestBody(GiveLetterQuestId),
                assetService,
                AccountId);
            Check("accept 2042 succeeds", IsSuccessAck(accept2042), ref failures);
            Check("accept 2042 gives letter event item", TryReadAcceptEventItem(accept2042, out var slot, out var itemId, out var count)
                && slot > 0
                && itemId == AganzoLetterItemId
                && count == 1,
                ref failures);
            Check("letter persisted after accept 2042", CountItem(assetService, AganzoLetterItemId) == 1, ref failures);

            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = GiveLetterQuestId, TriggerValue = 0 },
            });

            var finish2042 = QuestService.HandleFinishQuest(
                connStr,
                CharacterId,
                BuildQuestBody(GiveLetterQuestId),
                assetService);
            Check("finish 2042 succeeds", IsSuccessAck(finish2042), ref failures);
            Check("letter remains for next quest after finish 2042", CountItem(assetService, AganzoLetterItemId) == 1, ref failures);

            var accept2043 = QuestService.HandleAcceptQuest(
                connStr,
                CharacterId,
                BuildQuestBody(UseLetterQuestId),
                assetService,
                AccountId);
            Check("accept 2043 succeeds", IsSuccessAck(accept2043), ref failures);
            Check("accept 2043 starts with only npc trigger after held letter is counted", TryReadAcceptTrigger(accept2043, out var initTrigger) && initTrigger == 512, ref failures);

            var matched = QuestService.SyncMonsterRewardItemProgress(
                connStr,
                CharacterId,
                assetService,
                AccountId,
                new[] { AganzoLetterItemId });
            Check("letter progress sync matches active quest", matched, ref failures);
            Check("letter progress clears only item channel", LoadTrigger(connStr, UseLetterQuestId) == 512, ref failures);

            var setNpcTrigger = QuestService.HandleSetTrigger(connStr, CharacterId, BuildSetTriggerBody(UseLetterQuestId, 0x20, false));
            Check("npc trigger ack succeeds", IsSuccessAck(setNpcTrigger), ref failures);
            Check("npc trigger clears remaining channel", LoadTrigger(connStr, UseLetterQuestId) == 0, ref failures);

            var finish2043 = QuestService.HandleFinishQuest(
                connStr,
                CharacterId,
                BuildQuestBody(UseLetterQuestId),
                assetService);
            Check("finish 2043 succeeds", IsSuccessAck(finish2043), ref failures);
            Check("letter consumed by seek quest finish", CountItem(assetService, AganzoLetterItemId) == 0, ref failures);

            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = UseLetterQuestId, TriggerValue = 1 },
            });
            AddItem(assetService, AganzoLetterItemId, 1);
            var sender = new RecordingQuestSender(CharacterId, AccountId);
            var questManager = new QuestManager(sender, connStr, assetService);
            questManager.SyncItemSeekingQuestProgressAsync(new[] { AganzoLetterItemId }).GetAwaiter().GetResult();
            Check("generic item-seeking sync clears active quest item channel", LoadTrigger(connStr, UseLetterQuestId) == 0, ref failures);
            Check("generic item-seeking sync sends active quest refresh", sender.LastNotiType == 0x023F && sender.NotiCount == 1, ref failures);

            AddItem(assetService, NonCarryEventItemId, 1);
            QuestService.SaveActiveQuests(connStr, CharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = NonCarryEventQuestId, TriggerValue = 0 },
            });
            var finishNonCarryEventQuest = QuestService.HandleFinishQuest(
                connStr,
                CharacterId,
                BuildQuestBody(NonCarryEventQuestId),
                assetService);
            Check("non-carry event item quest finish succeeds", IsSuccessAck(finishNonCarryEventQuest), ref failures);
            Check("non-carry event item is consumed on finish", CountItem(assetService, NonCarryEventItemId) == 0, ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildQuestBody(ushort questId)
        {
            var body = new byte[2];
            BitConverter.GetBytes(questId).CopyTo(body, 0);
            return body;
        }

        private static byte[] BuildSetTriggerBody(ushort questId, byte triggerType, bool increment)
        {
            var body = new byte[4];
            BitConverter.GetBytes(questId).CopyTo(body, 0);
            body[2] = triggerType;
            body[3] = increment ? (byte)1 : (byte)0;
            return body;
        }

        private static bool IsSuccessAck(byte[] ack)
        {
            return ack != null && ack.Length > 0 && ack[0] == 0x01;
        }

        private static bool TryReadAcceptTrigger(byte[] ack, out uint trigger)
        {
            trigger = 0;
            if (ack == null || ack.Length < 8 || ack[0] != 0x01)
                return false;
            trigger = BitConverter.ToUInt32(ack, 3);
            return true;
        }

        private static bool TryReadAcceptEventItem(byte[] ack, out ushort slot, out int itemId, out int count)
        {
            slot = 0;
            itemId = 0;
            count = 0;
            if (ack == null || ack.Length < 18 || ack[0] != 0x01 || ack[7] < 1)
                return false;

            slot = BitConverter.ToUInt16(ack, 8);
            itemId = (int)BitConverter.ToUInt32(ack, 10);
            count = (int)BitConverter.ToUInt32(ack, 14);
            return true;
        }

        private static bool TryReadFinishInsertedItem(byte[] ack, out ushort slot, out int itemId, out int count)
        {
            slot = 0;
            itemId = 0;
            count = 0;
            if (ack == null || ack.Length < 25 || ack[0] != 0x01)
                return false;

            int consumedCount = ack[12];
            int chainTypeOffset = 13 + consumedCount * 7;
            if (ack.Length < chainTypeOffset + 12 || ack[chainTypeOffset] != 0)
                return false;

            int insertedCount = ack[chainTypeOffset + 1];
            if (insertedCount <= 0)
                return false;

            int itemOffset = chainTypeOffset + 2;
            slot = BitConverter.ToUInt16(ack, itemOffset);
            itemId = (int)BitConverter.ToUInt32(ack, itemOffset + 2);
            count = (int)BitConverter.ToUInt32(ack, itemOffset + 6);
            return true;
        }

        private static uint LoadTrigger(string connStr, ushort questId)
        {
            var active = QuestService.LoadActiveQuests(connStr, CharacterId);
            var quest = QuestService.FindByQuestId(active, questId);
            return quest != null ? quest.TriggerValue : uint.MaxValue;
        }

        private static int CountItem(IAssetService assetService, int itemId)
        {
            using (var scope = assetService.OpenScope(CharacterId, AccountId))
            {
                return assetService.CountItem(scope, itemId);
            }
        }

        private static void AddItem(IAssetService assetService, int itemId, int count)
        {
            using (var scope = assetService.OpenScope(CharacterId, AccountId))
            {
                short assignedSlot;
                if (!assetService.TryAddItem(scope, itemId, count, out assignedSlot))
                    throw new InvalidOperationException($"failed to add item {itemId}");
                scope.Commit();
            }
        }

        private static void SeedAccount(string dbPath)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@mid", "quest-item-flow-test");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void MarkQuestCleared(string connStr, int questId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR REPLACE INTO character_invisible_falgs (character_id, slot_index, flag_value)
VALUES (@cid, @qid, 1);";
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.Parameters.AddWithValue("@qid", questId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void ClearIssue135State(string connStr)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM character_active_quests WHERE character_id=@cid AND quest_id IN (2042, 2043);";
                        cmd.Parameters.AddWithValue("@cid", CharacterId);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM character_items WHERE character_id=@cid AND item_template_id=@item;";
                        cmd.Parameters.AddWithValue("@cid", CharacterId);
                        cmd.Parameters.AddWithValue("@item", AganzoLetterItemId);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM character_invisible_falgs WHERE character_id=@cid AND slot_index IN (2042, 2043);";
                        cmd.Parameters.AddWithValue("@cid", CharacterId);
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private sealed class RecordingQuestSender : ISessionPacketSender
        {
            public RecordingQuestSender(int characterId, int accountId)
            {
                CharacterId = characterId;
                AccountId = accountId;
            }

            public int CharacterId { get; }
            public int AccountId { get; }
            public PlayerContext Player => null;
            public int NotiCount { get; private set; }
            public ushort LastNotiType { get; private set; }

            public Task SendPacketAsync(byte[] rawPacket)
            {
                return Task.CompletedTask;
            }

            public Task SendNotiAsync(ushort notiType, byte[] body)
            {
                NotiCount++;
                LastNotiType = notiType;
                return Task.CompletedTask;
            }

            public Task SendCmdAckAsync(ushort cmdType, byte[] body)
            {
                return Task.CompletedTask;
            }
        }
    }
}
