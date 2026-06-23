namespace DfoServer.Network.Builders
{
    public static class ExpNotificationBuilder
    {
        
        
        public static byte[] Build(byte level, uint totalExp, ushort spTree0, ushort spTree1)
        {
            var w = new GamePacketWriter();

            
            w.WriteByte(level);                
            w.WriteUInt32(totalExp);           
            w.WriteUInt32(0);                  
            w.WriteUInt32(0);                  
            w.WriteUInt16(spTree0);            
            w.WriteUInt16(spTree1);            
            w.WriteUInt16(0);                  
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
