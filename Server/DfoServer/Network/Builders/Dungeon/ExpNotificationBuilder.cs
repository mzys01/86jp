namespace DfoServer.Network.Builders
{
    public static class ExpNotificationBuilder
    {
        
        
        public static byte[] Build(byte level, uint totalExp, ushort remainSp, ushort remainTp = 0,
            uint partyBonusExp = 0, uint memberBonusExp = 0, uint fatigueBuffBonusExp = 0,
            uint seriaBufBonusExp = 0, uint growthContractBonusExp = 0,
            uint weekendBonusExp = 0, uint premiumBonusExp = 0)
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

            // 86JP 客户端的杀怪经验聊天播报按这个长包内的偏移读取 premium bonus。
            // 与 df_game_r 短包相比，这里在 fatigue 后多 5 字节；bonus 必须从 body+34 开始，
            // 否则 0x00000029 会被错读成 0x29000000。
            w.WriteByte(0);
            w.WriteUInt32(seriaBufBonusExp);
            w.WriteUInt32(premiumBonusExp);
            w.WriteZeroBytes(3);
            w.WriteUInt32(0);                  
            w.WriteUInt32(0);                  
            w.WriteByte(0);
            w.WriteByte(0);                    

            w.WriteUInt32(growthContractBonusExp);
            w.WriteUInt32(weekendBonusExp);
            w.WriteUInt32(0);
            w.WriteUInt32(0);                  
            w.WriteUInt32(0);                  
            w.WriteUInt32(0);                  
            w.WriteUInt32(0);                  
            w.WriteUInt32(0);                  
            w.WriteUInt32(0);                  

            w.WriteUInt32(0);
            w.WriteUInt32(0);

            return w.ToArray();               
        }
    }
}
