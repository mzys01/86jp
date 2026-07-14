using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Inventory;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.SelfTests
{
    internal static class ExperienceItemSelfTest
    {
        private const int AccountId = 946001;
        private const int BookCharacterId = 946101;
        private const int LargeCapsuleCharacterId = 946102;
        private const int ExpiredCharacterId = 946103;
        private const int RollbackCharacterId = 946104;

        private const int GrowthCapsuleItemId = 10147584;
        private const int LargeGrowthCapsuleItemId = 10002954;
        private const int PercentCapsuleItemId = 10094652;
        private const int StructuredPercentCapsuleItemId = 10006088;
        private const int StructuredBookItemId = 690001552;
        private const int DynamicCapsuleItemId = 10146833;
        private const int DuplicateExpirationItemId = 10003192;
        private const int TextOnlyRestrictionItemId = 2683665;
        private const int TimedCapsuleItemId = 10003998;
        private const int PriestCooldownCapsuleItemId = 10097954;
        private const int DuelExperienceBookItemId = 10093720;
        private const int OtherDuelExperienceBookItemId = 10093724;
        private const int LegacyBookItemId = 1034;
        private const short SourceSlot = 10;

        private static int _failures;

        internal static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== EXPERIENCE_ITEM selftest ===");

            try
            {
                CheckProtocol();
                CheckDefinitions();
                CheckPolicy();
                CheckCooldown();
                CheckPersistenceAndHandler();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ExperienceItemSelfTest FAILED: " + ex);
                return 1;
            }

            Console.WriteLine(_failures == 0
                ? "ExperienceItemSelfTest OK"
                : $"ExperienceItemSelfTest FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void CheckProtocol()
        {
            Check("0x001E parses exactly one non-negative i16 Main slot",
                IncreaseStatusRequest.TryParse(new byte[] { 0x41, 0x00 }, out var request)
                && request.SlotIndex == 65
                && !IncreaseStatusRequest.TryParse(new byte[] { 0x41 }, out _)
                && !IncreaseStatusRequest.TryParse(new byte[] { 0x41, 0x00, 0x00 }, out _)
                && !IncreaseStatusRequest.TryParse(new byte[] { 0xFF, 0xFF }, out _));

            Check("0x001E success ACK is the complete 12-byte no-status record",
                BitConverter.ToString(IncreaseStatusAckBuilder.BuildExperienceSuccess(0x1234))
                    == "01-34-12-FF-00-00-00-00-00-00-00-00");
            Check("0x001E failure ACK is result plus client error code",
                BitConverter.ToString(IncreaseStatusAckBuilder.BuildError(0x11)) == "00-11");
        }

        private static void CheckDefinitions()
        {
            var growth = ExperienceItemDataProvider.Resolve(GrowthCapsuleItemId);
            var large = ExperienceItemDataProvider.Resolve(LargeGrowthCapsuleItemId);
            var percent = ExperienceItemDataProvider.Resolve(PercentCapsuleItemId);
            var structuredPercent = ExperienceItemDataProvider.Resolve(StructuredPercentCapsuleItemId);
            var book = ExperienceItemDataProvider.Resolve(StructuredBookItemId);

            Check("growth capsule uses its structured fixed 1,000,000 EXP and level bounds",
                growth.IsSupported
                && growth.GrantKind == ExperienceItemGrantKind.Fixed
                && growth.Value == 1_000_000
                && growth.MinimumLevel == 50
                && growth.MaximumLevel == 85);
            Check("large growth capsule grants its own 300,000 EXP",
                large.IsSupported
                && large.GrantKind == ExperienceItemGrantKind.Fixed
                && large.Value == 300_000);
            Check("[need material] remains acquisition metadata",
                InventoryDbPrimitives.LoadStackableItem(LargeGrowthCapsuleItemId)
                    .NeedMaterial.Trim() == "10002939 50");
            Check("percentage capsule derives EXP from the current level segment",
                percent.IsSupported
                && percent.GrantKind == ExperienceItemGrantKind.Percent
                && percent.CalculateGain(25) == (uint)(
                    (ExpTableProvider.GetLevelThreshold(25)
                     - ExpTableProvider.GetLevelThreshold(24)) / 10));
            var percentageEffectKinds = new[]
                {
                    PercentCapsuleItemId,
                    StructuredPercentCapsuleItemId,
                }
                .SelectMany(itemId => InventoryDbPrimitives.LoadStackableItem(itemId).StatusIncreases)
                .Select(effect => effect.EffectType.ToLowerInvariant())
                .ToHashSet();
            Check("both structured percentage effect spellings use ordinary character EXP",
                structuredPercent.IsSupported
                && structuredPercent.GrantKind == ExperienceItemGrantKind.Percent
                && percentageEffectKinds.Contains("exppercentup")
                && percentageEffectKinds.Contains("expupbypercent"));
            Check("structured experience book uses the same ordinary EXP model",
                book.IsSupported
                && book.GrantKind == ExperienceItemGrantKind.Fixed
                && book.Value == 1_000);

            Check("duel EXP books never enter ordinary character EXP handling",
                !ExperienceItemDataProvider.Resolve(DuelExperienceBookItemId).IsExperienceLike
                && !ExperienceItemDataProvider.Resolve(OtherDuelExperienceBookItemId).IsExperienceLike);
            Check("metadata-only legacy books are not compatibility-mapped",
                !ExperienceItemDataProvider.Resolve(LegacyBookItemId).IsExperienceLike);
            var dynamic = ExperienceItemDataProvider.Resolve(DynamicCapsuleItemId);
            Check("dimension-crack EXP without a value is identified but rejected",
                dynamic.IsExperienceLike
                && !dynamic.IsSupported
                && dynamic.GrantKind == ExperienceItemGrantKind.CrackOfDimension);

            var mixed = StackableItemFile.Parse(@"
[stackable type]
`[etc]` 0
[increase status type]
`[expup]` 100
`[strength]` 1");
            var unknownAction = StackableItemFile.Parse(@"
[stackable type]
`[etc]` 0
[increase status type]
`[expup]` 100
[action type]
`[another behavior]`");
            Check("mixed or additional behavior definitions fail closed",
                !ExperienceItemDataProvider.Resolve(-1, mixed).IsSupported
                && !ExperienceItemDataProvider.Resolve(-2, unknownAction).IsSupported);

            var duplicateExpiration = ExperienceItemDataProvider.Resolve(DuplicateExpirationItemId);
            var textOnlyRestriction = ExperienceItemDataProvider.Resolve(TextOnlyRestrictionItemId);
            var timedCapsule = ExperienceItemDataProvider.Resolve(TimedCapsuleItemId);
            var priestCooldownCapsule = ExperienceItemDataProvider.Resolve(PriestCooldownCapsuleItemId);
            Check("real PVF restriction shapes remain modeled or fail closed",
                duplicateExpiration.IsExperienceLike
                && !duplicateExpiration.IsSupported
                && textOnlyRestriction.IsExperienceLike
                && !textOnlyRestriction.IsSupported
                && timedCapsule.IsSupported
                && timedCapsule.UsablePeriodDays == 2
                && priestCooldownCapsule.IsSupported
                && priestCooldownCapsule.CooldownMilliseconds == 1_000
                && priestCooldownCapsule.IsUsableByJob(4)
                && !priestCooldownCapsule.IsUsableByJob(0));

            var expectedExpiration = new DateTimeOffset(
                2026, 3, 11, 6, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
            Check("PVF expiration uses strict fixed UTC+8 parsing",
                PvfExpirationMetadata.TryParseUnixTime(
                    "2026-03-11 06:00:00",
                    out var parsedExpiration)
                && parsedExpiration == expectedExpiration
                && !PvfExpirationMetadata.TryParseUnixTime(
                    "2026-03-11 06:00:00 ignored",
                    out _)
                && !PvfExpirationMetadata.TryParseUnixTime(
                    "20260311 ignored",
                    out _));
        }

        private static void CheckPolicy()
        {
            var definition = CreateDefinition();
            var accepted = ExperienceItemUsePolicy.Evaluate(CreateContext(definition));
            Check("policy accepts an in-range fixed ordinary EXP item",
                accepted.Success
                && accepted.GrantedExp == 100
                && accepted.NewExp == 100
                && accepted.NewLevel == 10);

            var timed = CreateDefinition();
            timed.UsablePeriodDays = 1;
            var templateExpired = CreateDefinition();
            templateExpired.AbsoluteExpirationUnixTime = 1_000;
            var jobRestricted = CreateDefinition();
            jobRestricted.AllowedJobLabels.Add("mage");
            var townOnly = CreateDefinition();
            townOnly.TownOnly = true;
            var hardcoreBlocked = CreateDefinition();
            hardcoreBlocked.BlockedInHardcore = true;
            Check("policy enforces expiration, job, place, hardcore, and level boundaries",
                ExperienceItemUsePolicy.Evaluate(
                    CreateContext(definition, sourceExpireTime: 1_000, now: 1_000)).Status
                    == ExperienceItemUseStatus.Expired
                && ExperienceItemUsePolicy.Evaluate(
                    CreateContext(templateExpired, now: 1_000)).Status
                    == ExperienceItemUseStatus.Expired
                && ExperienceItemUsePolicy.Evaluate(CreateContext(timed)).Status
                    == ExperienceItemUseStatus.Expired
                && ExperienceItemUsePolicy.Evaluate(CreateContext(jobRestricted)).Status
                    == ExperienceItemUseStatus.JobRestricted
                && ExperienceItemUsePolicy.Evaluate(
                    CreateContext(townOnly, location: ExperienceItemUseLocation.Dungeon)).Status
                    == ExperienceItemUseStatus.LocationRestricted
                && ExperienceItemUsePolicy.Evaluate(
                    CreateContext(hardcoreBlocked, isHardcore: true)).Status
                    == ExperienceItemUseStatus.LevelRestricted
                && ExperienceItemUsePolicy.Evaluate(
                    CreateContext(definition, level: 9)).Status
                    == ExperienceItemUseStatus.LevelRestricted);

            var maxEntryLevel = (byte)(ExpTableProvider.MaxLevel - 1);
            var maxEntryExp = (uint)ExpTableProvider.GetLevelThreshold(maxEntryLevel);
            var crossingCap = CreateDefinition();
            crossingCap.MaximumLevel = maxEntryLevel;
            var capPlan = ExperienceItemUsePolicy.Evaluate(CreateContext(
                crossingCap,
                level: maxEntryLevel,
                exp: maxEntryExp - 10));
            Check("policy splits only max-level overflow into honor EXP",
                capPlan.Success
                && capPlan.NewLevel == ExpTableProvider.MaxLevel
                && capPlan.NewExp == maxEntryExp
                && capPlan.HonorExpGain == 90);
        }

        private static void CheckCooldown()
        {
            var firstDefinition = CreateDefinition(1);
            firstDefinition.CooldownMilliseconds = 20;
            firstDefinition.CooldownGroup = "shared";
            var secondDefinition = CreateDefinition(2);
            secondDefinition.CooldownMilliseconds = 20;
            secondDefinition.CooldownGroup = "shared";

            long timestamp = 0;
            var tracker = new ExperienceItemCooldownTracker(() => timestamp, 1_000);
            var firstAccepted = tracker.TryReserve(1, firstDefinition, out var first, out _);
            first.Commit();
            first.Dispose();
            var sharedBlocked = !tracker.TryReserve(
                1, secondDefinition, out _, out var remaining) && remaining > 0;
            timestamp = 21;
            var expiredAccepted = tracker.TryReserve(
                1, secondDefinition, out var afterExpiration, out _);
            afterExpiration.Dispose();
            Check("cooldown shares groups and expires from the committed monotonic time",
                firstAccepted && sharedBlocked && expiredAccepted);

            var concurrent = new ExperienceItemCooldownTracker();
            var concurrentDefinition = CreateDefinition(3);
            concurrentDefinition.CooldownMilliseconds = 1_000;
            var successfulReservations = 0;
            Parallel.For(0, 8, _ =>
            {
                if (!concurrent.TryReserve(
                        2, concurrentDefinition, out var reservation, out _))
                    return;
                Interlocked.Increment(ref successfulReservations);
                reservation.Commit();
                reservation.Dispose();
            });
            Check("cooldown permits exactly one concurrent reservation",
                successfulReservations == 1);

            var timestampReads = 0;
            var commitFailureTracker = new ExperienceItemCooldownTracker(
                () => ++timestampReads == 2
                    ? throw new InvalidOperationException("injected clock failure")
                    : 0,
                1_000);
            commitFailureTracker.TryReserve(
                3, concurrentDefinition, out var failedCommit, out _);
            var commitFailed = false;
            try
            {
                failedCommit.Commit();
            }
            catch (InvalidOperationException)
            {
                commitFailed = true;
            }
            failedCommit.Dispose();
            var retryAccepted = commitFailureTracker.TryReserve(
                3, concurrentDefinition, out var retry, out _);
            retry.Dispose();
            Check("cooldown commit failure releases the pending reservation",
                commitFailed && retryAccepted);
        }

        private static void CheckPersistenceAndHandler()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "experience_item_selftest_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Seed(connectionString);

                var timeProvider = new FixedRentalTimeProvider(1_700_000_000);
                var store = new SqliteInventoryStore(
                    databasePath,
                    ServerPaths.SchemaFilePath,
                    timeProvider);
                var cooldowns = new ExperienceItemCooldownTracker();
                var service = new ExperienceItemUseService(store, timeProvider, cooldowns);

                var bookBefore = LoadCharacterExp(connectionString, BookCharacterId);
                var bookResult = service.UseBySlot(
                    BookCharacterId,
                    AccountId,
                    InventoryListType.Main,
                    SourceSlot,
                    ExperienceItemUseLocation.Town);
                Check("timed special-backed stackable consumes exactly one ordinary EXP book",
                    bookResult.Success
                    && bookResult.GrantedExp == 1_000
                    && LoadCharacterExp(connectionString, BookCharacterId) == bookBefore + 1_000
                    && LoadStackCount(connectionString, BookCharacterId) == 2);
                Check("successful use creates one committed delete_item audit row",
                    LoadDeleteAuditCount(connectionString, BookCharacterId) == 1
                    && LoadDeleteAuditDelta(connectionString, BookCharacterId) == -1);
                Check("transaction result carries the four-field skill snapshot",
                    bookResult.SkillPoints.Page0Sp > 0
                    && bookResult.SkillPoints.Page1Sp == 0
                    && bookResult.SkillPoints.Page1Tp == 0);

                var largeBefore = LoadCharacterExp(connectionString, LargeCapsuleCharacterId);
                var largeResult = service.UseBySlot(
                    LargeCapsuleCharacterId,
                    AccountId,
                    InventoryListType.Main,
                    SourceSlot,
                    ExperienceItemUseLocation.Town);
                Check("large capsule succeeds without exchange material and consumes only itself",
                    largeResult.Success
                    && largeResult.GrantedExp == 300_000
                    && LoadCharacterExp(connectionString, LargeCapsuleCharacterId)
                        == largeBefore + 300_000
                    && LoadStackCount(connectionString, LargeCapsuleCharacterId) == 0
                    && LoadCharacterItemCount(
                        connectionString,
                        LargeCapsuleCharacterId,
                        10002939) == 0);

                var expiredBefore = LoadCharacterExp(connectionString, ExpiredCharacterId);
                var expiredResult = service.UseBySlot(
                    ExpiredCharacterId,
                    AccountId,
                    InventoryListType.Main,
                    SourceSlot,
                    ExperienceItemUseLocation.Town);
                Check("expired source is rejected without progress, consumption, or audit",
                    expiredResult.Status == ExperienceItemUseStatus.Expired
                    && LoadCharacterExp(connectionString, ExpiredCharacterId) == expiredBefore
                    && LoadStackCount(connectionString, ExpiredCharacterId) == 1
                    && LoadDeleteAuditCount(connectionString, ExpiredCharacterId) == 0);

                var rollbackBefore = LoadCharacterExp(connectionString, RollbackCharacterId);
                var rollbackResult = service.UseBySlot(
                    RollbackCharacterId,
                    AccountId,
                    InventoryListType.Main,
                    SourceSlot,
                    ExperienceItemUseLocation.Town);
                Check("skill persistence failure rolls back item, EXP, and audit atomically",
                    rollbackResult.Status == ExperienceItemUseStatus.PersistenceFailed
                    && LoadCharacterExp(connectionString, RollbackCharacterId) == rollbackBefore
                    && LoadStackCount(connectionString, RollbackCharacterId) == 1
                    && LoadDeleteAuditCount(connectionString, RollbackCharacterId) == 0);

                CheckHandlerSequence(
                    databasePath,
                    connectionString,
                    store,
                    timeProvider,
                    cooldowns,
                    bookResult.SkillPoints);

                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    SeedItem(connection, BookCharacterId, DuelExperienceBookItemId, 1);
                }
                var duelResult = service.UseBySlot(
                    BookCharacterId,
                    AccountId,
                    InventoryListType.Main,
                    SourceSlot,
                    ExperienceItemUseLocation.Town);
                Check("existing non-ordinary EXP books use the generic rejection instead of a missing-item error",
                    duelResult.Status == ExperienceItemUseStatus.UnsupportedDefinition
                    && InventoryHandler.GetExperienceItemFailureErrorCode(duelResult.Status) == 0x01
                    && LoadStackCount(connectionString, BookCharacterId) == 1);
            }
            finally
            {
                DeleteDatabase(databasePath);
            }
        }

        private static void CheckHandlerSequence(
            string databasePath,
            string connectionString,
            SqliteInventoryStore store,
            IRentalTimeProvider timeProvider,
            ExperienceItemCooldownTracker cooldowns,
            SkillPointProtocolState expectedSkillPoints)
        {
            var characterRepository = new SqliteCharacterRepository(
                databasePath,
                ServerPaths.SchemaFilePath);
            var assetService = new SqliteAssetService(
                databasePath,
                ServerPaths.SchemaFilePath,
                store);
            var dataSource = new SqliteSelectCharacterDataSource(
                databasePath,
                ServerPaths.SchemaFilePath,
                characterRepository,
                assetService,
                store,
                timeProvider);
            var refresh = new InventoryRefreshSender(
                store,
                dataSource,
                characterRepository);
            var handler = new InventoryHandler(
                store,
                new ExperienceItemUseService(store, timeProvider, cooldowns),
                dataSource,
                characterRepository,
                refresh,
                new ExperienceItemNotificationService(
                    characterRepository,
                    databasePath,
                    ServerPaths.SchemaFilePath));

            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            using (var client = new TcpClient())
            {
                listener.Start();
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                client.Connect(IPAddress.Loopback, endpoint.Port);
                using (var serverClient = listener.AcceptTcpClient())
                {
                    client.ReceiveTimeout = 3_000;
                    var session = new EnhancedClientSession(serverClient, new GamePacketHeader());
                    session.Account = new AccountRecord { AccountId = AccountId };
                    session.Player.HydrateFrom(characterRepository.GetById(BookCharacterId));

                    handler.Handle_ENUM_CMDPACKET_INCREASE_STATUS(
                            session,
                            new GamePacketHeader(),
                            BitConverter.GetBytes(SourceSlot))
                        .GetAwaiter()
                        .GetResult();

                    var stream = client.GetStream();
                    var ack = ReadPacket(stream);
                    var slotRefresh = ReadPacket(stream);
                    var exp = ReadPacket(stream);

                    Check("handler sends ACK -> slot refresh -> EXP in client-consumption order",
                        GetCommand(ack) == 0x01
                        && GetType(ack) == (ushort)CmdPacketType.INCREASE_STATUS
                        && ack.Length == 15 + IncreaseStatusAckBuilder.SuccessBodyLength
                        && GetCommand(slotRefresh) == 0x00
                        && GetType(slotRefresh) == 0x000E
                        && slotRefresh.Length == 15 + 87
                        && GetCommand(exp) == 0x00
                        && GetType(exp) == 0x0025
                        && exp.Length == 15 + ExpNotificationBuilder.BodyLength);
                    Check("handler success ACK carries the complete no-status record",
                        ack[15] == 0x01
                        && BitConverter.ToUInt16(ack, 16) == session.Player.UserId
                        && ack[18] == 0xFF
                        && ack.Skip(19).Take(8).All(value => value == 0));
                    Check("handler partial-stack refresh carries the remaining item count",
                        BitConverter.ToInt16(slotRefresh, 15 + 3) == SourceSlot
                        && BitConverter.ToInt32(slotRefresh, 15 + 5) == StructuredBookItemId
                        && BitConverter.ToInt32(slotRefresh, 15 + 9) == 1);
                    Check("handler 0x0025 carries level, EXP, and all four skill-point fields",
                        exp[15] == session.Player.Level
                        && BitConverter.ToUInt32(exp, 16) == session.Player.Exp
                        && BitConverter.ToUInt16(exp, 15 + 13) == expectedSkillPoints.Page0Sp
                        && BitConverter.ToUInt16(exp, 15 + 15) == expectedSkillPoints.Page1Sp
                        && BitConverter.ToUInt16(exp, 15 + 17) == expectedSkillPoints.Page0Tp
                        && BitConverter.ToUInt16(exp, 15 + 19) == expectedSkillPoints.Page1Tp);
                    Check("handler 0x0025 keeps PvP points and compatibility tail zero",
                        BitConverter.ToUInt32(
                            exp,
                            15 + ExpNotificationBuilder.PvpVictoryPointOffset) == 0
                        && exp.Skip(15 + ExpNotificationBuilder.ClientReadLengthWithNoVariableEntries)
                            .Take(8)
                            .All(value => value == 0));

                    handler.Handle_ENUM_CMDPACKET_INCREASE_STATUS(
                            session,
                            new GamePacketHeader(),
                            BitConverter.GetBytes(SourceSlot))
                        .GetAwaiter()
                        .GetResult();
                    var finalAck = ReadPacket(stream);
                    var finalSlotRefresh = ReadPacket(stream);
                    var finalExp = ReadPacket(stream);
                    Check("handler final-stack use keeps the same three-packet order",
                        GetType(finalAck) == (ushort)CmdPacketType.INCREASE_STATUS
                        && GetType(finalSlotRefresh) == 0x000E
                        && GetType(finalExp) == 0x0025);
                    Check("handler final-stack refresh clears the source slot immediately",
                        BitConverter.ToInt16(finalSlotRefresh, 15 + 3) == SourceSlot
                        && BitConverter.ToInt32(finalSlotRefresh, 15 + 5) == -1);
                    Check("handler commits exactly one audit row per successful use",
                        LoadStackCount(connectionString, BookCharacterId) == 0
                        && LoadDeleteAuditCount(connectionString, BookCharacterId) == 3
                        && LoadDeleteAuditDelta(connectionString, BookCharacterId) == -3);

                    session.Close();
                }
            }
        }

        private static void Seed(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                Execute(connection,
                    "INSERT INTO accounts(account_id,m_id,growth_capsule_exp) VALUES(@id,@name,12345);",
                    "@id", AccountId,
                    "@name", "experience_item_selftest");

                var level20Exp = (uint)ExpTableProvider.GetLevelThreshold(19);
                var level50Exp = (uint)ExpTableProvider.GetLevelThreshold(49);
                SeedCharacter(connection, BookCharacterId, "experience_book", 20, level20Exp, 789);
                SeedCharacter(connection, LargeCapsuleCharacterId, "experience_large", 50, level50Exp);
                SeedCharacter(connection, ExpiredCharacterId, "experience_expired", 50, level50Exp);
                SeedCharacter(
                    connection,
                    RollbackCharacterId,
                    "experience_rollback",
                    50,
                    (uint)ExpTableProvider.GetLevelThreshold(50) - 1);

                SeedItem(
                    connection,
                    BookCharacterId,
                    StructuredBookItemId,
                    3,
                    "special",
                    int.MaxValue);
                SeedItem(connection, LargeCapsuleCharacterId, LargeGrowthCapsuleItemId, 1);
                SeedItem(connection, ExpiredCharacterId, GrowthCapsuleItemId, 1, expireTime: 1);
                SeedItem(connection, RollbackCharacterId, GrowthCapsuleItemId, 1);

                Execute(connection, $@"
CREATE TRIGGER reject_experience_item_skill_persistence
BEFORE INSERT ON character_skill_points
WHEN NEW.character_id = {RollbackCharacterId}
BEGIN
    SELECT RAISE(ABORT, 'experience-item rollback test');
END;");
            }
        }

        private static void SeedCharacter(
            SqliteConnection connection,
            int characterId,
            string name,
            int level,
            uint exp,
            int bonusSp = 0)
        {
            Execute(connection, @"
INSERT INTO characters(character_id,account_id,name,job,grow_type,level,exp,bonus_sp)
VALUES(@cid,@aid,@name,0,0,@level,@exp,@bonusSp);",
                "@cid", characterId,
                "@aid", AccountId,
                "@name", name,
                "@level", level,
                "@exp", (long)exp,
                "@bonusSp", bonusSp);
            Execute(
                connection,
                "INSERT INTO character_subtype1_fields(character_id) VALUES(@cid);",
                "@cid", characterId);
        }

        private static void SeedItem(
            SqliteConnection connection,
            int characterId,
            int itemTemplateId,
            int stackCount,
            string itemKind = "stackable",
            int expireTime = 0)
        {
            Execute(connection, @"
INSERT INTO character_items(
    owner_scope,owner_id,character_id,list_type,slot_index,item_template_id,
    item_kind,stack_count,instance_value,expire_time)
VALUES('character',@cid,@cid,0,@slot,@item,@kind,@count,@count,@expire);",
                "@cid", characterId,
                "@slot", SourceSlot,
                "@item", itemTemplateId,
                "@kind", itemKind,
                "@count", stackCount,
                "@expire", expireTime);
        }

        private static ExperienceItemDefinition CreateDefinition(int itemTemplateId = 1)
            => new ExperienceItemDefinition(itemTemplateId)
            {
                GrantKind = ExperienceItemGrantKind.Fixed,
                Value = 100,
                MinimumLevel = 10,
                MaximumLevel = 20,
                IsExperienceLike = true,
                IsSupported = true,
            };

        private static ExperienceItemUseContext CreateContext(
            ExperienceItemDefinition definition,
            byte level = 10,
            uint exp = 0,
            int sourceExpireTime = 0,
            uint now = 999,
            bool isHardcore = false,
            ExperienceItemUseLocation location = ExperienceItemUseLocation.Town)
            => new ExperienceItemUseContext
            {
                Definition = definition,
                SourceExpireTime = sourceExpireTime,
                NowUnixTime = now,
                Job = 0,
                Level = level,
                Exp = exp,
                IsHardcore = isHardcore,
                Location = location,
            };

        private static byte[] ReadPacket(NetworkStream stream)
        {
            var header = ReadExactly(stream, 15);
            var packetLength = BitConverter.ToInt32(header, 3);
            if (packetLength < 15)
                throw new InvalidDataException($"invalid packet length {packetLength}");
            var body = ReadExactly(stream, packetLength - 15);
            return header.Concat(body).ToArray();
        }

        private static byte[] ReadExactly(NetworkStream stream, int length)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(buffer, offset, length - offset);
                if (read <= 0)
                    throw new EndOfStreamException();
                offset += read;
            }
            return buffer;
        }

        private static byte GetCommand(byte[] packet) => packet[0];
        private static ushort GetType(byte[] packet) => BitConverter.ToUInt16(packet, 1);

        private static uint LoadCharacterExp(string connectionString, int characterId)
            => (uint)LoadScalar(
                connectionString,
                "SELECT exp FROM characters WHERE character_id=@id;",
                "@id", characterId);

        private static int LoadStackCount(string connectionString, int characterId)
            => (int)LoadScalar(
                connectionString,
                @"SELECT COALESCE(MAX(stack_count),0) FROM character_items
WHERE character_id=@id AND list_type=0 AND slot_index=@slot;",
                "@id", characterId,
                "@slot", SourceSlot);

        private static int LoadCharacterItemCount(
            string connectionString,
            int characterId,
            int itemTemplateId)
            => (int)LoadScalar(
                connectionString,
                @"SELECT COALESCE(SUM(stack_count),0) FROM character_items
WHERE character_id=@id AND item_template_id=@item;",
                "@id", characterId,
                "@item", itemTemplateId);

        private static int LoadDeleteAuditCount(string connectionString, int characterId)
            => (int)LoadScalar(
                connectionString,
                @"SELECT COUNT(*) FROM item_audit_log
WHERE character_id=@id AND action_name='delete_item';",
                "@id", characterId);

        private static int LoadDeleteAuditDelta(string connectionString, int characterId)
            => (int)LoadScalar(
                connectionString,
                @"SELECT COALESCE(SUM(delta_stack_count),0) FROM item_audit_log
WHERE character_id=@id AND action_name='delete_item';",
                "@id", characterId);

        private static long LoadScalar(
            string connectionString,
            string sql,
            params object[] parameters)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    AddParameters(command, parameters);
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
        }

        private static void Execute(
            SqliteConnection connection,
            string sql,
            params object[] parameters)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                AddParameters(command, parameters);
                command.ExecuteNonQuery();
            }
        }

        private static void AddParameters(SqliteCommand command, object[] parameters)
        {
            for (var i = 0; i + 1 < parameters.Length; i += 2)
                command.Parameters.AddWithValue((string)parameters[i], parameters[i + 1]);
        }

        private static void Check(string name, bool success)
        {
            Console.WriteLine($"  [{(success ? "PASS" : "FAIL")}] {name}");
            if (!success)
                _failures++;
        }

        private static void DeleteDatabase(string databasePath)
        {
            foreach (var path in new[]
                     {
                         databasePath,
                         databasePath + "-wal",
                         databasePath + "-shm",
                     })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private sealed class FixedRentalTimeProvider : IRentalTimeProvider
        {
            private readonly uint _now;

            internal FixedRentalTimeProvider(uint now)
            {
                _now = now;
            }

            public uint UtcNowUnixSeconds() => _now;
        }
    }
}
