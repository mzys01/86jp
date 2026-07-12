using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DfoServer.Game.Accounts;
using DfoServer.Game.DailyReset;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Inventory;
using DfoServer.Game.ReviveCoin;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    public static class DeathTowerEntrySelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DEATH_TOWER_ENTRY selftest ===");
            var failures = 0;

            var config = new DeathTowerData.TowerConfig
            {
                DungeonId = 11000,
                TotalStages = 3,
                StageMapIds = new[] { 33060, 33061, 33062 },
                BasisLevel = 50,
                MaxClearItemCount = 10,
            };

            var tower = new DeathTowerSession(config);
            Check("tower session starts on the first configured floor",
                tower.CurrentStage == 0
                && tower.State == 0
                && tower.GetCurrentMapId() == 33060,
                ref failures);

            var liveEntryCreated = new DeathTowerHandler()
                .TryCreateSession(11000, out var liveEntryTower);
            var liveConfig = DeathTowerData.GetConfig(11000);
            Check("handler-created death tower session starts on the first configured floor",
                liveEntryCreated
                && liveConfig != null
                && liveEntryTower.CurrentStage == 0
                && liveEntryTower.GetCurrentMapId() == liveConfig.StageMapIds[0],
                ref failures);

            var towerInfo = DeathTowerPacketBuilder.BuildTowerInfo(11000, 3);
            Check("0x008E body remains 8 bytes",
                towerInfo.Length == 8,
                ref failures);
            Check("0x008E encodes dungeon and stage count",
                BitConverter.ToUInt32(towerInfo, 0) == 11000
                && BitConverter.ToUInt16(towerInfo, 4) == 3,
                ref failures);
            Check("0x008E normal tower mode tail is 00 0B",
                towerInfo[6] == 0
                && towerInfo[7] == DeathTowerPacketBuilder.ObservedRandomBuffType
                && towerInfo[7] == 11,
                ref failures);

            var rewardConfig = DeathTowerRewardConfig.Load();
            Check("death tower PVF reward config exposes floor-45 weights and item cap inputs",
                rewardConfig != null
                && Math.Abs(rewardConfig.GetExpWeight(45) - 8.413f) < 0.0001f
                && rewardConfig.GetRewardCardCount(45) == 11
                && Math.Abs(rewardConfig.GoldWeight - 11f) < 0.0001f
                && rewardConfig.NormalItemWeight == 50
                && rewardConfig.MagicItemWeight == 49
                && rewardConfig.ItemWeightTotal == 100,
                ref failures);
            var unavailableRewardConfig = DeathTowerRewardConfig.Parse(string.Empty);
            Check("missing death tower reward PVF fails closed",
                unavailableRewardConfig.GoldWeight == 0
                && unavailableRewardConfig.GetExpWeight(45) == 0
                && unavailableRewardConfig.GetRewardCardCount(45) == 0
                && unavailableRewardConfig.ItemWeightTotal == 0,
                ref failures);

            var rewardBody = DeathTowerPacketBuilder.BuildReward(
                0,
                new[]
                {
                    (IReadOnlyList<DeathTowerRewardItem>)new[]
                    {
                        new DeathTowerRewardItem(10089420, 2),
                        new DeathTowerRewardItem(6515, 1),
                    },
                    Array.Empty<DeathTowerRewardItem>(),
                    Array.Empty<DeathTowerRewardItem>(),
                    Array.Empty<DeathTowerRewardItem>(),
                });
            Check("0x0091 non-empty reward body is u32 plus four count/item groups",
                rewardBody.Length == 24
                && BitConverter.ToUInt32(rewardBody, 0) == 0
                && rewardBody[4] == 2
                && BitConverter.ToUInt32(rewardBody, 5) == 10089420
                && BitConverter.ToUInt32(rewardBody, 9) == 2
                && BitConverter.ToUInt32(rewardBody, 13) == 6515
                && BitConverter.ToUInt32(rewardBody, 17) == 1
                && rewardBody[21] == 0
                && rewardBody[22] == 0
                && rewardBody[23] == 0,
                ref failures);
            using (var fixture = SelectDungeonFixture.Create())
            {
                var handler = fixture.CreateDungeonHandler();
                handler
                    .Handle_ENUM_CMDPACKET_SELECT_DUNGEON(
                        fixture.Session,
                        new GamePacketHeader(),
                        BuildSelectDungeonBody(11000, difficulty: 2))
                    .GetAwaiter()
                    .GetResult();

                var sentTypes = fixture.ReadSentTypes(expectedPackets: 3);
                Check("select-dungeon tower creates CurrentRun payload",
                    fixture.Session.Player.CurrentRun != null
                    && fixture.Session.Player.CurrentRun.DungeonId == 11000
                    && fixture.Session.Player.CurrentRun.Tower != null,
                    ref failures);
                Check("tower stage monsters are available to the combat/experience pipeline",
                    fixture.Session.Player.CurrentRun.RoomMonsters.Count > 0
                    && fixture.Session.Player.CurrentRun.RoomStartSequence > 0,
                    ref failures);
                Check("select-dungeon tower packet order starts with 0x008E then tower packets",
                    sentTypes.Count >= 3
                    && sentTypes[0] == 0x008E
                    && sentTypes[1] == 0x008F
                    && sentTypes[2] == 0x001E,
                    ref failures);
                Check("tower guaranteed drop completes DIE_MONSTER with one authoritative stage LCG",
                    TowerGuaranteedDropCompletesCombatHandler(fixture, handler),
                    ref failures);
            }

            using (var fixture = SelectDungeonFixture.Create())
            {
                var rollbackTower = CreateFinalFloorTower(config);
                var previousExp = fixture.Session.Player.Exp;
                var previousGold = fixture.LoadGold();
                var previousItems = fixture.CountPersistentMainItems();
                var failed = false;
                try
                {
                    new DeathTowerSettlementService(
                            fixture.AssetService,
                            (scope, characterId, level, exp) => false)
                        .Grant(fixture.Session, rollbackTower);
                }
                catch (InvalidOperationException)
                {
                    failed = true;
                }

                Check("tower settlement rolls back gold, items and memory exp when progress write fails",
                    failed
                    && fixture.Session.Player.Exp == previousExp
                    && fixture.LoadGold() == previousGold
                    && fixture.CountPersistentMainItems() == previousItems,
                    ref failures);
                Check("failed settlement gate can be explicitly reopened",
                    rollbackTower.TryBeginSettlement()
                    && (AbortAndRetrySettlement(rollbackTower)),
                    ref failures);
            }

            using (var fixture = SelectDungeonFixture.Create())
            {
                var handler = fixture.CreateDungeonHandler();
                var settlementTower = CreateFinalFloorTower(config);

                DungeonRunLifecycle.BeginTowerRun(fixture.Session, config.DungeonId, settlementTower);
                settlementTower.SetFighting();
                var previousExp = fixture.Session.Player.Exp;
                var previousGold = fixture.LoadGold();
                handler
                    .Handle_ENUM_CMDPACKET_DEATH_TOWER_STAGE_CMD(
                        fixture.Session,
                        new GamePacketHeader(),
                        new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();

                var sentPackets = fixture.ReadSentPackets(expectedPackets: 3);
                Check("tower settlement sends ranking, non-empty reward and EPLP packets first",
                    sentPackets.Count == 3
                    && sentPackets[0].Type == 0x0090
                    && sentPackets[1].Type == 0x0091
                    && sentPackets[1].Body.Length >= 16
                    && sentPackets[1].Body[4] > 0
                    && sentPackets[2].Type == 0x0092,
                    ref failures);
                Check("tower settlement persists PVF-scaled exp, gold and reward items",
                    fixture.Session.Player.Exp > previousExp
                    && fixture.LoadGold() > previousGold
                    && fixture.CountPersistentMainItems() > 0,
                    ref failures);

                var settledExp = fixture.Session.Player.Exp;
                var settledGold = fixture.LoadGold();
                var settledItems = fixture.CountPersistentMainItems();
                handler
                    .Handle_ENUM_CMDPACKET_DEATH_TOWER_STAGE_CMD(
                        fixture.Session,
                        new GamePacketHeader(),
                        new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();
                Check("duplicate final-floor stage command cannot grant settlement twice",
                    fixture.Session.Player.Exp == settledExp
                    && fixture.LoadGold() == settledGold
                    && fixture.CountPersistentMainItems() == settledItems,
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static DeathTowerSession CreateFinalFloorTower(DeathTowerData.TowerConfig config)
        {
            var tower = new DeathTowerSession(config);
            while (tower.CurrentStage < tower.EndStage)
            {
                tower.SetFighting();
                if (!tower.TryAdvanceStage())
                    throw new InvalidOperationException("Unable to advance settlement test to final floor.");
            }
            return tower;
        }

        private static bool AbortAndRetrySettlement(DeathTowerSession tower)
        {
            tower.AbortSettlement();
            return tower.TryBeginSettlement();
        }

        private static byte[] BuildSelectDungeonBody(ushort dungeonId, byte difficulty)
        {
            var body = new byte[5];
            BitConverter.GetBytes(dungeonId).CopyTo(body, 0);
            body[2] = difficulty;
            return body;
        }

        private static bool TowerGuaranteedDropCompletesCombatHandler(
            SelectDungeonFixture fixture,
            DungeonHandler handler)
        {
            var run = fixture.Session.Player.CurrentRun;
            if (run?.Tower == null || run.RoomMonsters.Count == 0 || run.RoomStartSequence == 0)
                return false;

            var monster = run.RoomMonsters[0];
            var monsterUniqueId = run.RoomStartSequence;
            monster.Code = 10504;
            monster.Type = 5;
            run.Tower.BeginStage(0x12345678, new[]
            {
                new StageTowerItem
                {
                    SourceListIndex = monster.TemplateOrder,
                    SourceMonsterUniqueId = monsterUniqueId,
                    ItemUniqueId = 51,
                    ItemId = 6515,
                    DropRate = 10000,
                    StackCount = 1,
                },
            });

            var syncCombatStage = typeof(DeathTowerHandler).GetMethod(
                "SyncCombatStage",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (syncCombatStage == null)
                return false;

            syncCombatStage.Invoke(null, new object[]
            {
                fixture.Session,
                run.Tower,
                new List<StageMonster>
                {
                    new StageMonster
                    {
                        ListIndex = monster.TemplateOrder,
                        MonsterUniqueId = monsterUniqueId,
                        MonsterIndex = monster.Code,
                        MonsterLevel = monster.Level,
                        MonsterType = monster.Type,
                        IsBoxMonster = monster.IsBlocking ? (byte)0 : (byte)1,
                    },
                },
            });

            try
            {
                var body = new byte[4];
                BitConverter.GetBytes(monsterUniqueId).CopyTo(body, 0);
                BitConverter.GetBytes((ushort)fixture.Session.Player.UserId).CopyTo(body, 2);
                handler.Handle_ENUM_CMDPACKET_DIE_MONSTER(
                        fixture.Session,
                        new GamePacketHeader(),
                        body)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] tower DIE_MONSTER threw: {ex.GetBaseException().Message}");
                return false;
            }

            var stageLcg = typeof(DeathTowerSession).GetProperty(
                "StageLcg",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(run.Tower);
            return stageLcg != null
                && ReferenceEquals(run.RoomLcg, stageLcg)
                && run.Tower.GroundItems.Count == 1
                && fixture.CountPersistentItem(10089420) == 1;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private sealed class SelectDungeonFixture : IDisposable
        {
            private const int CharacterId = 484101;
            private const int AccountId = 484101;

            private readonly TcpListener _listener;
            private readonly TcpClient _client;
            private readonly TcpClient _accepted;
            private readonly string _dbPath;
            private readonly SqliteInventoryStore _inventoryStore;
            private readonly SqliteAssetService _assetService;
            private readonly DailyResetService _dailyReset;

            public EnhancedClientSession Session { get; }
            public IAssetService AssetService => _assetService;

            private SelectDungeonFixture(
                TcpListener listener,
                TcpClient client,
                TcpClient accepted,
                EnhancedClientSession session,
                string dbPath,
                SqliteInventoryStore inventoryStore,
                SqliteAssetService assetService,
                DailyResetService dailyReset)
            {
                _listener = listener;
                _client = client;
                _accepted = accepted;
                Session = session;
                _dbPath = dbPath;
                _inventoryStore = inventoryStore;
                _assetService = assetService;
                _dailyReset = dailyReset;
            }

            public static SelectDungeonFixture Create()
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
                Directory.CreateDirectory(tempDir);
                var dbPath = Path.Combine(
                    tempDir,
                    $"death-tower-entry-{Guid.NewGuid():N}.db");

                SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
                SeedAccountAndCharacter(dbPath);
                Game.Quests.QuestService.SaveActiveQuests(
                    SqliteDatabaseBootstrap.BuildConnectionString(dbPath),
                    CharacterId,
                    new List<Game.Quests.ActiveQuest>
                    {
                        new Game.Quests.ActiveQuest
                        {
                            Slot = 0,
                            QuestId = 932,
                            TriggerValue = 10,
                        },
                    });

                var inventoryStore = new SqliteInventoryStore(dbPath, ServerPaths.SchemaFilePath);
                var assetService = new SqliteAssetService(dbPath, ServerPaths.SchemaFilePath, inventoryStore);
                var dailyReset = new DailyResetService(dbPath, ServerPaths.SchemaFilePath);

                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var client = new TcpClient();
                var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
                var accepted = listener.AcceptTcpClient();
                connectTask.GetAwaiter().GetResult();

                var session = new EnhancedClientSession(accepted, new GamePacketHeader());
                session.Player.CharacterId = CharacterId;
                session.Player.UserId = 1;
                session.Player.Level = 50;
                session.Player.Job = 4;
                session.Player.GrowType = 4;
                session.Account = new AccountRecord
                {
                    AccountId = AccountId,
                    MId = "death-tower-entry",
                };

                return new SelectDungeonFixture(
                    listener,
                    client,
                    accepted,
                    session,
                    dbPath,
                    inventoryStore,
                    assetService,
                    dailyReset);
            }

            public DungeonHandler CreateDungeonHandler()
            {
                var characterRepository = new Game.Characters.SqliteCharacterRepository(_dbPath, ServerPaths.SchemaFilePath);
                var selectCharacterDataSource = new Game.SelectCharacter.SqliteSelectCharacterDataSource(
                    _dbPath,
                    ServerPaths.SchemaFilePath,
                    characterRepository,
                    _assetService,
                    _inventoryStore,
                    SystemRentalTimeProvider.Instance);
                var reviveCoin = new ReviveCoinService(_inventoryStore, _assetService, _dailyReset);

                var inventoryRefresh = new Network.Handlers.InventoryRefreshSender(
                    _inventoryStore, selectCharacterDataSource, characterRepository);
                var questDropService = new Game.Quests.QuestDropService(
                    _assetService,
                    inventoryRefresh,
                    SqliteDatabaseBootstrap.BuildConnectionString(_dbPath),
                    (candidate, held) => 1);
                return new DungeonHandler(
                    _assetService,
                    reviveCoin,
                    characterRepository,
                    selectCharacterDataSource,
                    SystemRentalTimeProvider.Instance,
                    _inventoryStore,
                    inventoryRefresh,
                    questDropService: questDropService);
            }

            public int CountPersistentItem(int itemId)
            {
                using (var scope = _assetService.OpenScope(CharacterId, AccountId))
                    return _assetService.CountItem(scope, itemId);
            }

            public int LoadGold()
            {
                using (var scope = _assetService.OpenScope(CharacterId, AccountId))
                    return _assetService.LoadWallet(scope).Gold;
            }

            public int CountPersistentMainItems()
            {
                using (var scope = _assetService.OpenScope(CharacterId, AccountId))
                    return _assetService.LoadSnapshot(scope).MainItems.Count;
            }

            public List<ushort> ReadSentTypes(int expectedPackets)
            {
                var result = new List<ushort>();
                foreach (var packet in ReadSentPackets(expectedPackets))
                    result.Add(packet.Type);
                return result;
            }

            public List<CapturedPacket> ReadSentPackets(int expectedPackets)
            {
                var result = new List<CapturedPacket>();
                _client.ReceiveTimeout = 2000;

                for (var i = 0; i < expectedPackets; i++)
                {
                    var header = ReadExact(15);
                    var type = BitConverter.ToUInt16(header, 1);
                    var packetLength = BitConverter.ToInt32(header, 3);
                    var bodyLength = packetLength - 15;
                    var body = bodyLength > 0 ? ReadExact(bodyLength) : Array.Empty<byte>();
                    result.Add(new CapturedPacket(type, body));
                }

                return result;
            }

            public readonly struct CapturedPacket
            {
                public CapturedPacket(ushort type, byte[] body)
                {
                    Type = type;
                    Body = body;
                }

                public ushort Type { get; }
                public byte[] Body { get; }
            }

            private byte[] ReadExact(int count)
            {
                var buffer = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    var read = _client.GetStream().Read(buffer, offset, count - offset);
                    if (read <= 0)
                        throw new EndOfStreamException();
                    offset += read;
                }

                return buffer;
            }

            private static void SeedAccountAndCharacter(string dbPath)
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(dbPath, ServerPaths.SchemaFilePath);
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');
INSERT OR IGNORE INTO characters (character_id, account_id, name, job, grow_type, level)
VALUES (@cid, @aid, @name, 4, 4, 50);
INSERT OR IGNORE INTO character_subtype1_fields (character_id)
VALUES (@cid);";
                        cmd.Parameters.AddWithValue("@aid", AccountId);
                        cmd.Parameters.AddWithValue("@cid", CharacterId);
                        cmd.Parameters.AddWithValue("@mid", "death-tower-entry");
                        cmd.Parameters.AddWithValue("@name", "death-tower-entry");
                        cmd.ExecuteNonQuery();
                    }
                }
            }

            public void Dispose()
            {
                _accepted.Dispose();
                _client.Dispose();
                _listener.Stop();
            }
        }
    }
}
