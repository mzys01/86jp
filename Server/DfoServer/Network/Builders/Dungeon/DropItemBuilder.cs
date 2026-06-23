namespace DfoServer.Network.Builders
{
    public static class DropItemBuilder
    {
        
        
        public static byte[] BuildDrop(ushort dropperActorId, ushort sceneSlot, uint itemTemplateId, uint stackCount, ushort ownerActorId)
        {
            var w = new GamePacketWriter();

            
            w.WriteUInt16(dropperActorId);    
            w.WriteUInt16(0);                  
            w.WriteUInt16(0);                  
            w.WriteUInt16(sceneSlot);          
            w.WriteUInt32(itemTemplateId);     
            w.WriteByte(0);                    
            w.WriteUInt32(stackCount);         
            w.WriteUInt16(0);                  
            w.WriteUInt32(0);                  
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteUInt16(0);                  
            w.WriteUInt32(0);                  

            
            w.WriteByte(0);

            
            w.WriteUInt16(0);

            
            w.WriteByte(0);

            
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteUInt16(0);                  
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteByte(0);                    
            w.WriteUInt16(ownerActorId);       

            return w.ToArray();
        }

        
        
        public static byte[] BuildPickupItem(ushort srcSlot, ushort pickerActorId, ushort dstInvSlot, byte moveFlag)
        {
            var w = new GamePacketWriter();

            w.WriteUInt16(srcSlot);
            w.WriteUInt16(pickerActorId);

            for (int i = 0; i < 8; i++)
                w.WriteByte(0);

            w.WriteUInt16(pickerActorId);  
            w.WriteUInt16(dstInvSlot);
            w.WriteByte(moveFlag);

            return w.ToArray();
        }

        
        
        
        public static byte[] BuildPickupGold(ushort srcSlot, ushort pickerActorId, int goldAmount)
        {
            var w = new GamePacketWriter();

            w.WriteUInt16(srcSlot);            
            w.WriteUInt16(pickerActorId);      

            
            w.WriteByte(1);                    
            w.WriteUInt32((uint)goldAmount);   
            w.WriteByte(0);                    
            w.WriteUInt32(0);                  

            
            for (int i = 1; i < 8; i++)
            {
                w.WriteByte(0);                
                w.WriteUInt32(0);              
            }

            return w.ToArray();
        }
    }
}
