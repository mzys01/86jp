using DfoServer.Game.Characters;
using DfoServer.Network;
using System;
using System.Collections.Generic;
using System.Text;

namespace DfoServer.Network.Builders
{
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public static class GetUserInfoResponseBuilder
    {
        public static List<byte[]> Build(CharacterRecord record, GetUserInfoTemplate template)
        {
            return new List<byte[]>
            {
                BuildType2Packet(record, template),
                GamePacketEnvelopeBuilder.Build(0x01, 0x0286, new byte[] { 0x00, 0x04 }),
                BuildPkt2Packet(record, template),
            };
        }

        private static byte[] BuildType2Packet(CharacterRecord record, GetUserInfoTemplate template)
        {
            var writer = new GamePacketWriter();

            
            writer.WriteByte(0x02);

            
            writer.WriteUInt16(template.GateOrCount1);
            writer.WriteUInt16(template.GateOrCount2);
            writer.WriteByte(template.FlagOrManage);
            writer.WriteInt32(template.KeyOrPoint);
            writer.WriteUInt16(template.Unknown16);
            writer.WriteInt32(template.Unknown32);
            writer.WriteUInt16(1); 

            
            writer.WriteUInt16(0);                  
            writer.WriteDstr(record.Name);

            writer.WriteByte(0x00);                 
            writer.WriteByte(0x00);                 

            writer.WriteByte(record.Job);
            writer.WriteByte(record.GrowType);
            writer.WriteByte(record.Level);

            writer.WriteByte(0x00);                 
            writer.WriteByte(0x00);                 
            writer.WriteInt32(0);                   
            writer.WriteInt32(0);                   

            
            var filtered = FilterAppearances(record.Appearance, 0x0B);
            writer.WriteByte((byte)filtered.Count);
            foreach (var a in filtered)
                UserInfoSubtype0Builder.WriteAppearanceEntry(writer, a);

            
            writer.WriteInt32(0);                   
            writer.WriteByte(0x00);                 
            writer.WriteByte(0x00);                 
            writer.WriteByte(0x00);                 
            writer.WriteByte(0x00);                 
            writer.WriteZeroBytes(8);               
            writer.WriteByte(0x00);                 
            writer.WriteInt32(0);                   
            writer.WriteByte(0x00);                 
            writer.WriteByte(0x00);                 
            writer.WriteByte(0x00);                 
            writer.WriteInt32(0);                   
            writer.WriteByte(0x00);                 
            writer.WriteByte(0x00);                 
            writer.WriteByte(0x00);                 
            writer.WriteByte(0x00);                 

            var body = writer.ToArray();
            return BuildPacketWithRouting(0x00, 0x0002, body, template.Pkt0RoutingByte7);
        }

        private static byte[] BuildPkt2Packet(CharacterRecord record, GetUserInfoTemplate template)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(template.Pkt2ResultCode);
            writer.WriteInt32(template.Pkt2CharacterKey);
            writer.WriteByte(template.Pkt2SlotFlag1);
            writer.WriteByte(template.Pkt2SlotFlag2);
            writer.WriteByte(template.Pkt2StateFlag);
            writer.WriteByte(template.Pkt2Flag3);
            writer.WriteUInt16(template.Pkt2Reserved);
            writer.WriteDstr(record.Name);
            writer.WriteZeroBytes(5);
            writer.WriteByte(0xFF);
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);

            return GamePacketEnvelopeBuilder.Build(0x01, 0x01BA, writer.ToArray());
        }

        private static List<CharacterAppearanceEntry> FilterAppearances(CharacterAppearanceEntry[] appearances, byte maxSlot)
        {
            var result = new List<CharacterAppearanceEntry>();
            if (appearances == null) return result;
            foreach (var a in appearances)
            {
                if (a != null && a.Slot <= maxSlot)
                    result.Add(a);
            }
            return result;
        }

        private static byte[] BuildPacketWithRouting(byte command, ushort type, byte[] body, byte routingByte7)
        {
            int totalLen = 15 + (body != null ? body.Length : 0);
            var packet = new byte[totalLen];
            packet[0] = command;
            Buffer.BlockCopy(BitConverter.GetBytes(type), 0, packet, 1, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(totalLen), 0, packet, 3, 4);
            packet[7] = routingByte7;
            if (body != null && body.Length > 0)
                Buffer.BlockCopy(body, 0, packet, 15, body.Length);
            return packet;
        }

        
        
        

        public static GetUserInfoTemplate ParseTemplate(byte[] sampleBlob)
        {
            if (sampleBlob == null || sampleBlob.Length < 15)
                return null;

            var packets = SplitPackets(sampleBlob);
            if (packets.Count < 3) return null;

            var template = new GetUserInfoTemplate();

            
            template.Pkt0RoutingByte7 = packets[0][7];

            
            var body0 = ExtractBody(packets[0]);
            if (body0.Length >= 18)
            {
                template.GateOrCount1 = BitConverter.ToUInt16(body0, 1);
                template.GateOrCount2 = BitConverter.ToUInt16(body0, 3);
                template.FlagOrManage = body0[5];
                template.KeyOrPoint = BitConverter.ToInt32(body0, 6);
                template.Unknown16 = BitConverter.ToUInt16(body0, 10);
                template.Unknown32 = BitConverter.ToInt32(body0, 12);
            }

            
            var body2 = ExtractBody(packets[2]);
            if (body2.Length >= 11)
            {
                template.Pkt2ResultCode = body2[0];
                template.Pkt2CharacterKey = BitConverter.ToInt32(body2, 1);
                template.Pkt2SlotFlag1 = body2[5];
                template.Pkt2SlotFlag2 = body2[6];
                template.Pkt2StateFlag = body2[7];
                template.Pkt2Flag3 = body2[8];
                template.Pkt2Reserved = BitConverter.ToUInt16(body2, 9);
            }

            return template;
        }

        private static List<byte[]> SplitPackets(byte[] blob)
        {
            var packets = new List<byte[]>();
            int offset = 0;
            while (offset + 7 <= blob.Length)
            {
                int length = BitConverter.ToInt32(blob, offset + 3);
                if (length <= 0 || length > blob.Length - offset) break;
                var packet = new byte[length];
                Buffer.BlockCopy(blob, offset, packet, 0, length);
                packets.Add(packet);
                offset += length;
            }
            return packets;
        }

        private static byte[] ExtractBody(byte[] packet)
        {
            const int headerLen = 15;
            if (packet == null || packet.Length <= headerLen)
                return new byte[0];
            var body = new byte[packet.Length - headerLen];
            Buffer.BlockCopy(packet, headerLen, body, 0, body.Length);
            return body;
        }
    }

    public sealed class GetUserInfoTemplate
    {
        
        public byte Pkt0RoutingByte7 { get; set; }
        public ushort GateOrCount1 { get; set; }
        public ushort GateOrCount2 { get; set; }
        public byte FlagOrManage { get; set; }
        public int KeyOrPoint { get; set; }
        public ushort Unknown16 { get; set; }
        public int Unknown32 { get; set; }

        public int SeedCharacterId { get; set; } = 1000;

        
        public byte Pkt2ResultCode { get; set; }
        public int Pkt2CharacterKey { get; set; }
        public byte Pkt2SlotFlag1 { get; set; }
        public byte Pkt2SlotFlag2 { get; set; }
        public byte Pkt2StateFlag { get; set; }
        public byte Pkt2Flag3 { get; set; }
        public ushort Pkt2Reserved { get; set; }
    }
}
