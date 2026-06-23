using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DfoServer.Network.Parsers
{
    public enum S4LogDirection { Send, Recv }

    public sealed class S4LogPacket
    {
        public S4LogDirection Direction { get; set; }
        public byte Command { get; set; }
        public ushort Type { get; set; }
        public int Size { get; set; }
        public byte[] RawBytes { get; set; }
    }

    public static class S4LogParser
    {
        private static readonly Regex HeaderPattern = new Regex(
            @"^\[(SEND|RECV)-Disp\]\s+Cmd=(\d+)\s+Type=(\d+)\s+Size=(\d+)",
            RegexOptions.Compiled);

        public static List<S4LogPacket> Parse(string filePath)
        {
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            return ParseLines(lines);
        }

        public static List<S4LogPacket> ParseLines(string[] lines)
        {
            var packets = new List<S4LogPacket>();
            S4LogPacket current = null;
            var hexBuilder = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                if (line.StartsWith("---"))
                {
                    if (current != null)
                    {
                        current.RawBytes = HexToBytes(hexBuilder.ToString());
                        packets.Add(current);
                        current = null;
                        hexBuilder.Clear();
                    }
                    continue;
                }

                var match = HeaderPattern.Match(line);
                if (match.Success)
                {
                    if (current != null)
                    {
                        current.RawBytes = HexToBytes(hexBuilder.ToString());
                        packets.Add(current);
                        hexBuilder.Clear();
                    }

                    current = new S4LogPacket
                    {
                        Direction = match.Groups[1].Value == "SEND" ? S4LogDirection.Send : S4LogDirection.Recv,
                        Command = byte.Parse(match.Groups[2].Value),
                        Type = ushort.Parse(match.Groups[3].Value),
                        Size = int.Parse(match.Groups[4].Value),
                    };
                    continue;
                }

                if (current != null && line.Length > 0 && !line.StartsWith("["))
                {
                    hexBuilder.Append(' ').Append(line);
                }
            }

            if (current != null)
            {
                current.RawBytes = HexToBytes(hexBuilder.ToString());
                packets.Add(current);
            }

            return packets;
        }

        public static byte[] ExtractInitSequence(List<S4LogPacket> packets)
        {
            int startIdx = -1;
            for (int i = 0; i < packets.Count; i++)
            {
                if (packets[i].Direction == S4LogDirection.Recv && packets[i].Command == 1 && packets[i].Type == 4)
                {
                    startIdx = i;
                    break;
                }
            }
            if (startIdx < 0) return null;

            var stream = new List<byte>();
            for (int i = startIdx; i < packets.Count; i++)
            {
                var p = packets[i];
                if (p.Direction != S4LogDirection.Recv) continue;
                if (p.RawBytes == null || p.RawBytes.Length == 0) continue;

                
                if (p.Command == 0 && p.Type == 0x001E) break;
                
                if (p.Command == 1 && p.Type != 4 && p.Type != 0x0312) continue;

                stream.AddRange(p.RawBytes);
            }
            return stream.ToArray();
        }

        public static byte[] ExtractGetUserInfoResponse(List<S4LogPacket> packets)
        {
            int type2Idx = -1;
            for (int i = 0; i < packets.Count; i++)
            {
                var p = packets[i];
                if (p.Direction == S4LogDirection.Recv && p.Command == 0 && p.Type == 2
                    && p.RawBytes != null && p.RawBytes.Length > 15 && p.RawBytes[15] == 0x02)
                {
                    type2Idx = i;
                    break;
                }
            }
            if (type2Idx < 0) return null;

            var stream = new List<byte>();
            stream.AddRange(packets[type2Idx].RawBytes);

            
            bool found0286 = false, found01BA = false;
            for (int i = type2Idx + 1; i < packets.Count && (!found0286 || !found01BA); i++)
            {
                var p = packets[i];
                if (p.Direction != S4LogDirection.Recv) continue;
                if (p.Command == 1 && p.Type == 0x0286 && !found0286)
                {
                    if (p.RawBytes != null) stream.AddRange(p.RawBytes);
                    found0286 = true;
                }
                else if (p.Command == 1 && p.Type == 0x01BA && !found01BA)
                {
                    if (p.RawBytes != null) stream.AddRange(p.RawBytes);
                    found01BA = true;
                }
                else if (p.Command == 1 && p.Type == 4)
                {
                    break;
                }
            }
            return stream.Count > 0 ? stream.ToArray() : null;
        }

        private static byte[] HexToBytes(string hex)
        {
            hex = hex.Replace("0x", "").Replace(" ", "").Replace("\r", "").Replace("\n", "");
            if (hex.Length == 0) return new byte[0];
            if (hex.Length % 2 != 0) hex = "0" + hex;
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
