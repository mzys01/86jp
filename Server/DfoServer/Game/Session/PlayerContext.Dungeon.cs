using System;
using System.Collections.Generic;

namespace DfoServer.Game.Session
{
    public partial class PlayerContext
    {
        public short CurDungeon { get; set; }
        public byte CurDungeonDifficulty { get; set; }
        public int CurMazeIndex { get; set; } = -1;
        public int CurLayeredMapIndex { get; set; } = -1;
        public byte CurDungeonFlag1 { get; set; }
        public byte CurDungeonFlag2 { get; set; }
        public bool CurMazeQuestConnected { get; set; }
        public int CurMazeStartMapId { get; set; }
        public int CurMazeStartX { get; set; } = -1;
        public int CurMazeStartY { get; set; } = -1;
        public bool HellPartyGorgeousChallengeEnabled { get; set; }
        public bool CurDungeonHellMode { get; set; }
        public byte CurDungeonHellPartyMode { get; set; }
        public bool CurDungeonVeryDifficultHell { get; set; }
        public bool CurDungeonHellGorgeousChallenge { get; set; }
        public int CurDungeonHellMapId { get; set; } = -1;
        public byte CurDungeonHellMapX { get; set; } = 0xFF;
        public byte CurDungeonHellMapY { get; set; } = 0xFF;
        public GameWorld.Dungeon.HellPartyRoomInfo CurDungeonHellRoomInfo { get; set; }
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
        public uint CurDungeonBossTotalExp { get; set; }
        public uint CurDungeonChampionTotalExp { get; set; }
        public uint CurDungeonSuperChampionTotalExp { get; set; }
        public uint CurDungeonNamedMonsterTotalExp { get; set; }
        public uint CurDungeonMonsterGrowthContractBonusExp { get; set; }
        public int CurDungeonTotalGold { get; set; }
        public ushort CurSceneSlotCounter { get; set; }
        public Dictionary<ushort, Dungeon.DropInfo> CurDungeonDrops { get; set; }
            = new Dictionary<ushort, Dungeon.DropInfo>();
        public uint CurDungeonSeed { get; set; }
        public Dungeon.DnfLcg CurRoomLcg { get; set; }
        public Dungeon.RoomKey CurRoomKey { get; set; }
        public Dictionary<Dungeon.RoomKey, Dungeon.RoomState> DungeonRoomStates { get; set; }
            = new Dictionary<Dungeon.RoomKey, Dungeon.RoomState>();
        public List<Dungeon.RidableObjectSpawnEntry> CurDungeonRidableObjects { get; set; }
            = new List<Dungeon.RidableObjectSpawnEntry>();
    }
}
