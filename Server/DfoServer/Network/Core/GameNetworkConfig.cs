using System;

namespace DfoServer.Network
{
    public static class GameNetworkConfig
    {
        public const string ChannelName = "ch.11";

        public static string ServerIp { get; private set; } = "127.0.0.1";

        public const int ChannelServerIndex = 1;

        public const int ChannelIndex = 11;

        public const int InitialUdpPort1 = 12311;

        public const int InitialUdpPort2 = 12312;

        public const int LoginChannelPort = 10128;

        public const int LoginUnknownPort = 17200;

        public const int CommandPacketCount = 1086;

        public const int NotificationPacketCount = 1036;

        public static void Configure(string[] args)
        {
            string serverIp = null;

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--server-ip", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        serverIp = args[i + 1];
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(serverIp))
                serverIp = Environment.GetEnvironmentVariable("SERVER_IP");

            if (!string.IsNullOrWhiteSpace(serverIp))
                ServerIp = serverIp.Trim();
        }
    }
}
