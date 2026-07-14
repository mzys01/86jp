using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.Game.Skills
{
    public static class BuySkillSelfTest
    {
        private static int _pass, _fail;

        public static int Run()
        {
            Console.WriteLine("=== BUY_SKILL self-test ===");
            _pass = 0;
            _fail = 0;

            SkillStaticData sd = null;
            try { sd = SkillDataProvider.GetSkill(0, 64); }
            catch (Exception ex) { Console.WriteLine("  PVF read failed: " + ex.Message); }

            Check("SkillDataProvider finds skill 64", sd != null);
            if (sd != null)
            {
                Check($"skill64 name='{sd.Name}'", sd.Name != null && sd.Name.Contains("十字"));
                Check($"skill64 active={sd.IsActive}", sd.IsActive);
                Check($"skill64 first SP cost={FirstCost(sd)}", FirstCost(sd) == 15);
                Check($"skill64 maxLevel={sd.MaxLevel}", sd.MaxLevel == 60);
                Check($"skill64 requiredLevel={sd.RequiredLevel}", sd.RequiredLevel == 15);
            }

            var firstCost = sd != null ? sd.SpCostFor(0, 1) : 15;
            var secondCost = sd != null ? sd.SpCostFor(1, 2) : firstCost;
            const byte testLevel = 86;
            const int cid = 999001;
            var startingSp = firstCost + secondCost + 7;
            var afterFirst = startingSp - firstCost;
            var afterSecond = afterFirst - secondCost;

            byte[] reqBody = { 0x00, 0x01, 0x40, 0x00, 0x00, 0x01, 0x00, 0xB7, 0x8D, 0x0A, 0x8C };
            Check("request skillTree=0", reqBody[0] == 0);
            Check("request count=1", reqBody[1] == 1);
            Check("request skillIndex=64", reqBody[2] == 64);
            Check("request isRefund=0", reqBody[4] == 0);
            Check("request level byte=1", reqBody[5] == 1);

            CheckCalculatedPointStateBootstrap(testLevel);
            CheckSeedFromSnapshotCreatesPointState(testLevel);
            CheckSkillTreeIndexSurvivesSelectLoad(testLevel);
            CheckDarkKnightInitialSkillLayout();
            CheckDarkKnightComboSkillInfoPersists(testLevel);

            string tempDb = Path.Combine(Path.GetTempPath(), "buyskill_selftest.db");
            DeleteSqliteFiles(tempDb);

            var repo = new SqliteCharacterProgressRepository(tempDb, ServerPaths.SchemaFilePath);
            var charRepo = new Game.Characters.SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath);
            EnsureTestCharacter(tempDb, cid, testLevel);
            var seed = new SkillInfoSnapshot();
            var p0 = new SkillInfoPageSnapshot { HeaderValue = 0x0005 };
            p0.Entries.Add(new SkillInfoEntrySnapshot { Slot = 0, SkillId = 5, Level = 1 });
            p0.Entries.Add(new SkillInfoEntrySnapshot { Slot = 1, SkillId = 46, Level = 1 });
            p0.Entries.Add(new SkillInfoEntrySnapshot { Slot = 2, SkillId = 169, Level = 1 });
            seed.Pages.Add(p0);
            seed.Pages.Add(new SkillInfoPageSnapshot { HeaderValue = 0x2BF2 });
            SeedSkillProgress(repo, cid, seed, testLevel, startingSp);

            var entries = new List<BuySkillEntry>
            {
                new BuySkillEntry { SkillIndex = 64, Level = 0, IsRefund = 0 }
            };
            var result = RunBuy(repo, cid, entries, testLevel);
            Check("learn result exists", result != null);
            if (result != null)
            {
                Check("learn success", result.Success);
                Check($"learn remaining SP={result.RemainSp}, expected {afterFirst}", result.RemainSp == afterFirst);
                Check("learn ack entry count=1", result.Entries.Count == 1);
                if (result.Entries.Count == 1)
                {
                    var e = result.Entries[0];
                    Check("learn ack skillId=64", e.SkillId == 64);
                    Check("learn ack level=1", e.Level == 1);
                    Check("learn ack slot=3", e.Slot == 3);
                    Check("learn ack hasCmd=false", !e.HasCmd);
                }

                var ack = BuySkillAckBuilder.Build(result);
                byte[] expectedAck =
                {
                    0x01, 0x00,
                    (byte)(afterFirst & 0xFF), (byte)((afterFirst >> 8) & 0xFF),
                    0x25, 0x00,
                    0x01, 0x03, 0x40, 0x00, 0x01, 0x00
                };
                Check($"learn ACK bytes={ToHex(ack)} expected={ToHex(expectedAck)}", BytesEqual(ack, expectedAck));
            }

            var reload = repo.LoadSkills(cid);
            var page0 = reload.Pages.Count > 0 ? reload.Pages[0] : null;
            var page1 = reload.Pages.Count > 1 ? reload.Pages[1] : null;
            var learned = page0?.Entries.Find(x => x.SkillId == 64);
            Check("persisted skill64 exists", learned != null);
            if (learned != null)
            {
                Check("persisted skill64 slot=3", learned.Slot == 3);
                Check("persisted skill64 level=1", learned.Level == 1);
            }
            Check($"protocol Tail0 mirror={reload.Tail0}, expected 37", reload.Tail0 == 37);
            Check($"protocol Tail1 mirror={reload.Tail1}, expected 0", reload.Tail1 == 0);
            Check($"page0 header mirror={page0?.HeaderValue ?? 0}, expected {afterFirst}", page0 != null && page0.HeaderValue == afterFirst);
            Check($"page1 header preserved={page1?.HeaderValue ?? 0}, expected 0x2BF2", page1 != null && page1.HeaderValue == 0x2BF2);

            var pointsAfterFirst = repo.LoadSkillPointState(cid);
            Check("skill point state exists", pointsAfterFirst != null);
            Check($"state remaining SP={pointsAfterFirst?.RemainingSp ?? -1}, expected {afterFirst}",
                pointsAfterFirst != null && pointsAfterFirst.RemainingSp == afterFirst);

            var selectData = new SqliteSelectCharacterDataSource(tempDb, ServerPaths.SchemaFilePath, charRepo);
            var selectSnapshot = selectData.Load(cid, 1);
            Check($"select init Tail0={selectSnapshot.InitializationSnapshot.SkillInfo.Tail0}, expected 37",
                selectSnapshot.InitializationSnapshot.SkillInfo.Tail0 == 37);
            Check($"select init Tail1={selectSnapshot.InitializationSnapshot.SkillInfo.Tail1}, expected 0",
                selectSnapshot.InitializationSnapshot.SkillInfo.Tail1 == 0);
            Check($"select init page1 header={selectSnapshot.InitializationSnapshot.SkillInfo.Pages[1].HeaderValue}, expected 0x2BF2",
                selectSnapshot.InitializationSnapshot.SkillInfo.Pages[1].HeaderValue == 0x2BF2);

            var refundInitial = RunBuy(repo, cid, new List<BuySkillEntry>
            {
                new BuySkillEntry { SkillIndex = 5, Level = 0, IsRefund = 1 }
            }, testLevel);
            Check("initial skill refund is ignored", refundInitial != null && refundInitial.Success && refundInitial.Entries.Count == 0);
            Check($"initial skill refund keeps remaining SP={refundInitial?.RemainSp ?? 0}, expected {afterFirst}",
                refundInitial != null && refundInitial.RemainSp == afterFirst);

            var upgraded = RunBuy(repo, cid, entries, testLevel);
            Check("upgrade success", upgraded != null && upgraded.Success);
            Check($"upgrade remaining SP={upgraded?.RemainSp ?? 0}, expected {afterSecond}",
                upgraded != null && upgraded.RemainSp == afterSecond);
            var reload2 = repo.LoadSkills(cid);
            var up64 = reload2.Pages.Count > 0 ? reload2.Pages[0].Entries.Find(x => x.SkillId == 64) : null;
            Check($"upgrade persisted slot={up64?.Slot ?? 255} level={up64?.Level ?? 0}",
                up64 != null && up64.Slot == 3 && up64.Level == 2);

            SeedStackableItem(tempDb, cid, 64, SkillResetConsumableService.ForgetRiverWaterItemTemplateId, 2);
            var inventoryStore = new SqliteInventoryStore(tempDb, ServerPaths.SchemaFilePath);
            var refunded = RunBuyWithRefundConsumable(inventoryStore, repo, cid, new List<BuySkillEntry>
            {
                new BuySkillEntry { SkillIndex = 64, Level = 0, IsRefund = 1 }
            }, testLevel);
            Check("learned skill refund success", refunded != null && refunded.Success);
            Check($"learned skill refund remaining SP={refunded?.RemainSp ?? 0}, expected {afterFirst}",
                refunded != null && refunded.RemainSp == afterFirst);
            Check("learned skill refund consumes forget-river water",
                inventoryStore.CountItem(cid, SkillResetConsumableService.ForgetRiverWaterItemTemplateId) == 1);
            var reloadAfterRefund = repo.LoadSkills(cid);
            var refunded64 = reloadAfterRefund.Pages.Count > 0 ? reloadAfterRefund.Pages[0].Entries.Find(x => x.SkillId == 64) : null;
            Check($"learned skill refund keeps slot={refunded64?.Slot ?? 255} level={refunded64?.Level ?? 0}",
                refunded64 != null && refunded64.Slot == 3 && refunded64.Level == 1);

            var page1Learned = BuySkillService.Execute(repo, cid, 0, 1, entries, level: testLevel);
            Check("page1 learn success uses page1 SP", page1Learned != null && page1Learned.Success);
            Check($"page1 learn remaining SP={page1Learned?.RemainSp ?? 0}, expected {0x2BF2 - firstCost}",
                page1Learned != null && page1Learned.RemainSp == 0x2BF2 - firstCost);
            var reloadPage1 = repo.LoadSkills(cid);
            Check($"page1 header updated={reloadPage1.Pages[1].HeaderValue}, expected {0x2BF2 - firstCost}",
                reloadPage1.Pages[1].HeaderValue == 0x2BF2 - firstCost);
            Check($"page0 header preserved after page1 learn={reloadPage1.Pages[0].HeaderValue}, expected {afterFirst}",
                reloadPage1.Pages[0].HeaderValue == afterFirst);

            var syncedAtNextLevel = SkillStateService.LoadAndSync(
                repo, cid, 0, (byte)(testLevel + 1), 0, 0, persist: true);
            var gainedSp = SpTableProvider.GetSpAtLevel(testLevel + 1);
            Check($"level-up sync adds SP: {syncedAtNextLevel.Points.RemainingSp}, expected {afterFirst + gainedSp}",
                syncedAtNextLevel.Points.RemainingSp == afterFirst + gainedSp);
            Check($"level-up sync keeps page1 SP={syncedAtNextLevel.Skills.Pages[1].HeaderValue}, expected {0x2BF2 - firstCost + gainedSp}",
                syncedAtNextLevel.Skills.Pages[1].HeaderValue == 0x2BF2 - firstCost + gainedSp);
            var protocolState = SkillStateService.GetProtocolState(
                syncedAtNextLevel.Skills,
                syncedAtNextLevel.Points);
            Check($"0x0025 page1 SP={protocolState.Page1Sp}, expected {0x2BF2 - firstCost + gainedSp}",
                protocolState.Page1Sp == 0x2BF2 - firstCost + gainedSp);

            string tempDb2 = Path.Combine(Path.GetTempPath(), "buyskill_selftest2.db");
            DeleteSqliteFiles(tempDb2);
            var repo2 = new SqliteCharacterProgressRepository(tempDb2, ServerPaths.SchemaFilePath);
            _ = new Game.Characters.SqliteCharacterRepository(tempDb2, ServerPaths.SchemaFilePath);
            EnsureTestCharacter(tempDb2, cid, testLevel);
            var poorSp = Math.Max(0, firstCost - 1);
            var poorSeed = new SkillInfoSnapshot();
            poorSeed.Pages.Add(new SkillInfoPageSnapshot { HeaderValue = 0x0005 });
            poorSeed.Pages.Add(new SkillInfoPageSnapshot { HeaderValue = 0x2BF2 });
            SeedSkillProgress(repo2, cid, poorSeed, testLevel, poorSp);
            var poor = RunBuy(repo2, cid, entries, testLevel);
            Check("SP-insufficient purchase fails", poor != null && !poor.Success);
            var reload3 = repo2.LoadSkills(cid);
            var notLearned = reload3.Pages.Count > 0 ? reload3.Pages[0].Entries.Find(x => x.SkillId == 64) : null;
            Check("SP-insufficient does not add skill64", notLearned == null);
            Check($"SP-insufficient keeps Tail1={reload3.Tail1}, expected 0", reload3.Tail1 == 0);

            string tempDb5 = Path.Combine(Path.GetTempPath(), "buyskill_refund_no_water_selftest.db");
            DeleteSqliteFiles(tempDb5);
            var repo5 = new SqliteCharacterProgressRepository(tempDb5, ServerPaths.SchemaFilePath);
            EnsureTestCharacter(tempDb5, cid, testLevel);
            var noWaterSeed = new SkillInfoSnapshot();
            noWaterSeed.Pages.Add(new SkillInfoPageSnapshot { HeaderValue = (ushort)afterSecond });
            noWaterSeed.Pages[0].Entries.Add(new SkillInfoEntrySnapshot { Slot = 3, SkillId = 64, Level = 2 });
            noWaterSeed.Pages.Add(new SkillInfoPageSnapshot { HeaderValue = 0x2BF2 });
            SeedSkillProgress(repo5, cid, noWaterSeed, testLevel, afterSecond);
            var noWaterStore = new SqliteInventoryStore(tempDb5, ServerPaths.SchemaFilePath);
            var noWaterRefund = RunBuyWithRefundConsumable(noWaterStore, repo5, cid, new List<BuySkillEntry>
            {
                new BuySkillEntry { SkillIndex = 64, Level = 0, IsRefund = 1 }
            }, testLevel);
            var noWaterReload = repo5.LoadSkills(cid);
            var noWaterSkill64 = noWaterReload.Pages.Count > 0 ? noWaterReload.Pages[0].Entries.Find(x => x.SkillId == 64) : null;
            Check("learned skill refund without forget-river water fails", noWaterRefund != null && !noWaterRefund.Success);
            Check("learned skill refund without forget-river water keeps skill level",
                noWaterSkill64 != null && noWaterSkill64.Level == 2);

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static BuySkillResult RunBuy(
            SqliteCharacterProgressRepository repo,
            int cid,
            List<BuySkillEntry> entries,
            byte level)
        {
            try { return BuySkillService.Execute(repo, cid, 0, 0, entries, level: level); }
            catch (Exception ex)
            {
                Console.WriteLine("  BuySkillService exception: " + ex);
                return null;
            }
        }

        private static BuySkillResult RunBuyWithRefundConsumable(
            IInventoryStore inventoryStore,
            SqliteCharacterProgressRepository repo,
            int cid,
            List<BuySkillEntry> entries,
            byte level)
        {
            try
            {
                return BuySkillService.ExecuteWithRefundConsumable(
                    inventoryStore,
                    repo,
                    cid,
                    1,
                    0,
                    0,
                    entries,
                    level: level);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  BuySkillService refund consumable exception: " + ex);
                return null;
            }
        }

        private static void CheckCalculatedPointStateBootstrap(byte level)
        {
            var skills = new SkillInfoSnapshot { Tail1 = ushort.MaxValue, HasTailValues = true };
            skills.Pages.Add(new SkillInfoPageSnapshot { HeaderValue = ushort.MaxValue });
            var calculated = SkillPointCalculator.Calculate(0, level, 0, 0, skills);
            var resolved = SkillStateService.ResolvePointState(skills, null, 0, level, 0, 0);
            Check($"missing point row bootstraps calculated SP={resolved.RemainingSp}",
                resolved.RemainingSp == calculated.RemainingSp);
        }

        private static void CheckSeedFromSnapshotCreatesPointState(byte level)
        {
            const int seedCid = 999002;
            string tempDb = Path.Combine(Path.GetTempPath(), "buyskill_seed_selftest.db");
            DeleteSqliteFiles(tempDb);

            var repo = new SqliteCharacterProgressRepository(tempDb, ServerPaths.SchemaFilePath);
            _ = new Game.Characters.SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath);
            EnsureTestCharacter(tempDb, seedCid, level);
            var skills = InitialCharacterSkills.Build(0);
            var snapshot = new SelectCharacterInitializationSnapshot { SkillInfo = skills };

            repo.SeedFromSnapshot(seedCid, snapshot);

            var points = repo.LoadSkillPointState(seedCid);
            var reloaded = repo.LoadSkills(seedCid);
            var calculated = SkillPointCalculator.Calculate(0, level, 0, 0, reloaded);
            Check("seed snapshot creates skill point state", points != null && points.HasPersistedState);
            Check($"seed snapshot remaining SP={points?.RemainingSp ?? -1}, expected {calculated.RemainingSp}",
                points != null && points.RemainingSp == calculated.RemainingSp);
            Check($"seed snapshot Tail0 mirror={reloaded.Tail0}, expected TP={calculated.RemainingTp}",
                reloaded.Tail0 == calculated.RemainingTp);
        }

        private static void CheckSkillTreeIndexSurvivesSelectLoad(byte level)
        {
            const int skillTreeCid = 999003;
            string tempDb = Path.Combine(Path.GetTempPath(), "buyskill_skilltree_selftest.db");
            DeleteSqliteFiles(tempDb);

            var repo = new SqliteCharacterProgressRepository(tempDb, ServerPaths.SchemaFilePath);
            var charRepo = new Game.Characters.SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath);
            EnsureTestCharacter(tempDb, skillTreeCid, level);
            var skills = InitialCharacterSkills.Build(0);
            var points = SkillStateService.ResolvePointState(skills, null, 0, level, 0, 0);
            SkillStateService.Persist(repo, skillTreeCid, skills, points);

            var subtype1Repo = new SqliteSubtype1Repository(tempDb, ServerPaths.SchemaFilePath);
            subtype1Repo.UpdateSkillTreeIndex(skillTreeCid, 1);
            Check($"skill tree index persists={subtype1Repo.LoadSkillTreeIndex(skillTreeCid) ?? 255}, expected 1",
                subtype1Repo.LoadSkillTreeIndex(skillTreeCid) == 1);

            var connStr = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                SqliteSubtype0FieldsRepository.Save(conn, skillTreeCid, new UserInfoMinimumTailSnapshot { SkillTreeIndex = 0 });
            }

            var oldDbEnv = Environment.GetEnvironmentVariable("INVENTORY_DATABASE_PATH");
            try
            {
                Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", tempDb);
                var selectData = new SqliteSelectCharacterDataSource(tempDb, ServerPaths.SchemaFilePath, charRepo);
                var selectSnapshot = selectData.Load(skillTreeCid, 1);
                Check($"select load skill tree index={selectSnapshot.CharacterRecord?.Subtype0Tail?.SkillTreeIndex ?? 255}, expected 1",
                    selectSnapshot.CharacterRecord != null &&
                    selectSnapshot.CharacterRecord.Subtype0Tail != null &&
                    selectSnapshot.CharacterRecord.Subtype0Tail.SkillTreeIndex == 1);
            }
            finally
            {
                Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", oldDbEnv);
            }
        }

        private static void CheckDarkKnightComboSkillInfoPersists(byte level)
        {
            const int characterId = 999004;
            string tempDb = Path.Combine(Path.GetTempPath(), "buyskill_dark_knight_combo_selftest.db");
            DeleteSqliteFiles(tempDb);

            var repo = new SqliteCharacterProgressRepository(tempDb, ServerPaths.SchemaFilePath);
            var comboRepo = new SqliteDarkKnightComboSkillRepository(tempDb, ServerPaths.SchemaFilePath);
            var comboService = new DarkKnightComboSkillService(repo, comboRepo);
            var charRepo = new Game.Characters.SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath);
            EnsureTestCharacter(tempDb, characterId, level, job: 9);

            var skills = BuildDarkKnightSkillSnapshot(new (ushort SkillId, byte Slot)[]
            {
                (5, 10),
                (8, 11),
                (46, 12),
                (108, 200),
                (118, 14),
                (119, 15),
                (120, 16),
                (121, 17),
                (122, 18),
                (123, 19),
                (169, 6),
            });
            SeedSkillProgress(repo, characterId, skills, level, remainingSp: 100);

            var body = BuildDarkKnightComboPage(0);
            Check("dark knight combo save accepts raw body",
                comboService.SaveComboSkillInfo(characterId, body) > 0);
            Check("dark knight combo save rejects malformed body",
                comboService.SaveComboSkillInfo(characterId, new byte[] { 0x00, 0x02, 0x76 }) == 0);
            Check("dark knight combo save rejects unsupported page",
                comboService.SaveComboSkillInfo(characterId, BuildDarkKnightComboPage(2)) == 0);

            var reloaded = repo.LoadSkills(characterId);
            Check("dark knight combo save does not rewrite ordinary skill slots",
                SlotOf(reloaded.Pages[0], 118) == 14 &&
                SlotOf(reloaded.Pages[0], 46) == 12 &&
                BytesEqual(FirstComboSkillInfoBody(comboRepo, characterId), body));

            var dataSource = new SqliteSelectCharacterDataSource(tempDb, ServerPaths.SchemaFilePath, charRepo);
            var selectPackets = new List<byte[]>(SelectCharacterPacketBuilder.BuildPacketStream(dataSource, characterId, 1));
            byte[] expectedInitBody;
            Check("dark knight select init sends combo restore noti",
                DarkKnightComboSkillInfoCodec.TryBuildNotificationBody(new[] { body }, out expectedInitBody) &&
                selectPackets.Exists(p => IsPacket(p, 0x00, 0x01C0, expectedInitBody)));

            SeedSkillProgress(repo, characterId, skills, level, remainingSp: 100);
            var autoComboSave = comboService.SaveAutoComboSkillInfo(characterId, body);
            var afterAutoCombo = repo.LoadSkills(characterId);
            Check("dark knight auto combo moves saved child quick-slot duplicates out of shortcut slots",
                autoComboSave.Saved &&
                autoComboSave.QuickSlotsCleaned == 3 &&
                !DarkKnightComboSkillInfoCodec.IsShortcutSlot(SlotOf(afterAutoCombo.Pages[0], 5)) &&
                !DarkKnightComboSkillInfoCodec.IsShortcutSlot(SlotOf(afterAutoCombo.Pages[0], 8)) &&
                !DarkKnightComboSkillInfoCodec.IsShortcutSlot(SlotOf(afterAutoCombo.Pages[0], 108)) &&
                SlotOf(afterAutoCombo.Pages[0], 46) == 12 &&
                BytesEqual(FirstComboSkillInfoBody(comboRepo, characterId), body));

            byte[] singleChildBody =
            {
                0x00, 0x06,
                0x76, 0x00, 0x01, 0x08, 0x00,
                0x77, 0x00, 0x00,
                0x78, 0x00, 0x00,
                0x79, 0x00, 0x00,
                0x7A, 0x00, 0x00,
                0x7B, 0x00, 0x00,
            };
            SeedSkillProgress(repo, characterId, skills, level, remainingSp: 100);
            comboService.SaveComboSkillInfo(characterId, singleChildBody);
            var inferredMoveResult = comboService.SwapDarkKnightSkillSlot(characterId, 0, 54, 8);
            var afterSwap = repo.LoadSkills(characterId);
            Check("dark knight inferred combo slot move keeps destination shortcut state",
                inferredMoveResult &&
                BytesEqual(FirstComboSkillInfoBody(comboRepo, characterId), singleChildBody) &&
                SlotOf(afterSwap.Pages[0], 8) == 8 &&
                SlotOf(afterSwap.Pages[0], 5) == 10 &&
                SlotOf(afterSwap.Pages[0], 108) == 200);
        }

        private static void CheckDarkKnightInitialSkillLayout()
        {
            const int characterId = 999014;
            string tempDb = Path.Combine(Path.GetTempPath(), "buyskill_dark_knight_initial_selftest.db");
            DeleteSqliteFiles(tempDb);

            var repo = new SqliteCharacterProgressRepository(tempDb, ServerPaths.SchemaFilePath);
            var comboRepo = new SqliteDarkKnightComboSkillRepository(tempDb, ServerPaths.SchemaFilePath);
            var charRepo = new Game.Characters.SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath);
            EnsureTestCharacter(tempDb, characterId, level: 1, job: 9);

            var initialSkills = InitialCharacterSkills.Build(9);
            Check("dark knight initial skills keep ordinary quick slots",
                initialSkills.Pages.Count >= 2 &&
                SlotOf(initialSkills.Pages[0], 118) == 0 &&
                SlotOf(initialSkills.Pages[0], 169) == 6 &&
                SlotOf(initialSkills.Pages[0], 108) == 10);

            var initialBodies = DarkKnightInitialSkillLayout.BuildDefaultComboSkillInfoBodies(initialSkills);
            var page0Roots = initialBodies.Count > 0
                ? DarkKnightComboSkillInfoCodec.GetRootSkillIds(initialBodies[0])
                : new HashSet<ushort>();
            var page0Children = initialBodies.Count > 0
                ? DarkKnightComboSkillInfoCodec.GetChildSkillIds(initialBodies[0])
                : new HashSet<ushort>();
            Check("dark knight initial combo pages are derived from PVF combo sets",
                initialBodies.Count == 2 &&
                DarkKnightComboSkillInfoCodec.IsValidPageBlock(initialBodies[0]) &&
                DarkKnightComboSkillInfoCodec.IsValidPageBlock(initialBodies[1]) &&
                initialBodies[0][0] == 0 &&
                initialBodies[1][0] == 1 &&
                page0Roots.Contains(118) &&
                page0Roots.Contains(119) &&
                page0Children.Contains(46) &&
                page0Children.Contains(8) &&
                page0Children.Contains(5) &&
                page0Children.Contains(108));

            var dataSource = new SqliteSelectCharacterDataSource(tempDb, ServerPaths.SchemaFilePath, charRepo);
            dataSource.InitializeNewCharacter(characterId, 1, 9);

            var persistedBodies = comboRepo.LoadPageBodies(characterId);
            var reloadedSkills = repo.LoadSkills(characterId);
            Check("dark knight new character persists quick slots and combo pages",
                SlotOf(reloadedSkills.Pages[0], 118) == 0 &&
                SlotOf(reloadedSkills.Pages[0], 108) == 10 &&
                persistedBodies.Count == initialBodies.Count &&
                BytesEqual(persistedBodies[0], initialBodies[0]) &&
                BytesEqual(persistedBodies[1], initialBodies[1]));
            Check("dark knight combo pages are not stored in legacy init bodies",
                CountLegacyComboInitBodies(tempDb, characterId) == 0);

            byte[] expectedInitBody;
            var selectPackets = new List<byte[]>(SelectCharacterPacketBuilder.BuildPacketStream(dataSource, characterId, 1));
            Check("dark knight new character select init sends default combo restore noti",
                DarkKnightComboSkillInfoCodec.TryBuildNotificationBody(initialBodies, out expectedInitBody) &&
                selectPackets.Exists(p => IsPacket(p, 0x00, 0x01C0, expectedInitBody)));
        }


        private static int CountLegacyComboInitBodies(string databasePath, int characterId)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM character_init_bodies WHERE character_id=@cid AND noti_type=0x01FD";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        private static byte[] BuildDarkKnightComboPage(byte page)
        {
            return new byte[]
            {
                page, 0x06,
                0x76, 0x00, 0x02, 0x2E, 0x00, 0x08, 0x00,
                0x77, 0x00, 0x02, 0x05, 0x00, 0x6C, 0x00,
                0x78, 0x00, 0x00,
                0x79, 0x00, 0x00,
                0x7A, 0x00, 0x00,
                0x7B, 0x00, 0x00,
            };
        }

        private static SkillInfoSnapshot BuildDarkKnightSkillSnapshot((ushort SkillId, byte Slot)[] slots)
        {
            var skills = new SkillInfoSnapshot();
            var page0 = new SkillInfoPageSnapshot { HeaderValue = 0x0005 };
            foreach (var seed in slots)
            {
                page0.Entries.Add(new SkillInfoEntrySnapshot
                {
                    Slot = seed.Slot,
                    SkillId = seed.SkillId,
                    Level = 1,
                });
            }
            skills.Pages.Add(page0);

            var page1 = new SkillInfoPageSnapshot { HeaderValue = 0x2BF2 };
            foreach (var entry in page0.Entries)
            {
                page1.Entries.Add(new SkillInfoEntrySnapshot
                {
                    Slot = entry.Slot,
                    SkillId = entry.SkillId,
                    Level = entry.Level,
                });
            }
            skills.Pages.Add(page1);
            return skills;
        }

        private static byte[] FirstComboSkillInfoBody(SqliteDarkKnightComboSkillRepository comboRepo, int characterId)
        {
            var bodies = comboRepo.LoadPageBodies(characterId);
            return bodies.Count > 0 ? bodies[0] : null;
        }

        private static int SlotOf(SkillInfoPageSnapshot page, ushort skillId)
        {
            var entry = page?.Entries.Find(x => x.SkillId == skillId);
            return entry == null ? -1 : entry.Slot;
        }

        private static void SeedSkillProgress(
            SqliteCharacterProgressRepository repo,
            int cid,
            SkillInfoSnapshot skills,
            byte level,
            int remainingSp)
        {
            var calculated = SkillPointCalculator.Calculate(0, level, 0, 0, skills);
            var points = new SkillPointState
            {
                TotalSp = calculated.TotalSp,
                RemainingSp = remainingSp,
                TotalTp = calculated.TotalTp,
                RemainingTp = calculated.TotalTp,
                SyncedLevel = level,
                HasPersistedState = true,
            };
            SkillStateService.Persist(repo, cid, skills, points);
        }

        private static int FirstCost(SkillStaticData data)
        {
            return data.SpCostPerLevel.Length > 0 ? data.SpCostPerLevel[0] : -1;
        }

        private static void EnsureTestCharacter(string databasePath, int characterId, byte level, byte job = 0)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (1, 'selftest', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name)
VALUES (@cid, 1, 'selftest');";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE characters
SET job = @job, level = @level, bonus_sp = 0, bonus_tp = 0
WHERE character_id = @cid;";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@job", (int)job);
                    cmd.Parameters.AddWithValue("@level", (int)level);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void SeedStackableItem(string databasePath, int characterId, short slotIndex, int itemTemplateId, int stackCount)
        {
            using (var conn = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(databasePath)))
            {
                conn.Open();
                using (var delete = conn.CreateCommand())
                {
                    delete.CommandText = @"
DELETE FROM character_items
WHERE character_id = @cid
  AND list_type = 0
  AND slot_index = @slot;";
                    delete.Parameters.AddWithValue("@cid", characterId);
                    delete.Parameters.AddWithValue("@slot", slotIndex);
                    delete.ExecuteNonQuery();
                }

                using (var insert = conn.CreateCommand())
                {
                    insert.CommandText = @"
INSERT INTO character_items (
    owner_scope, owner_id, character_id, list_type, slot_index, item_template_id, item_kind,
    stack_count, instance_value, durability, seal_flag, option_value, expire_time, marker_16,
    pet_serial_or_handle, extra_json)
VALUES (
    'character', @cid, @cid, 0, @slot, @itemId, 'stackable',
    @count, @count, 0, 0, 0, 0, 0,
    0, '{}');";
                    insert.Parameters.AddWithValue("@cid", characterId);
                    insert.Parameters.AddWithValue("@slot", slotIndex);
                    insert.Parameters.AddWithValue("@itemId", itemTemplateId);
                    insert.Parameters.AddWithValue("@count", stackCount);
                    insert.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteSqliteFiles(string databasePath)
        {
            foreach (var ext in new[] { "", "-wal", "-shm" })
                try { if (File.Exists(databasePath + ext)) File.Delete(databasePath + ext); } catch { }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++; else _fail++;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool IsPacket(byte[] packet, byte command, ushort type, byte[] body)
        {
            if (packet == null || packet.Length < 15)
                return false;
            if (packet[0] != command || BitConverter.ToUInt16(packet, 1) != type)
                return false;

            var packetBody = new byte[packet.Length - 15];
            Buffer.BlockCopy(packet, 15, packetBody, 0, packetBody.Length);
            return BytesEqual(packetBody, body);
        }

        private static string ToHex(byte[] b)
        {
            if (b == null) return "(null)";
            var sb = new System.Text.StringBuilder();
            foreach (var x in b) sb.Append(x.ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }
    }
}
