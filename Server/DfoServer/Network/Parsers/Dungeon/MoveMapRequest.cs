using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    public readonly struct MoveMapRequest
    {
        public byte NextX { get; }
        public byte NextY { get; }
        public uint Unknown15 { get; }
        public uint Unknown19 { get; }
        public byte Unknown23 { get; }
        public ushort Unknown25 { get; }
        public ushort Short0 { get; }
        public ushort Short1 { get; }
        public ushort Short2 { get; }
        public ushort Short3 { get; }
        public uint Int0 { get; }
        public uint Int1 { get; }
        public uint Int2 { get; }
        public uint Int3 { get; }
        public ushort Unknown51 { get; }
        public uint Unknown53 { get; }

        public MoveMapRequest(byte nextX, byte nextY, uint u15, uint u19, byte u23, ushort u25,
            ushort s0, ushort s1, ushort s2, ushort s3, uint i0, uint i1, uint i2, uint i3,
            ushort u51, uint u53)
        {
            NextX = nextX;
            NextY = nextY;
            Unknown15 = u15;
            Unknown19 = u19;
            Unknown23 = u23;
            Unknown25 = u25;
            Short0 = s0; Short1 = s1; Short2 = s2; Short3 = s3;
            Int0 = i0; Int1 = i1; Int2 = i2; Int3 = i3;
            Unknown51 = u51;
            Unknown53 = u53;
        }

        public static MoveMapRequest Parse(byte[] body)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));
            if (body.Length < 2)
                throw new ArgumentException($"MOVE_MAP body too short ({body.Length}B); need at least nextX/nextY.", nameof(body));

            byte nextX = body[0];
            byte nextY = body[1];

            
            
            uint u15 = ReadUInt32(body, 2);
            uint u19 = ReadUInt32(body, 6);
            byte u23 = ReadByte(body, 10);
            ushort u25 = ReadUInt16(body, 11);

            ushort s0 = ReadUInt16(body, 14);
            ushort s1 = ReadUInt16(body, 16);
            ushort s2 = ReadUInt16(body, 18);
            ushort s3 = ReadUInt16(body, 20);

            uint i0 = ReadUInt32(body, 22);
            uint i1 = ReadUInt32(body, 26);
            uint i2 = ReadUInt32(body, 30);
            uint i3 = ReadUInt32(body, 34);

            ushort u51 = ReadUInt16(body, 38);
            uint u53 = ReadUInt32(body, 40);

            return new MoveMapRequest(nextX, nextY, u15, u19, u23, u25,
                s0, s1, s2, s3, i0, i1, i2, i3, u51, u53);
        }

        private static byte ReadByte(byte[] b, int o) => o < b.Length ? b[o] : (byte)0;
        private static ushort ReadUInt16(byte[] b, int o) => o + 2 <= b.Length ? BitConverter.ToUInt16(b, o) : (ushort)0;
        private static uint ReadUInt32(byte[] b, int o) => o + 4 <= b.Length ? BitConverter.ToUInt32(b, o) : 0u;
    }
}
