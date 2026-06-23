using System;
using System.IO;
using System.Text;

namespace DfoServer.Network
{
    public static class PacketFileLogger
    {
        private static readonly object _lock = new object();
        private static string _logPath;

        static PacketFileLogger()
        {
            var dir = AppContext.BaseDirectory;
            _logPath = Path.Combine(dir, "packet_log.txt");
            
            File.WriteAllText(_logPath, $"=== DfoServer packet log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n");
        }

        public static void Log(string direction, byte[] data)
        {
            if (data == null || data.Length < 3) return;
            var cmd  = data[0];
            var type = (ushort)(data[1] | (data[2] << 8));
            var hex  = BitConverter.ToString(data).Replace("-", " ");
            var line = $"{direction} cmd=0x{cmd:X2} type=0x{type:X4} len={data.Length} hex={hex}\r\n";
            lock (_lock)
            {
                File.AppendAllText(_logPath, line, Encoding.UTF8);
            }
        }
    }
}
