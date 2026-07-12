using PvfLib;
using System;
using System.IO;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    internal static class PvfArchiveAccessor
    {
        private static readonly Lazy<PvfArchive> Archive = new Lazy<PvfArchive>(() => PvfArchive.Open(GameWorldConfig.PvfArchivePath));

        public static string ReadText(string relativePath)
        {
            var normalizedPath = NormalizeRelativePath(relativePath);
            var content = Archive.Value.GetFileContent(normalizedPath);
            if (string.IsNullOrEmpty(content))
                throw new FileNotFoundException($"PVF 归档中不存在文件: {normalizedPath}", normalizedPath);

            return content;
        }

        public static IReadOnlyList<string> ReadAllText(string relativePath)
        {
            var normalizedPath = NormalizeRelativePath(relativePath);
            var result = new List<string>();
            foreach (var file in Archive.Value.Files)
            {
                if (!string.Equals(file.Path, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                var content = Archive.Value.GetFileContent(file);
                if (!string.IsNullOrEmpty(content))
                    result.Add(content);
            }
            return result;
        }

        private static string NormalizeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("relativePath cannot be null or empty.", nameof(relativePath));

            return relativePath.Replace('\\', '/').TrimStart('.', '/');
        }
    }
}
