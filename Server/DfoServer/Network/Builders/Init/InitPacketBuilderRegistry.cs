using DfoServer.Game.SelectCharacter;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public sealed class InitPacketBuilderRegistry
    {
        private readonly Dictionary<ushort, IInitPacketBuilder> _builders = new Dictionary<ushort, IInitPacketBuilder>();

        public InitPacketBuilderRegistry()
        {
            
            Register(new SkillInfoBodyBuilder());              
            Register(new QuestListBodyBuilder());              
            Register(new UserInfoBodyBuilder());               
            Register(new CreatureListBodyBuilder());           
            Register(new ExpertJobInfoBodyBuilder());          
            Register(new ItemLockListBodyBuilder());           
            Register(new ItemValueListBodyBuilder(0x00AC));    
            Register(new ItemValueListBodyBuilder(0x00AE));    
            Register(new AchievementListBodyBuilder());    
            Register(new TitleBookListBodyBuilder());   
            Register(new ChampionBreakSystemBodyBuilder());    
            Register(new DailyScheduleBodyBuilder());             
            Register(new BuyRestrictItemListBodyBuilder());             

            
            Register(new SimpleByteBodyBuilder(0x00B1, s => s.ShopCoinEventFlag));
            Register(new SimpleByteBodyBuilder(0x01A8, s => s.PcRoomPlayTimeState));
            Register(new SimpleByteBodyBuilder(0x0331, s => s.Level60UiState));

            
            Register(new EmptyBodyBuilder(0x007C));

            
            Register(new BossTowerBodyBuilder());                                       
            Register(new MailboxBodyBuilder());                                         
            Register(new GrowthWeaponBodyBuilder());                                     
            Register(new ShowEffectBodyBuilder());                                       
            Register(new PvpMissionBodyBuilder());                                       
            Register(new DungeonPermissionBodyBuilder());                                

            
            Register(new EventInfoBodyBuilder());                                       
            Register(new HotkeyConfigBodyBuilder());                                    
            Register(new ServerEventPhaseBodyBuilder());                                

            
            Register(new GameOptionBodyBuilder());                                      
            Register(new ClearQuestListBodyBuilder());                            
            Register(new DailyChallengeBodyBuilder());                                   

            
            Register(new SkillPointSlotBodyBuilder());                                  
            Register(new CollectionBoxBodyBuilder());                                   
            Register(new RentalInfoBodyBuilder());                                      
            Register(new LotteryBufferBodyBuilder());                                   
            Register(new CubeInfoBodyBuilder());                                        
            Register(new GageInfoBodyBuilder());                                        
            Register(new FatigueAccelBodyBuilder());                                    

            
            
            
            Register(new UserPositionBodyBuilder());   
            Register(new CeraBodyBuilder());            
            Register(new DbFieldBuilder(0x0036));   
            Register(new DbFieldBuilder(0x0077));   
            Register(new DbFieldBuilder(0x0111));   
            Register(new DbFieldBuilder(0x019F));   

            
            
        }

        public bool TryBuild(ushort notiType, SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            if (_builders.TryGetValue(notiType, out var builder))
                return builder.TryBuild(snapshot, occurrenceIndex, out body);
            body = null;
            return false;
        }

        public bool TryBuildCmd(ushort cmdType, SelectCharacterDataSnapshot snapshot, out byte[] body)
        {
            
            if (cmdType == 0x0004)
            {
                if (SelectCharacterAckBodyBuilder.TryBuild(snapshot, out body))
                    return true;
            }
            
            if (cmdType == 0x0312)
            {
                var initSnap = snapshot.InitializationSnapshot;
                if (initSnap.PremiumServiceData != null)
                {
                    var writer = new GamePacketWriter();
                    writer.WriteByte(1); 
                    writer.WriteUInt16(initSnap.PremiumServiceType);
                    writer.WriteBytes(initSnap.PremiumServiceData);
                    body = writer.ToArray();
                    return true;
                }
            }
            body = null;
            return false;
        }

        private void Register(IInitPacketBuilder builder)
        {
            _builders[builder.NotiType] = builder;
        }
    }
}
