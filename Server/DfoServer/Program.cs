using DfoServer.Network;
using System;
using System.Collections.Generic;

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

            GameNetworkConfig.Configure(args);

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

            var server = new MultiStructureTcpServer();

            var portConfigs = new Dictionary<int, (IProtocolHandler handler, IPacketHeader structure)>
            {
                { 7001, (new ChannelProtocolHandler(), new ChannelPacketHeader()) },
                { 10011, (new GameProtocolHandler(), new GamePacketHeader()) }
            };

            server.Start(portConfigs);

            Console.WriteLine("Multi-structure TCP server started!");
            Console.WriteLine($"Advertised server IP: {GameNetworkConfig.ServerIp} (ports 7001 channel, 10011 game)");
            Console.WriteLine("Press 's' for statistics, 'q' to quit.");

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

            server.Stop();
            Console.WriteLine("Server stopped.");
        }
    }
}
