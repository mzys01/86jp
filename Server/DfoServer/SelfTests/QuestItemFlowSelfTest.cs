using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class QuestItemFlowSelfTest
    {
        private const int CharacterId = 135001;
        private const int LevelUpCharacterId = 135002;
        private const int AccountId = 135001;
        private const ushort GiveLetterQuestId = 2042;
        private const ushort UseLetterQuestId = 2043;
        private const int AganzoLetterItemId = 10089292;
        private const ushort NonCarryEventQuestId = 2578;
        private const int NonCarryEventItemId = 10100257;

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
            characterRepository.Create(new CharacterRecord
            {
                CharacterId = LevelUpCharacterId,
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes("quest-level-up-test"),
                Job = 0,
                GrowType = 0,
                Level = 1,
            });

            var assetService = new SqliteAssetService(dbPath, schemaPath);
            var connStr = SqliteDatabaseBootstrap.BuildConnectionString(dbPath);
            MarkQuestCleared(connStr, 2041);
            var failures = 0;

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

            RunQuestLevelUpStatsChecks(connStr, dbPath, schemaPath, characterRepository, assetService, ref failures);

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

        private static void RunQuestLevelUpStatsChecks(
            string connStr,
            string dbPath,
            string schemaPath,
            SqliteCharacterRepository characterRepository,
            IAssetService assetService,
            ref int failures)
        {
            var questId = SelectPlainExpQuest();
            Check("plain exp reward quest found", questId > 0, ref failures);
            if (questId <= 0)
                return;

            var reward = GameWorld.QuestData.GetRewardExp(questId, playerLevel: 1, playerJob: 0, playerGrowType: 0);
            var level2Threshold = (uint)ExpTableProvider.GetLevelThreshold(1);
            var startExp = reward.Exp >= level2Threshold ? 0u : level2Threshold - reward.Exp;

            characterRepository.UpdateLevelAndExp(LevelUpCharacterId, 1, startExp);
            SeedSubtype1Stats(dbPath, schemaPath, LevelUpCharacterId, job: 0, level: 1);
            var before = new SqliteSubtype1Repository(dbPath, schemaPath).Load(LevelUpCharacterId);

            QuestService.SaveActiveQuests(connStr, LevelUpCharacterId, new List<ActiveQuest>
            {
                new ActiveQuest { Slot = 0, QuestId = questId, TriggerValue = 0 },
            });

            var player = new PlayerContext
            {
                CharacterId = LevelUpCharacterId,
                Job = 0,
                GrowType = 0,
                Level = 1,
                Exp = startExp,
            };
            var sender = new RecordingQuestSender(LevelUpCharacterId, AccountId, player);
            var questManager = new QuestManager(sender, connStr, assetService);
            questManager.HandleFinishQuestAsync(0x003C, BuildQuestBody(questId)).GetAwaiter().GetResult();
            var ackExp = sender.LastAckBody != null && sender.LastAckBody.Length >= 8
                ? BitConverter.ToUInt32(sender.LastAckBody, 4)
                : 0;

            var record = characterRepository.GetById(LevelUpCharacterId);
            var after = new SqliteSubtype1Repository(dbPath, schemaPath).Load(LevelUpCharacterId);
            var expectedStats = CharacterStatComputer.BuildAdditionalInfo(0, player.Level);
            var expectedHp = BitConverter.ToUInt32(expectedStats, 0);
            var expectedPhysicalAttack = BitConverter.ToInt16(expectedStats, 8);

            Check("quest reward ack grants exp", ackExp > 0, ref failures);
            Check("quest reward levels character in memory", player.Level > 1, ref failures);
            Check("quest reward level persisted", record != null && record.Level == player.Level, ref failures);
            Check("quest reward subtype1 hp recomputed",
                before != null && after != null && after.StatHpMax == expectedHp && after.StatHpMax != before.StatHpMax,
                ref failures);
            Check("quest reward subtype1 attack recomputed",
                after != null && after.StatPhysicalAttack == expectedPhysicalAttack,
                ref failures);
            Check("quest reward sends subtype0 before exp notification",
                SendsSubtype0BeforeExp(sender, player.Level), ref failures);
            Check("quest reward sends subtype1 stats before exp notification",
                SendsSubtype1StatsBeforeExp(sender, expectedHp, expectedPhysicalAttack), ref failures);
            Check("quest reward sends exp notification", sender.NotiTypes.Contains(0x0025), ref failures);
        }

        private static bool SendsSubtype0BeforeExp(RecordingQuestSender sender, byte expectedLevel)
        {
            var subtype0Index = sender.Notis.FindIndex(n =>
                n.Item1 == 0x0002 && IsSubtype0LevelRefresh(n.Item2, expectedLevel));
            var expIndex = sender.Notis.FindIndex(n => n.Item1 == 0x0025);
            return subtype0Index >= 0 && expIndex >= 0 && subtype0Index < expIndex;
        }

        private static bool SendsSubtype1StatsBeforeExp(
            RecordingQuestSender sender,
            uint expectedHp,
            short expectedPhysicalAttack)
        {
            var subtype1Index = sender.Notis.FindIndex(n =>
                n.Item1 == 0x0002 && IsSubtype1StatRefresh(n.Item2, expectedHp, expectedPhysicalAttack));
            var expIndex = sender.Notis.FindIndex(n => n.Item1 == 0x0025);
            return subtype1Index >= 0 && expIndex >= 0 && subtype1Index < expIndex;
        }

        private static bool IsSubtype0LevelRefresh(byte[] body, byte expectedLevel)
        {
            if (body == null || body.Length < 12 || body[0] != 0)
                return false;

            int nameLength = BitConverter.ToInt32(body, 5);
            if (nameLength < 0)
                return false;

            int levelOffset = 9 + nameLength + 2;
            return levelOffset < body.Length && body[levelOffset] == expectedLevel;
        }

        private static bool IsSubtype1StatRefresh(
            byte[] body,
            uint expectedHp,
            short expectedPhysicalAttack)
        {
            if (body == null || body.Length < 23 || body[0] != 1)
                return false;

            var count = BitConverter.ToUInt16(body, 1);
            if (count == 0)
                return false;

            const int subtype1Offset = 5;
            var statCount = BitConverter.ToInt32(body, subtype1Offset + 4);
            var hp = BitConverter.ToUInt32(body, subtype1Offset + 8);
            var physicalAttack = BitConverter.ToInt16(body, subtype1Offset + 16);
            return statCount == 83
                && hp == expectedHp
                && physicalAttack == expectedPhysicalAttack;
        }

        private static ushort SelectPlainExpQuest()
        {
            ushort[] candidates =
            {
                GiveLetterQuestId,
                UseLetterQuestId,
                1776,
                1016,
                101,
            };

            foreach (var questId in candidates)
            {
                var reward = GameWorld.QuestData.GetRewardExp(questId, playerLevel: 1, playerJob: 0, playerGrowType: 0);
                if (reward.Exp > 0 && reward.ChainType == 0)
                    return questId;
            }

            return 0;
        }

        private static void SeedSubtype1Stats(string dbPath, string schemaPath, int characterId, byte job, byte level)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(dbPath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR IGNORE INTO character_subtype1_fields(character_id) VALUES(@cid);";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.ExecuteNonQuery();
                }
            }

            var stats = CharacterStatComputer.BuildAdditionalInfo(job, level);
            new SqliteSubtype1Repository(dbPath, schemaPath).UpdateCombatStats(characterId, stats);
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
            public RecordingQuestSender(int characterId, int accountId, PlayerContext player = null)
            {
                CharacterId = characterId;
                AccountId = accountId;
                Player = player;
            }

            public int CharacterId { get; }
            public int AccountId { get; }
            public PlayerContext Player { get; }
            public int NotiCount { get; private set; }
            public ushort LastNotiType { get; private set; }
            public List<ushort> NotiTypes { get; } = new List<ushort>();
            public List<Tuple<ushort, byte[]>> Notis { get; } = new List<Tuple<ushort, byte[]>>();
            public byte[] LastAckBody { get; private set; }

            public Task SendPacketAsync(byte[] rawPacket)
            {
                return Task.CompletedTask;
            }

            public Task SendNotiAsync(ushort notiType, byte[] body)
            {
                NotiCount++;
                LastNotiType = notiType;
                NotiTypes.Add(notiType);
                Notis.Add(Tuple.Create(notiType, body));
                return Task.CompletedTask;
            }

            public Task SendCmdAckAsync(ushort cmdType, byte[] body)
            {
                LastAckBody = body;
                return Task.CompletedTask;
            }
        }
    }
}
