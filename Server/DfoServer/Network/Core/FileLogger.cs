using System;
using System.IO;
using System.Text;

namespace DfoServer
{
    public static class FileLogger
    {
        private static readonly object _lock = new object();
        private static readonly string _logPath;

        static FileLogger()
        {
            var dir = AppContext.BaseDirectory;
            _logPath = Path.Combine(dir, "server.log");
            File.WriteAllText(_logPath, $"=== DfoServer started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n");
        }

        public static void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n";
            lock (_lock)
            {
                File.AppendAllText(_logPath, line, Encoding.UTF8);
            }
        }

        public static void Log(string format, params object[] args)
        {
            Log(string.Format(format, args));
        }
    }
}
