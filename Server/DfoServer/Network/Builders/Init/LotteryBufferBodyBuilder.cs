using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    // NOTI 984 (0x03D8) 增率抽奖数据。固定发 204 字节空态(新角色既有基线)，
    // 活动增率数据对单机服务端无意义。
    public sealed class LotteryBufferBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x03D8;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = new byte[204];
            return true;
        }
    }
}
