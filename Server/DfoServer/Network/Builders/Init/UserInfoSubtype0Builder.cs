using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using System;
using System.Collections.Generic;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    
    
    
    
    
    
    
    
    
    
    
    
    public static class UserInfoSubtype0Builder
    {
        
        
        
        
        public static byte[] BuildRemainingBytes(CharacterRecord record)
        {
            var writer = new GamePacketWriter();

            
            writer.WriteByte(record.Job);           
            writer.WriteByte(record.GrowType);      
            writer.WriteByte(record.Level);              
            writer.WriteByte(record.PvpGrade);           
            writer.WriteByte(record.PvpRatingGrade);     
            writer.WriteByte(record.UserState);          

            
            var appearances = GetAppearanceEntries(record);
            writer.WriteByte((byte)appearances.Count);   
            foreach (var e in appearances)
                WriteAppearanceEntry(writer, e);

            
            WriteTail(writer, record);

            return writer.ToArray();
        }

        
        
        
        
        
        private static void WriteTail(GamePacketWriter writer, CharacterRecord record)
        {
            var t = record.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();

            writer.WriteUInt32(t.NameTagItemId);            
            writer.WriteByte(t.CreatureField1);             
            writer.WriteByte(t.CreatureField2);             
            writer.WriteByte(t.CreatureField3);             
            writer.WriteByte(t.CreatureField4);             
            var cb = t.CreatureBuffer != null && t.CreatureBuffer.Length == 8
                ? t.CreatureBuffer : new byte[8];
            writer.WriteBytes(cb);                          
            writer.WriteByte(t.Stamina);                    
            writer.WriteUInt32(t.FatiguePenalty);           
            writer.WriteByte(t.IsEventCharacter);           
            writer.WriteUInt32(t.PcRoomId);                 
            writer.WriteByte(t.IsPrivateStore);             
            writer.WriteByte(t.IsPremiumPcRoom);            
            writer.WriteByte(t.ServerGroupId);              
            writer.WriteUInt32(t.BlackCount);               
            writer.WriteByte(t.GuildLevel);                 
            writer.WriteUInt32(t.ChaosPoint);               
            writer.WriteByte(1);                            
            writer.WriteByte(t.DisguiseKind);               
            writer.WriteByte(t.IsDisguised);                
            writer.WriteByte(t.ExpertJobType);              
            writer.WriteUInt32(t.ExpertJobExp);             
            writer.WriteByte(0);                            
            writer.WriteUInt32(0);                          
            writer.WriteUInt16(0);                          
            writer.WriteByte(t.IsHardcoreMode);             
            writer.WriteByte(t.IsHardcoreDead);             
            writer.WriteUInt16(t.HardcoreDeathCount);       
            writer.WriteUInt32(t.ProgressA);                
            writer.WriteUInt32(t.ProgressB);                
            writer.WriteByte(t.UserStateBits);              
            writer.WriteUInt32(t.ChatBanEndTime);           
            writer.WriteByte(100);                          
            writer.WriteUInt16(t.FatigueUpdate);            
            writer.WriteByte(t.ReturnUserFlag);             
            writer.WriteUInt16(t.ChannelDisplayMode);       
            writer.WriteByte(t.ChannelType);                
            writer.WriteUInt16(t.ChannelId);                
            writer.WriteByte(t.SkillTreeIndex);             
            writer.WriteByte(t.IsReturnUser);               
            writer.WriteByte(t.LinkSlotEnabled);            
            writer.WriteByte(t.LinkTypeA);                  
            writer.WriteByte(t.LinkTypeB);                  
            writer.WriteUInt16(t.EmotionIndex);             
            writer.WriteByte(t.ActionByte);                 
            writer.WriteUInt16(t.FatigueDisplayUpdate);     
            writer.WriteByte(t.CostumeFlag);                
            writer.WriteByte(t.AuraFlag);                   
            writer.WriteByte(t.PetDisplayFlag);             
            writer.WriteByte(t.TitleDisplayFlag);           
            writer.WriteUInt32(t.PvpStatA);                 
            writer.WriteByte(t.PvpWinStreak);               
            writer.WriteByte(t.PvpLoseStreak);              
            writer.WriteUInt32(t.PvpRankPoint);             
            writer.WriteByte(t.TrailingByte);               
        }

        
        
        

        private static List<CharacterAppearanceEntry> GetAppearanceEntries(CharacterRecord record)
        {
            var result = new List<CharacterAppearanceEntry>();
            if (record.Appearance == null)
                return result;

            foreach (var e in record.Appearance)
            {
                if (e != null)
                    result.Add(e);
            }
            return result;
        }

        
        
        
        
        
        
        
        internal static void WriteAppearanceEntry(GamePacketWriter writer, CharacterAppearanceEntry e)
        {
            writer.WriteByte(e.Slot);
            writer.WriteInt32(e.ItemId);
            writer.WriteInt32(e.ExpansionLen);
            writer.WriteBytes(e.ExpansionData != null && e.ExpansionData.Length == 4
                ? e.ExpansionData : new byte[4]);
            writer.WriteByte(e.State);
            writer.WriteInt32(e.ClearAvatar);
            writer.WriteUInt32(e.EnchantValue);
            writer.WriteByte(e.Flag20);
        }
    }
}
