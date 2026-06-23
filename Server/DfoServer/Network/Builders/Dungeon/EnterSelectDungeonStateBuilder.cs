using DfoServer.Game.Session;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class EnterSelectDungeonStateBuilder
    {
        public static byte[] BuildUserState(PlayerContext player)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(0x01);
            writer.WriteUInt16(player.UserId);
            writer.WriteByte(player.UserState);
            return writer.ToArray();
        }

        public static byte[] BuildEnterSelectDungeon(PlayerContext player)
        {
            
            var writer = new GamePacketWriter();

            writer.WriteInt32(0x01);                  
            writer.WriteUInt16(0x0000);               
            writer.WriteByte(0x01);                   
            writer.WriteUInt16(player.UserId);        
            writer.WriteZeroBytes(10);                
            return writer.ToArray();
        }
    }
}