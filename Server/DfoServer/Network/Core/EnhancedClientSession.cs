using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using DfoServer.Game.Session;
using DfoServer.Game.Accounts;

namespace DfoServer.Network
{
    public class EnhancedClientSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public TcpClient TcpClient { get; }
        public NetworkStream Stream => TcpClient.GetStream();
        public DateTime ConnectedTime { get; } = DateTime.Now;
        public IPacketHeader PacketStructure { get; private set; }
        public ushort SequenceNumber { get; private set; }

        
        
        
        
        public PlayerContext Player { get; } = new PlayerContext();

        
        
        
        public AccountRecord Account { get; set; }

        public GameSession GameSession { get; set; }

        public EnhancedClientSession(TcpClient client, IPacketHeader packetStructure)
        {
            TcpClient = client;
            PacketStructure = packetStructure;
            SequenceNumber = 0;
        }

        public async Task SendPacketAsync(byte[] data)
        {
            PacketFileLogger.Log("SEND", data);
            await Stream.WriteAsync(data, 0, data.Length);
        }

        public void Close()
        {
            TcpClient?.Close();
        }
    }
}
