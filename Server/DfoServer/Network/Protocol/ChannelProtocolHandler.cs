using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DfoServer.Infrastructure;

namespace DfoServer.Network
{
    public class ChannelProtocolHandler : BaseProtocolHandler
    {
        public override string ProtocolName => "ChannelProtocol";

        public string ScriptVersion => "59";

        public string AesEncryptionKey => DateTime.Now.ToString("yyyyMMdd") + "000006";

        public string EtcFilePath => ServerPaths.ChannelInfoFilePath;

        public string TestServerIP => GameNetworkConfig.ServerIp;

        public int TestServerPort => 10011;


        private enum PACKETS : int
        {
            CS_ASK_CHANNEL_INFO = 0x1,
            CS_UPDATE_CHANNEL_INFO = 0x2,
            SC_ASK_CHANNEL_INFO = 0x3,
            CS_NOTICE_CHANNEL_SERVER = 0x4,
            CS_CHECK_SCRIPT_VERSION = 0x5,
            SC_CHECK_SCRIPT_VERSION = 0x6,
            CS_ASK_CHANNEL_SCRIPT = 0x7,
            SC_ASK_CHANNEL_SCRIPT = 0x8,
            CS_GET_SCRIPT = 0x9,
            SC_GET_SCRIPT = 0xA,
            CS_CONNECT = 0xB,
            SC_CONNECT = 0xC,
            CS_GET_GC_INFO = 0xD,
            SC_GET_GC_INFO = 0xE,
            CB_GET_CHANNEL_INFO = 0xF,
            BC_GET_CHANNEL_INFO = 0x10,
            CS_ASK_CHANNEL_INFO_NEW = 0x11,
            SC_ASK_CHANNEL_INFO_NEW = 0x12,
        }

        private class ServerInfo
        {
            public string ChannelName { get; set; }
            public int MaxUserNum { get; set; }
            public int CurUserNum { get; set; }
            public string ServerIP { get; set; }
            public int Port { get; set; }
        }

        private class ServerGroupInfo
        {
            public string ServerGroupName { get; set; }
            public int ServerCount { get; set; }
            public List<ServerInfo> Servers { get; set; }
        }

        private class ServerGroup
        {
            public int ServerGroupCount { get; set; }
            public List<ServerGroupInfo> Groups { get; set; }
        }

        public override async Task OnClientConnected(EnhancedClientSession session)
        {
            FileLogger.Log($"[{ProtocolName}] Client connected: {session.SessionId}");
            FileLogger.Log(AesEncryptionKey);
            await Task.CompletedTask;
        }

        public override Task OnClientDisconnected(EnhancedClientSession session)
        {
            FileLogger.Log($"[{ProtocolName}] Client disconnected: {session.SessionId}");
            return Task.CompletedTask;
        }

        public override async Task OnPacketReceived(EnhancedClientSession session, FlexiblePacket packet)
        {
            var header = packet.GetHeader<ChannelPacketHeader>();
            var msgType = (PACKETS)header.msg_no;
            var body = packet.BodyData;

            
            FileLogger.Log($"[{ProtocolName}] Packet received from {session.SessionId}:, Type={msgType}, Length={packet.TotalLength}");

            
            if (packet.BodyData != null && packet.BodyData.Length > 0)
                FileLogger.Log($"[{ProtocolName}] Packet body (hex): {BitConverter.ToString(packet.BodyData).Replace("-", " ")}");
            else
                FileLogger.Log($"[{ProtocolName}] Packet body is empty.");


            switch (msgType)
            {
                case PACKETS.CS_ASK_CHANNEL_INFO_NEW:
                    await HandleCS_ASK_CHANNEL_INFO_NEW(session, body);
                    break;
                case PACKETS.CS_CHECK_SCRIPT_VERSION:
                    await HandleCS_CHECK_SCRIPT_VERSION(session, body);
                    break;
                case PACKETS.CS_GET_SCRIPT:
                    await HandleCS_GET_SCRIPT(session, body);
                    break;
                case PACKETS.CS_CONNECT:
                    await HandleCS_CONNECT(session, body);
                    break;
                default:
                    FileLogger.Log($"[{ProtocolName}] Unknown message type: {msgType}");
                    break;
            }
        }

        
        private async Task SendResponsePacket(EnhancedClientSession session, PACKETS msgType, byte[] data)
        {
            var header = new ChannelPacketHeader()
            {
                classification = 0x7C,
                msg_no = (byte)msgType,
                sLength = (uint)(Marshal.SizeOf<ChannelPacketHeader>() + data.Length),
                check_sum = 0,
                ack = 1
            };
            var responsePacket = new FlexiblePacket(header, data);
            var responseBytes = responsePacket.GetBytes();
            await session.SendPacketAsync(responseBytes);
        }

        private async Task HandleCS_CONNECT(EnhancedClientSession session, byte[] packet)
        {
            var list = new List<byte>();
            list.AddRange(new byte[] { 0, 0, 0, 0 }); 
            list.AddRange(Encoding.ASCII.GetBytes(AesEncryptionKey));
            list.AddRange(new byte[32 - AesEncryptionKey.Length]);
            var data = list.ToArray();

            await SendResponsePacket(session, PACKETS.SC_CONNECT, data);
        }

        private async Task HandleCS_GET_SCRIPT(EnhancedClientSession session, byte[] packet)
        {
            var data = EncryptTool.EncryptData(File.ReadAllBytes(EtcFilePath), AesEncryptionKey);
            await SendResponsePacket(session, PACKETS.SC_GET_SCRIPT, data);
        }

        private async Task HandleCS_ASK_CHANNEL_INFO_NEW(EnhancedClientSession session, byte[] packet)
        {
            var ch11 = Encoding.ASCII.GetBytes("#ch.11");
            var srvi = Encoding.ASCII.GetBytes(TestServerIP);
            var port = TestServerPort;

            var list = new List<byte>();
            list.AddRange(new byte[2]);                 
            list.AddRange(BitConverter.GetBytes(1));    

            list.AddRange(ch11);                        
            list.AddRange(new byte[20 - ch11.Length]);  
            list.AddRange(BitConverter.GetBytes(500));  
            list.AddRange(BitConverter.GetBytes(0));    

            list.AddRange(srvi);                        
            list.AddRange(new byte[16 - srvi.Length]);  
            list.AddRange(BitConverter.GetBytes(port)); 

            var data = EncryptTool.EncryptData(list.ToArray(), AesEncryptionKey);
            await SendResponsePacket(session, PACKETS.SC_ASK_CHANNEL_INFO_NEW, data);
        }

        private async Task HandleCS_CHECK_SCRIPT_VERSION(EnhancedClientSession session, byte[] packet)
        {
            var list = new List<byte>();
            list.AddRange(new byte[] { 0, 0, 0, 0 }); 
            list.AddRange(Encoding.ASCII.GetBytes(ScriptVersion));
            list.AddRange(new byte[16 - ScriptVersion.Length]);
            var data = EncryptTool.EncryptData(list.ToArray(), AesEncryptionKey, false);
            await SendResponsePacket(session, PACKETS.SC_CHECK_SCRIPT_VERSION, data);
        }
    }
}
