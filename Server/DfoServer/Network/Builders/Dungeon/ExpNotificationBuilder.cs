namespace DfoServer.Network.Builders
{
    public static class ExpNotificationBuilder
    {
        
        
        public static byte[] Build(byte level, uint totalExp, ushort remainSp, ushort remainTp = 0)
        {
            var w = new GamePacketWriter();

            w.WriteByte(level);
            w.WriteUInt32(totalExp);
            w.WriteUInt32(0);
            w.WriteUInt32(0);
            w.WriteUInt16(remainSp);
            w.WriteUInt16(remainSp);
            w.WriteUInt16(remainTp);
            w.WriteUInt16(0);                  
            w.WriteUInt32(0);                  

            
            w.WriteUInt32(0);                  
            w.WriteByte(0);                    
            w.WriteUInt32(0);                  
            w.WriteUInt32(0);                  
            w.WriteUInt32(0);                  
            w.WriteUInt32(0);                  
            w.WriteUInt32(0);                  
            w.WriteByte(0);                    

            
            w.WriteUInt32(0);                  
            w.WriteUInt32(0);                  
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
