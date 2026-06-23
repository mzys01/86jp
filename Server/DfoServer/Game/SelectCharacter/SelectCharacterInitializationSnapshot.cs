using System.Collections.Generic;

namespace DfoServer.Game.SelectCharacter
{
    public sealed class SelectCharacterInitializationSnapshot
    {
        public byte ShopCoinEventFlag { get; set; }

        public ExpertJobInfoSnapshot ExpertJobInfo { get; set; } = new ExpertJobInfoSnapshot();

        public ItemLockListSnapshot ItemLockList { get; set; } = new ItemLockListSnapshot();

        public byte PcRoomPlayTimeState { get; set; }

        public ChampionBreakSystemSnapshot ChampionBreakSystem { get; set; } = new ChampionBreakSystemSnapshot();

        public byte Level60UiState { get; set; }

        public int BossTowerPlaceholder { get; set; }

        
        public byte LoadedMailCount { get; set; }
        public byte MailboxMode { get; set; }
        public ushort NotLoadedMailCount { get; set; }
        public ushort MailboxUnknownCountC { get; set; }

        
        public List<byte> GrowthWeaponStageIds { get; } = new List<byte>();

        
        public List<ShowEffectEntrySnapshot> ShowEffects { get; } = new List<ShowEffectEntrySnapshot>();

        
        public List<PvpMissionEntrySnapshot> PvpMissions { get; } = new List<PvpMissionEntrySnapshot>();

        
        public List<DungeonPermissionEntrySnapshot> DungeonPermissions { get; } = new List<DungeonPermissionEntrySnapshot>();

        
        public List<EventInfoEntrySnapshot> EventInfoEntries { get; } = new List<EventInfoEntrySnapshot>();

        public byte EventInfoTailByte { get; set; }

        
        public byte HotkeyKeyType { get; set; }

        public List<ushort> HotkeyConfigSlots { get; } = new List<ushort>();

        
        
        public byte[] MainGameOptionBlob { get; set; }

        public byte[] QuickchatBank0 { get; set; }

        public byte[] QuickchatBank1 { get; set; }

        
        
        
        public uint CharacInvisibleFalgsPayloadLen { get; set; }

        public List<CharacInvisibleFalgEntrySnapshot> CharacInvisibleFalgs { get; } = new List<CharacInvisibleFalgEntrySnapshot>();

        
        
        public byte[] ServerEventPhaseBitmap { get; set; }

        
        public uint RacingDungeonCurrentEnterCount { get; set; }

        public List<RacingDungeonGroupSnapshot> RacingDungeonGroups { get; } = new List<RacingDungeonGroupSnapshot>();

        public byte[] RacingDungeonGroupFlags { get; set; } = new byte[6];

        public List<uint> RacingDungeonTailIds { get; } = new List<uint>();


        public List<ItemValueEntrySnapshot> CooltimeItems { get; } = new List<ItemValueEntrySnapshot>();

        public List<ItemValueEntrySnapshot> EffectItems { get; } = new List<ItemValueEntrySnapshot>();

        public AchievementCompleteSnapshot AchievementComplete { get; set; } = new AchievementCompleteSnapshot();

        public Unknown730Snapshot Unknown730 { get; set; } = new Unknown730Snapshot();

        public List<AchievementListChunkSnapshot> AchievementChunks { get; } = new List<AchievementListChunkSnapshot>();

        public List<Unknown725Snapshot> Unknown725Packets { get; } = new List<Unknown725Snapshot>();

        public SkillInfoSnapshot SkillInfo { get; set; } = new SkillInfoSnapshot();

        public CreatureItemListSnapshot CreatureItemList { get; set; } = new CreatureItemListSnapshot();

        public List<SelectCharacterUserInfoPacketSnapshot> UserInfoPackets { get; } = new List<SelectCharacterUserInfoPacketSnapshot>();

        
        public List<SkillPointSlotEntrySnapshot> SkillPointSlots { get; } = new List<SkillPointSlotEntrySnapshot>();

        
        public CollectionBoxSnapshot CollectionBox { get; set; } = new CollectionBoxSnapshot();

        
        public RentalInfoSnapshot RentalInfo { get; set; } = new RentalInfoSnapshot();

        
        public byte[] LotteryBufferBlob { get; set; }

        
        public byte CubeType { get; set; }
        public byte CubeGrade { get; set; }

        
        public byte GageType { get; set; }
        public byte GageValue { get; set; }

        
        public ushort FatigueAccelValue { get; set; }
        public byte FatigueAccelState { get; set; }

        
        public int AckCharCreatedTime { get; set; }
        public ushort AckUniqueId { get; set; }
        public int AckAccountRegTime { get; set; }
        public List<AckPremiumEntrySnapshot> AckPremiums { get; } = new List<AckPremiumEntrySnapshot>();
        public int AckCera { get; set; }
        public AckQuestShopEntrySnapshot[] AckQuestShopEntries { get; set; }
        public byte[] AckQuestDisplayIds { get; set; }
        public byte AckCharSlotIndex { get; set; }
        public byte AckTutorialSkipable { get; set; } = 0;
        public ushort AckFatigueBattery { get; set; }
        public ushort AckFatigueGrownUpBuff { get; set; }
        public byte AckTradePunishFlag { get; set; }
        public ushort AckExtraField86JP { get; set; }
        public byte[] AckReserved8B { get; set; }
        public ushort AckPostTutorialU16 { get; set; }
        public byte[] AckUnreadTail { get; set; }

        
        public ushort PremiumServiceType { get; set; }
        public byte[] PremiumServiceData { get; set; }

        public UserInfoAdditionSnapshot UserInfoAddition { get; set; }
    }

    public sealed class AckPremiumEntrySnapshot
    {
        public byte PremiumType { get; set; }
        public byte[] EndTime { get; set; }
    }

    public sealed class AckQuestShopEntrySnapshot
    {
        public ushort QuestId { get; set; }
        public uint QuestValue { get; set; }
    }
}
