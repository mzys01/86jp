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

            if (Array.IndexOf(args, "--selftest-selectable-package") >= 0)
            {
                Environment.Exit(SelfTests.SelectablePackageSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-premium-contract-account-scope") >= 0)
            {
                Environment.Exit(SelfTests.PremiumContractAccountScopeSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-dungeon-map-fallback") >= 0)
            {
                Environment.Exit(SelfTests.DungeonMapFallbackSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-character-option") >= 0)
            {
                Environment.Exit(SelfTests.CharacterOptionSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-slot-expansion-quest") >= 0)
            {
                Environment.Exit(SelfTests.SlotExpansionQuestSelfTest.Run());
                return;
            }

            if (Array.IndexOf(args, "--selftest-clear-map-quest") >= 0)
            {
                Environment.Exit(SelfTests.ClearMapQuestSelfTest.Run());
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

            var server = new MultiStructureTcpServer();

            int channelPort = GameNetworkConfig.ProxyMode ? 7002 : 7001;
            int gamePort = GameNetworkConfig.ProxyMode ? 10012 : 10011;

            var portConfigs = new Dictionary<int, (IProtocolHandler handler, IPacketHeader structure)>
            {
                { channelPort, (new ChannelProtocolHandler(), new ChannelPacketHeader()) },
                { gamePort, (new GameProtocolHandler(packet => server.BroadcastToPortAsync(gamePort, packet)), new GamePacketHeader()) }
            };

            server.Start(portConfigs);

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
