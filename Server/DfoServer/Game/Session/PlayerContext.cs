using System;
using System.Collections.Generic;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Session
{
    /// <summary>
    /// </summary>
    public class PlayerContext
    {

        public byte[] Name { get; set; } = System.Array.Empty<byte>();
        public byte Job { get; set; }
        public byte GrowType { get; set; }
        public byte Level { get; set; } = 1;
        public uint Exp { get; set; }
        public CharacterAppearanceEntry[] AppearanceEntries { get; set; } = System.Array.Empty<CharacterAppearanceEntry>();
        public SelectCharacter.UserInfoMinimumTailSnapshot Subtype0Tail { get; set; }
        public ushort UserId { get; set; }
        public byte UserState { get; set; } = 0x01;
        public byte CurTownId { get; set; }
        public byte CurAreaId { get; set; }
        public short CurPosX { get; set; }
        public short CurPosY { get; set; }
        public byte CurDirection { get; set; } = 0x05;
        public byte CurAreaState { get; set; } = 0x03;


        public short CurDungeon { get; set; }
        public byte CurDungeonDifficulty { get; set; }
        public int CurMazeIndex { get; set; } = -1;
        public int CurLayeredMapIndex { get; set; } = -1;
        public byte CurDungeonFlag1 { get; set; }
        public byte CurDungeonFlag2 { get; set; }
        public byte CurMap { get; set; }
        public uint CurMoveMapU15 { get; set; }
        public uint CurMoveMapU19 { get; set; }
        public ushort CurMonsterCnt { get; set; }
        public ushort CurRoomStartSequence { get; set; }
        public IReadOnlyList<GameWorld.Dungeon.MonsterSumInfo> CurRoomMonsters { get; set; }
            = Array.Empty<GameWorld.Dungeon.MonsterSumInfo>();
        public HashSet<ushort> CurRoomKilledSeqIds { get; set; } = new HashSet<ushort>();
        public bool CurBossKilled { get; set; }
        public bool CurDungeonCleared { get; set; }
        public Game.Dungeon.ClearConditionState CurClearCondition { get; set; }
        public int CurBossCode { get; set; }
        public int[] CurBossMapPos { get; set; }
        public uint CurDungeonTotalExp { get; set; }
        public int CurDungeonTotalGold { get; set; }
        public ushort CurSceneSlotCounter { get; set; }
        public Dictionary<ushort, Dungeon.DropInfo> CurDungeonDrops { get; set; }
            = new Dictionary<ushort, Dungeon.DropInfo>();
        public uint CurDungeonSeed { get; set; }
        public Dungeon.DnfLcg CurRoomLcg { get; set; }
        public Dictionary<Dungeon.RoomKey, Dungeon.RoomState> DungeonRoomStates { get; set; }
            = new Dictionary<Dungeon.RoomKey, Dungeon.RoomState>();

        public System.Collections.Generic.List<Dungeon.RidableObjectSpawnEntry> CurDungeonRidableObjects { get; set; }
            = new System.Collections.Generic.List<Dungeon.RidableObjectSpawnEntry>();

        public System.Collections.Generic.List<Dungeon.ClearRewardGenerator.CardReward> CurCardRewards { get; set; }
        public int CurCardFlipCount { get; set; }
        public int CurDungeonClearState { get; set; }
        public byte[] CurFreeCardSlots { get; set; } = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        public byte[] CurPaidCardSlots { get; set; } = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

        /// <summary>
        /// </summary>
        public int CharacterId { get; set; }

        /// <summary>
        /// </summary>
        public DateTime LastPositionPersistAt { get; set; } = DateTime.MinValue;

        /// <summary>
        /// </summary>
        public void HydrateFrom(CharacterRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            CharacterId = record.CharacterId;
            Name = record.Name ?? Name;
            UserId = (ushort)record.CharacterId;
            Job = record.Job;
            GrowType = record.GrowType;
            Level = record.Level == 0 ? Level : record.Level;
            Exp = record.Exp;
            {
                byte townId = record.TownId > 0 ? record.TownId : (byte)1;
                var gate = GameWorld.Town.GetCeraRoomInfo(townId);
                CurTownId = gate.Town > 0 ? gate.Town : (byte)1;
                CurAreaId = gate.Town > 0 ? gate.Area : (byte)0;
                CurPosX = gate.Town > 0 ? gate.X : (short)474;
                CurPosY = gate.Town > 0 ? gate.Y : (short)234;
                CurDirection = 5;
                CurAreaState = 3;
            }

            if (record.Appearance != null && record.Appearance.Length > 0)
                AppearanceEntries = record.Appearance;

            Subtype0Tail = record.Subtype0Tail;
        }
    }
}
