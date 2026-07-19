namespace DfoServer.Network.Builders
{
    public static class DropItemBuilder
    {
        
        
        public static byte[] BuildDrop(
            ushort dropperActorId,
            ushort positionX,
            ushort positionY,
            Game.Dungeon.DropInfo drop,
            ushort ownerActorId)
        {
            var w = new GamePacketWriter();

            
            w.WriteUInt16(dropperActorId);    
            w.WriteUInt16(positionX);
            w.WriteUInt16(positionY);
            w.WriteUInt16(drop.SceneSlot);
            w.WriteUInt32(drop.TemplateId);
            w.WriteByte(drop.UpgradeLevel);
            w.WriteUInt32(drop.PacketValue);
            w.WriteUInt16(drop.Endurance);

            var item = drop.InventoryPayload?.PacketItem;
            var prefix = item?.PrefixData0E ?? System.Array.Empty<byte>();
            var tail = item?.TailData2F ?? System.Array.Empty<byte>();
            w.WriteUInt32(item != null ? item.SealFlag : 0u);
            w.WriteByte(ReadByte(tail, 27));
            w.WriteByte(ReadByte(tail, 29));
            w.WriteUInt16(ReadUInt16(prefix, 6));
            w.WriteUInt32(unchecked((uint)ReadInt32(prefix, 0)));

            
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

        public static byte[] BuildDropSuccessAck(byte listType, ushort slotIndex, int count)
        {
            var w = new GamePacketWriter();
            w.WriteByte(1);
            w.WriteByte(listType);
            w.WriteUInt16(slotIndex);
            w.WriteInt32(count);
            return w.ToArray();
        }

        public static byte[] BuildDropFailureAck(byte errorCode, byte listType)
        {
            var w = new GamePacketWriter();
            w.WriteByte(0);
            w.WriteByte(errorCode);
            w.WriteByte(listType);
            return w.ToArray();
        }

        private static byte ReadByte(byte[] source, int offset)
            => source != null && offset >= 0 && offset < source.Length ? source[offset] : (byte)0;

        private static ushort ReadUInt16(byte[] source, int offset)
            => source != null && offset >= 0 && offset + 2 <= source.Length
                ? System.BitConverter.ToUInt16(source, offset)
                : (ushort)0;

        private static int ReadInt32(byte[] source, int offset)
            => source != null && offset >= 0 && offset + 4 <= source.Length
                ? System.BitConverter.ToInt32(source, offset)
                : 0;

        
        
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

        
        
        
        public static byte[] BuildPickupGold(ushort srcSlot, ushort pickerActorId, int goldAmount, int extraGold = 0)
        {
            var w = new GamePacketWriter();

            w.WriteUInt16(srcSlot);            
            w.WriteUInt16(pickerActorId);      

            // Valid gold slots carry the pickup effect flag and extra/tax gold fields.
            w.WriteByte(1);                    
            w.WriteUInt32((uint)goldAmount);   
            w.WriteByte(1);
            w.WriteUInt32((uint)extraGold);
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
