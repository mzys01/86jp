using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
            };

            var tower = new DeathTowerSession(config);
            Check("tower session starts on the first configured floor",
                tower.CurrentStage == 0
                && tower.State == 0
                && tower.GetCurrentMapId() == 33060,
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
                Check("select-dungeon tower packet order starts with 0x008E then tower packets",
                    sentTypes.Count >= 3
                    && sentTypes[0] == 0x008E
                    && sentTypes[1] == 0x008F
                    && sentTypes[2] == 0x001E,
                    ref failures);
            }

            using (var fixture = SelectDungeonFixture.Create())
            {
                var settlementTower = new DeathTowerSession(config);
                while (settlementTower.CurrentStage < settlementTower.EndStage)
                {
                    settlementTower.SetFighting();
                    if (!settlementTower.TryAdvanceStage())
                        throw new InvalidOperationException("Unable to advance settlement test to final floor.");
                }

                DungeonRunLifecycle.BeginTowerRun(fixture.Session, config.DungeonId, settlementTower);
                settlementTower.SetFighting();
                new DeathTowerHandler()
                    .HandleStageCommand(fixture.Session, new GamePacketHeader(), new byte[] { 2 })
                    .GetAwaiter()
                    .GetResult();

                var sentTypes = fixture.ReadSentTypes(expectedPackets: 3);
                Check("tower settlement sends only ranking reward and EPLP packets",
                    sentTypes.Count == 3
                    && sentTypes[0] == 0x0090
                    && sentTypes[1] == 0x0091
                    && sentTypes[2] == 0x0092,
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildSelectDungeonBody(ushort dungeonId, byte difficulty)
        {
            var body = new byte[5];
            BitConverter.GetBytes(dungeonId).CopyTo(body, 0);
            body[2] = difficulty;
            return body;
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
                return new DungeonHandler(
                    _assetService,
                    reviveCoin,
                    characterRepository,
                    selectCharacterDataSource,
                    SystemRentalTimeProvider.Instance,
                    inventoryRefresh);
            }

            public List<ushort> ReadSentTypes(int expectedPackets)
            {
                var result = new List<ushort>();
                _client.ReceiveTimeout = 2000;

                for (var i = 0; i < expectedPackets; i++)
                {
                    var header = ReadExact(15);
                    result.Add(BitConverter.ToUInt16(header, 1));
                    var packetLength = BitConverter.ToInt32(header, 3);
                    var bodyLength = packetLength - 15;
                    if (bodyLength > 0)
                        ReadExact(bodyLength);
                }

                return result;
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
VALUES (@cid, @aid, @name, 4, 4, 50);";
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
