using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PvfLib
{
    
    
    
    
    public static class PvfPacker
    {
        
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
        
        private static readonly HashSet<char> InvalidCharSet = new HashSet<char>(InvalidFileNameChars);

        public class Progress
        {
            public int Current { get; set; }
            public int Total { get; set; }
            public string Phase { get; set; }
        }

        public class PackResult
        {
            public int TotalFiles { get; set; }
            public int Replaced { get; set; }
            public int Unchanged { get; set; }
            public int SkippedChunks { get; set; }
            public int RebuiltChunks { get; set; }
            public int OutputSize { get; set; }
        }

        public static PackResult Pack(string templatePvfPath, string inputDir, string outputPvfPath, Action<Progress> onProgress = null)
        {
            if (!File.Exists(templatePvfPath))
                throw new FileNotFoundException("模板 PVF 文件不存在", templatePvfPath);
            if (!Directory.Exists(inputDir))
                throw new DirectoryNotFoundException("输入目录不存在: " + inputDir);

            using (var archive = PvfArchive.Open(templatePvfPath))
            {
                return PackCore(archive, inputDir, outputPvfPath, onProgress);
            }
        }

        public static PackResult Pack(PvfArchive archive, string inputDir, string outputPvfPath, Action<Progress> onProgress = null)
        {
            if (archive == null) throw new ArgumentNullException(nameof(archive));
            if (!Directory.Exists(inputDir))
                throw new DirectoryNotFoundException("输入目录不存在: " + inputDir);
            return PackCore(archive, inputDir, outputPvfPath, onProgress);
        }

        private static PackResult PackCore(PvfArchive archive, string inputDir, string outputPvfPath, Action<Progress> onProgress)
        {
            var result = new PackResult { TotalFiles = archive.FileCount };
            var progress = new Progress { Total = archive.FileCount, Phase = "Building index" };

            
            var diskIndex = BuildDiskIndex(inputDir);

            
            int chunkCount = 0;
            var chunkGroups = new SortedDictionary<int, List<int>>();
            var fileDiskPaths = new string[archive.FileCount]; 

            progress.Phase = "Matching files";
            for (int i = 0; i < archive.FileCount; i++)
            {
                var file = archive.Files[i];
                int ci = file.Entry.ChunkIndex;
                if (ci >= chunkCount) chunkCount = ci + 1;

                List<int> list;
                if (!chunkGroups.TryGetValue(ci, out list))
                {
                    list = new List<int>();
                    chunkGroups[ci] = list;
                }
                list.Add(i);

                
                if (file.Entry.DataSize > 0 && !file.Name.EndsWith("/") && !file.Name.EndsWith("\\"))
                {
                    string relPath = BuildRelativePath(file.Path, file.Name);
                    string diskPath;
                    if (diskIndex.TryGetValue(relPath.ToLowerInvariant(), out diskPath))
                        fileDiskPaths[i] = diskPath;
                }
            }

            if (onProgress != null)
            {
                progress.Phase = "Matching files";
                progress.Current = archive.FileCount;
                onProgress(progress);
            }

            
            var diskFileSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fileDiskPaths.Length; i++)
            {
                string dp = fileDiskPaths[i];
                if (dp != null && !diskFileSizes.ContainsKey(dp))
                {
                    var fi = new FileInfo(dp);
                    diskFileSizes[dp] = fi.Exists ? fi.Length : -1;
                }
            }

            
            var newItems = new PvfFileItem[archive.FileCount];
            for (int i = 0; i < archive.FileCount; i++)
                newItems[i] = archive.Files[i].Entry;

            
            
            
            var chunkNeedRebuild = new bool[chunkCount];
            for (int i = 0; i < archive.FileCount; i++)
            {
                string dp = fileDiskPaths[i];
                if (dp == null) continue;
                var item = archive.Files[i].Entry;
                long diskSize;
                if (diskFileSizes.TryGetValue(dp, out diskSize) && diskSize != item.DataSize)
                {
                    chunkNeedRebuild[item.ChunkIndex] = true;
                }
            }

            
            string outDir = Path.GetDirectoryName(outputPvfPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            
            string tempBodyPath = outputPvfPath + ".body.tmp";
            var newGroups = new List<GrpiItem>(chunkCount);
            int cumulativeCompressed = 0;

            try
            {
                using (var bodyStream = new FileStream(tempBodyPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024))
                {
                    for (int ci = 0; ci < chunkCount; ci++)
                    {
                        List<int> fileIndices;
                        if (!chunkGroups.TryGetValue(ci, out fileIndices))
                            fileIndices = null;

                        bool needDecompress = chunkNeedRebuild[ci] && fileIndices != null && fileIndices.Count > 0;

                        if (!needDecompress)
                        {
                            
                            
                            byte[] rawEncrypted = archive.GetChunkRawEncrypted(ci);
                            if (rawEncrypted != null)
                            {
                                bodyStream.Write(rawEncrypted, 0, rawEncrypted.Length);
                                cumulativeCompressed += rawEncrypted.Length;
                                
                                var origGroups = archive.Groups;
                                newGroups.Add(new GrpiItem
                                {
                                    CompressedSize = cumulativeCompressed,
                                    OriginalSize = origGroups[ci].OriginalSize
                                });
                                result.SkippedChunks++;
                            }
                        }
                        else
                        {
                            
                            byte[] originalChunk = archive.GetChunkData(ci);
                            var fileUpdates = new List<FileUpdate>(fileIndices.Count);

                            foreach (int fi in fileIndices)
                            {
                                string dp = fileDiskPaths[fi];
                                var item = newItems[fi];

                                if (dp == null || item.DataSize <= 0)
                                {
                                    fileUpdates.Add(new FileUpdate { FileIndex = fi, NewData = null, Changed = false });
                                    continue;
                                }

                                long diskSize;
                                diskFileSizes.TryGetValue(dp, out diskSize);

                                if (diskSize != item.DataSize)
                                {
                                    
                                    byte[] diskData = File.ReadAllBytes(dp);
                                    result.Replaced++;
                                    fileUpdates.Add(new FileUpdate { FileIndex = fi, NewData = diskData, Changed = true });
                                }
                                else
                                {
                                    
                                    result.Unchanged++;
                                    fileUpdates.Add(new FileUpdate { FileIndex = fi, NewData = null, Changed = false });
                                }
                            }

                            
                            byte[] newChunk = RebuildChunk(originalChunk, fileUpdates, newItems);
                            result.RebuiltChunks++;

                            byte[] compressed = PvfDecryptor.ZlibCompress(newChunk);
                            byte[] encrypted = (byte[])compressed.Clone();
                            PvfDecryptor.Decrypt("BodY", encrypted);

                            bodyStream.Write(encrypted, 0, encrypted.Length);
                            cumulativeCompressed += encrypted.Length;
                            newGroups.Add(new GrpiItem
                            {
                                CompressedSize = cumulativeCompressed,
                                OriginalSize = newChunk.Length
                            });
                        }

                        if (onProgress != null && (ci % 50 == 0 || ci == chunkCount - 1))
                        {
                            progress.Phase = $"Packing chunk {ci + 1}/{chunkCount}";
                            progress.Current = ci + 1;
                            progress.Total = chunkCount;
                            onProgress(progress);
                        }
                    }
                }

                
                byte[] tableBytes = new byte[archive.FileCount * 0x18];
                for (int i = 0; i < archive.FileCount; i++)
                {
                    byte[] itemBytes = StructToBytes(newItems[i]);
                    Array.Copy(itemBytes, 0, tableBytes, i * 0x18, 0x18);
                }

                
                byte[] hashBytes = archive.GetRawHashBytes();
                PvfDecryptor.Decrypt("HASH", hashBytes);
                byte[] nameBytes = archive.GetRawNameBytes();

                
                byte[] grpiBytes = new byte[newGroups.Count * 8];
                for (int i = 0; i < newGroups.Count; i++)
                {
                    byte[] g = StructToBytes(newGroups[i]);
                    Array.Copy(g, 0, grpiBytes, i * 8, 8);
                }
                PvfDecryptor.Decrypt("GRPI", grpiBytes);

                
                var header = archive.GetHeader();
                header.BodySize = cumulativeCompressed;
                header.GroupCount = newGroups.Count;
                header.HashTableSize = hashBytes.Length;
                header.NameTableSize = nameBytes.Length;

                byte[] headerBytes = StructToBytes(header);
                PvfDecryptor.Decrypt("HeaD", headerBytes);
                PvfDecryptor.DecryptGuard(headerBytes);

                
                int totalPvfSize = 0x30 + tableBytes.Length + hashBytes.Length +
                                   nameBytes.Length + grpiBytes.Length + cumulativeCompressed;
                result.OutputSize = totalPvfSize;

                using (var outFs = new FileStream(outputPvfPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
                {
                    outFs.Write(headerBytes, 0, 0x30);
                    outFs.Write(tableBytes, 0, tableBytes.Length);
                    outFs.Write(hashBytes, 0, hashBytes.Length);
                    outFs.Write(nameBytes, 0, nameBytes.Length);
                    outFs.Write(grpiBytes, 0, grpiBytes.Length);

                    
                    using (var bodyIn = new FileStream(tempBodyPath, FileMode.Open, FileAccess.Read, FileShare.None, 256 * 1024))
                    {
                        byte[] copyBuf = new byte[256 * 1024];
                        int read;
                        while ((read = bodyIn.Read(copyBuf, 0, copyBuf.Length)) > 0)
                            outFs.Write(copyBuf, 0, read);
                    }
                }

                return result;
            }
            finally
            {
                
                try { if (File.Exists(tempBodyPath)) File.Delete(tempBodyPath); } catch { }
            }
        }

        
        
        
        private static byte[] RebuildChunk(byte[] originalChunk, List<FileUpdate> fileUpdates, PvfFileItem[] newItems)
        {
            var segments = new List<(int origOffset, int origSize, int fileIndex, byte[] newData)>();
            foreach (var upd in fileUpdates)
            {
                var item = newItems[upd.FileIndex];
                if (item.DataSize <= 0) continue;
                segments.Add((item.DataOffset, item.DataSize, upd.FileIndex, upd.Changed ? upd.NewData : null));
            }
            segments.Sort((a, b) => a.origOffset.CompareTo(b.origOffset));

            
            var ms = new MemoryStream();
            int srcPos = 0;
            foreach (var seg in segments)
            {
                
                if (seg.origOffset > srcPos && originalChunk != null)
                {
                    ms.Write(originalChunk, srcPos, seg.origOffset - srcPos);
                }

                
                var item = newItems[seg.fileIndex];
                item.DataOffset = (int)ms.Position;

                
                if (seg.newData != null)
                {
                    ms.Write(seg.newData, 0, seg.newData.Length);
                    item.DataSize = seg.newData.Length;
                }
                else if (originalChunk != null && seg.origOffset >= 0 &&
                         seg.origOffset + seg.origSize <= originalChunk.Length)
                {
                    ms.Write(originalChunk, seg.origOffset, seg.origSize);
                }

                newItems[seg.fileIndex] = item;
                srcPos = seg.origOffset + seg.origSize;
            }

            
            if (originalChunk != null && srcPos < originalChunk.Length)
                ms.Write(originalChunk, srcPos, originalChunk.Length - srcPos);

            return ms.ToArray();
        }

        private static Dictionary<string, string> BuildDiskIndex(string rootDir)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string root = rootDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string filePath in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
            {
                string relative = filePath.Substring(root.Length);
                string key = relative.Replace(Path.DirectorySeparatorChar, '/').ToLowerInvariant();
                if (!index.ContainsKey(key))
                    index[key] = filePath;
            }
            return index;
        }

        private static string BuildRelativePath(string dir, string name)
        {
            string combined = (dir + "/" + name).Replace('\\', '/');
            combined = combined.TrimEnd('/');
            while (combined.Length > 0)
            {
                if (combined.Length > 1 && combined[0] == '.' && combined[1] == '/')
                {
                    combined = combined.Substring(2);
                    continue;
                }
                if (combined[0] == '/')
                {
                    combined = combined.Substring(1);
                    continue;
                }
                break;
            }

            var parts = combined.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                bool hasInvalid = false;
                for (int j = 0; j < parts[i].Length; j++)
                {
                    if (InvalidCharSet.Contains(parts[i][j]))
                    { hasInvalid = true; break; }
                }
                if (hasInvalid)
                {
                    var sb = new StringBuilder(parts[i]);
                    for (int j = 0; j < sb.Length; j++)
                    {
                        if (InvalidCharSet.Contains(sb[j]))
                            sb[j] = '_';
                    }
                    parts[i] = sb.ToString();
                }
            }
            return string.Join("/", parts);
        }

        private static byte[] StructToBytes<T>(T value) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] bytes = new byte[size];
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false); }
            finally { handle.Free(); }
            return bytes;
        }

        private struct FileUpdate
        {
            public int FileIndex;
            public byte[] NewData;
            public bool Changed;
        }
    }
}
