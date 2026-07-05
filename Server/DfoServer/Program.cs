using DfoServer.Network;
using System;
using System.Collections.Generic;
using System.Threading;

namespace DfoServer
{
    internal class Program
    {
        static void Main(string[] args)
        {
            args ??= Array.Empty<string>();

            if (Array.IndexOf(args, "--selftest-buyskill") >= 0)
            {
                Environment.Exit(Game.Skills.BuySkillSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-avatar-package") >= 0)
            {
                Environment.Exit(SelfTests.AvatarPackageSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-cerashop") >= 0)
            {
                Environment.Exit(SelfTests.CeraShopSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-selectable-package") >= 0)
            {
                Environment.Exit(SelfTests.SelectablePackageSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-pet-consumable") >= 0)
            {
                Environment.Exit(SelfTests.PetConsumableSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-inventory-sale") >= 0)
            {
                Environment.Exit(SelfTests.InventorySaleSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-personal-cargo") >= 0)
            {
                Environment.Exit(SelfTests.PersonalCargoSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-dungeon-map-fallback") >= 0)
            {
                Environment.Exit(SelfTests.DungeonMapFallbackSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-dungeon-room-progress") >= 0)
            {
                Environment.Exit(SelfTests.DungeonRoomProgressSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-character-option") >= 0)
            {
                Environment.Exit(SelfTests.CharacterOptionSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-crystal-contract") >= 0)
            {
                Environment.Exit(SelfTests.CrystalContractSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-slot-expansion-quest") >= 0)
            {
                Environment.Exit(SelfTests.SlotExpansionQuestSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-character-slot-policy") >= 0)
            {
                Environment.Exit(SelfTests.CharacterSlotPolicySelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-clear-map-quest") >= 0)
            {
                Environment.Exit(SelfTests.ClearMapQuestSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-quest-clear") >= 0)
            {
                Environment.Exit(SelfTests.QuestClearSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-quest-trigger-counts") >= 0)
            {
                Environment.Exit(SelfTests.QuestTriggerCountSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-quest-item-flow") >= 0)
            {
                Environment.Exit(SelfTests.QuestItemFlowSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-question-quest-branch") >= 0)
            {
                Environment.Exit(SelfTests.QuestionQuestBranchSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-striker-skill") >= 0)
            {
                Environment.Exit(SelfTests.StrikerSkillSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-pet-equipment") >= 0)
            {
                Environment.Exit(SelfTests.PetEquipmentSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-pet-hatch") >= 0)
            {
                Environment.Exit(SelfTests.PetHatchSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-currency") >= 0)
            {
                Environment.Exit(SelfTests.CurrencySelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-daily-reset") >= 0)
            {
                Environment.Exit(SelfTests.DailyResetSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-revive-coin") >= 0)
            {
                Environment.Exit(SelfTests.ReviveCoinSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-clock") >= 0)
            {
                Environment.Exit(SelfTests.ClockSelfTest.Run());
                return;
            }

            GameNetworkConfig.Configure(args);

            PacketFileLogger.Initialize();
            if (GameNetworkConfig.PacketCaptureEnabled)
                Console.WriteLine("[PacketCapture] ENABLED – all SEND/RECV packets logged to packet_log.txt");

            try
            {
                _ = GameWorld.GameWorldConfig.PvfArchivePath;
            }
            catch (System.IO.FileNotFoundException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: Script.pvf not found.");
                Console.WriteLine("Please place Script.pvf in Data/Pvf/Script.pvf, or set the PVF_ARCHIVE_PATH environment variable.");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            Console.Write("Loading Script.pvf... ");
            try
            {
                GameWorld.PvfArchiveAccessor.ReadText("character/character.lst");
                Game.Mercenary.StrikerSkillDataProvider.Warmup();
                Console.WriteLine("OK");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAILED");
                Console.WriteLine($"Error: Failed to load Script.pvf: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(1);
                return;
            }

            // 启动时一次性按当前等级重算所有角色战斗属性, 修复历史"升级未重算属性"的存量数据。
            // 必须在 PVF 加载后: 属性表来自 Script.pvf。幂等, 重复执行结果一致, 正常时静默, 仅出错时提示。
            try
            {
                new Game.CharacterData.SqliteSubtype1Repository(
                    Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath)
                    .RecomputeAllCombatStats();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] combat stats recompute skipped: {ex.Message}");
            }

            var server = new MultiStructureTcpServer();

            int channelPort = GameNetworkConfig.ProxyMode ? 7002 : 7001;
            int gamePort = GameNetworkConfig.ProxyMode ? 10012 : 10011;

            var portConfigs = new Dictionary<int, (IProtocolHandler handler, IPacketHeader structure)>
            {
                { channelPort, (new ChannelProtocolHandler(), new ChannelPacketHeader()) },
                { gamePort, (new GameProtocolHandler(packet => server.BroadcastToPortAsync(gamePort, packet)), new GamePacketHeader()) }
            };

            server.Start(portConfigs);

            Infrastructure.ClockService.Instance.Start();

            if (GameNetworkConfig.ProxyMode)
                Console.WriteLine($"[ProxyMode] Server listening on {channelPort}(channel) / {gamePort}(game) – PvfProxy forwards 7001/10011 to these ports.");

            Console.WriteLine("Multi-structure TCP server started!");
            Console.WriteLine($"Advertised server IP: {GameNetworkConfig.ServerIp} (ports 7001 channel, 10011 game)");
            var interactiveConsole = Environment.UserInteractive && !Console.IsInputRedirected;
            Console.WriteLine(interactiveConsole
                ? "Press 's' for statistics, 'q' to quit."
                : "Running without interactive console. Stop the service to quit.");

            if (!interactiveConsole)
            {
                var stopped = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    stopped.Set();
                };
                AppDomain.CurrentDomain.ProcessExit += (sender, e) => stopped.Set();
                stopped.Wait();
            }
            else
            {
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);

                    if (key.KeyChar == 's' || key.KeyChar == 'S')
                    {
                        var stats = server.GetStatistics();
                        Console.WriteLine("\n=== Server Statistics ===");
                        Console.WriteLine($"Total Clients: {stats.TotalClients}");
                        foreach (var stat in stats.PortStats)
                        {
                            var config = portConfigs[stat.Key];
                            Console.WriteLine($"Port {stat.Key} ({config.structure.GetType().Name}): {stat.Value} clients");
                        }
                        Console.WriteLine("=========================\n");
                    }
                    else if (key.KeyChar == 'q' || key.KeyChar == 'Q')
                    {
                        break;
                    }
                }
            }

            server.Stop();
            Console.WriteLine("Server stopped.");
        }
    }
}
