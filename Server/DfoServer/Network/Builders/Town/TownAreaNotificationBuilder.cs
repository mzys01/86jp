using DfoServer.Game.Session;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class TownAreaNotificationBuilder
    {
        public static TownUserSnapshot CreateCurrentSnapshot(PlayerContext player)
        {
            return new TownUserSnapshot
            {
                UserId = player.UserId,
                TownId = player.CurTownId,
                AreaId = player.CurAreaId,
                PosX = player.CurPosX,
                PosY = player.CurPosY,
                Direction = player.CurDirection,
                State = player.CurAreaState,
            };
        }

        public static byte[] BuildUserArea(TownUserSnapshot snapshot)
        {
            var writer = new GamePacketWriter();

            writer.WriteUInt16(snapshot.UserId);
            writer.WriteByte(snapshot.TownId);
            writer.WriteByte(snapshot.AreaId);
            writer.WriteInt16(snapshot.PosX);
            writer.WriteInt16(snapshot.PosY);
            writer.WriteByte(snapshot.Direction);
            writer.WriteByte(snapshot.State);
            return writer.ToArray();
        }

        public static byte[] BuildAreaUsers(TownUserSnapshot snapshot)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(snapshot.TownId);
            writer.WriteByte(snapshot.AreaId);
            writer.WriteUInt16(0x0001);
            writer.WriteUInt16(snapshot.UserId);
            writer.WriteInt16(snapshot.PosX);
            writer.WriteInt16(snapshot.PosY);
            writer.WriteByte(snapshot.Direction);
            writer.WriteByte(snapshot.State);
            return writer.ToArray();
        }
    }
}