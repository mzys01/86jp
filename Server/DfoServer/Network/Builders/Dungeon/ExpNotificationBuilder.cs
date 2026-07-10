using DfoServer.Game.Accounts;

namespace DfoServer.Network.Builders
{
    public static class ExpNotificationBuilder
    {
        public const int BodyLength = 95;
        public const int GrowthCapsuleExpOffset = 59;
        public const int HonorLevelOffset = 63;
        public const int HonorExpOffset = 67;

        public static byte[] Build(byte level, uint totalExp, ushort remainSp, ushort remainTp,
            HonorLevelSummary honorLevel,
            uint partyBonusExp = 0, uint memberBonusExp = 0, uint fatigueBuffBonusExp = 0,
            uint seriaBufBonusExp = 0, uint growthContractBonusExp = 0,
            uint weekendBonusExp = 0, uint premiumBonusExp = 0,
            uint growthCapsuleExp = 0)
        {
            var w = new GamePacketWriter();

            w.WriteByte(level);
            w.WriteUInt32(totalExp);
            w.WriteUInt32(partyBonusExp);
            w.WriteUInt32(memberBonusExp);
            w.WriteUInt16(remainSp);
            w.WriteUInt16(remainSp);
            w.WriteUInt16(remainTp);
            w.WriteUInt16(0);
            w.WriteUInt32(0);
            w.WriteUInt32(fatigueBuffBonusExp);
            w.WriteByte(0);
            w.WriteUInt32(seriaBufBonusExp);
            w.WriteUInt32(premiumBonusExp);
            w.WriteUInt32(0);
            w.WriteUInt32(0);
            w.WriteUInt32(0);
            w.WriteByte(0);
            w.WriteUInt32(growthContractBonusExp);
            w.WriteUInt32(weekendBonusExp);
            w.WriteUInt32(growthCapsuleExp);
            w.WriteUInt32(honorLevel?.HonorLevel ?? 0);
            w.WriteUInt32(honorLevel?.HonorExp ?? 0);
            w.WriteUInt32(0);
            w.WriteUInt32(0);
            w.WriteUInt32(0);
            w.WriteUInt32(0);

            // 86JP EXP handler 固定消费到 body+87；原协议仍带 8 字节兼容尾部。
            w.WriteUInt32(0);
            w.WriteUInt32(0);

            return w.ToArray();
        }
    }
}
