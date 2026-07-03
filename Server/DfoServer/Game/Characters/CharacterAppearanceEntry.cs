using System;

namespace DfoServer.Game.Characters
{
    
    
    
    
    
    public sealed class CharacterAppearanceEntry
    {
        public CharacterAppearanceEntry(byte slot, int displayItemId, int expansionLen, byte[] expansionData, byte state, int clearAvatar, uint enchantValue, byte flag20)
        {
            Slot = slot;
            DisplayItemId = displayItemId;
            ExpansionLen = expansionLen;
            ExpansionData = expansionData ?? new byte[4];
            State = state;
            ClearAvatar = clearAvatar;
            EnchantValue = enchantValue;
            Flag20 = flag20;
        }

        public byte Slot { get; set; }

        
        // NOTI2 外观列表中的 itemId 是显示用模板ID；克隆装扮/替换称号动画会覆盖真实穿戴物品ID。
        public int DisplayItemId { get; set; }

        
        public int ExpansionLen { get; set; } = 4;

        
        public byte[] ExpansionData { get; }

        
        public byte State { get; set; }

        
        public int ClearAvatar { get; set; }

        
        public uint EnchantValue { get; set; }

        
        public byte Flag20 { get; set; }

        
        public static CharacterAppearanceEntry FromBytes(byte[] buffer, int offset)
        {
            var slot = buffer[offset];
            var itemId = BitConverter.ToInt32(buffer, offset + 1);
            var expLen = BitConverter.ToInt32(buffer, offset + 5);
            var expData = new byte[4];
            Buffer.BlockCopy(buffer, offset + 9, expData, 0, 4);
            var state = buffer[offset + 13];
            var clearAvatar = BitConverter.ToInt32(buffer, offset + 14);
            var enchantValue = BitConverter.ToUInt32(buffer, offset + 18);
            var flag20 = buffer[offset + 22];
            return new CharacterAppearanceEntry(slot, itemId, expLen, expData, state, clearAvatar, enchantValue, flag20);
        }
    }
}
