using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    // NOTI 0x006C 活动信息。固定发空列表(u16 count=0 + 尾字节 0):
    // 抓包时代的活动列表对单机服务端无意义, 除种子角色外所有角色一直是空态且工作正常。
    public sealed class EventInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x006C;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = new byte[3];
            return true;
        }
    }
}
