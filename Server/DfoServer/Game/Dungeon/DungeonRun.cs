using System;
using System.Collections.Generic;
using System.Threading;

namespace DfoServer.Game.Dungeon
{
    // 一局副本(从选本进入到返城/断线/换角色)的全部会话内状态。
    // PlayerContext.CurrentRun 持有当前局, null 表示不在副本中:
    // 进本 new 一个实例, 结束置 null, 字段随对象一起消失 -- 不存在"漏重置"。
    // 字段默认值即"返城重置后"的取值, 与旧版逐字段清零清单一致。
    //
    // 跨局存活的状态不放这里(它们留在 PlayerContext 上):
    // - 深渊华丽挑战 UI 开关(选图界面在进本之前切换);
    // - 宠物城镇恢复锚点(副本之间持续计时);
    // - 宠物死亡定时器版本号(单调递增, 用于让过期的延迟回调失效, 归零会让旧回调复活)。
    public sealed class DungeonRun
    {
        public DungeonRun(short dungeonId, byte difficulty)
        {
            DungeonId = dungeonId;
            Difficulty = difficulty;
            Phase = DungeonRunPhase.InProgress;
        }

        // 自测用: 构造一个字段全为默认值的空局。
        public DungeonRun()
        {
        }

        public short DungeonId;
        public byte Difficulty;
        public DungeonRunPhase Phase;
        private readonly HashSet<(int DungeonId, int MapId)> _syncedClearMapQuestTargets =
            new HashSet<(int DungeonId, int MapId)>();

        // 迷宫选择与任务连接
        public int MazeIndex = -1;
        public int LayeredMapIndex = -1;
        public bool MazeQuestConnected;
        public int MazeStartMapId;
        public int MazeStartX = -1;
        public int MazeStartY = -1;

        // 深渊(地狱派对)
        public bool HellMode;
        public byte HellPartyMode;
        public bool VeryDifficultHell;
        public bool HellGorgeousChallenge;
        public int HellMapId = -1;
        public byte HellMapX = 0xFF;
        public byte HellMapY = 0xFF;
        public GameWorld.Dungeon.HellPartyRoomInfo HellRoomInfo;

        // 当前房间与怪物追踪
        public ushort MonsterCount;
        public ushort RoomStartSequence;
        public IReadOnlyList<GameWorld.Dungeon.MonsterSumInfo> RoomMonsters
            = Array.Empty<GameWorld.Dungeon.MonsterSumInfo>();
        public HashSet<ushort> RoomKilledSeqIds = new HashSet<ushort>();
        public RoomKey RoomKey;
        public Dictionary<RoomKey, RoomState> RoomStates = new Dictionary<RoomKey, RoomState>();
        public uint Seed;
        public DnfLcg RoomLcg;
        public List<RidableObjectSpawnEntry> RidableObjects = new List<RidableObjectSpawnEntry>();

        // 通关条件与 Boss
        public ClearConditionState ClearCondition;
        public int BossCode;
        public int[] BossMapPos;

        // 本局累计(经验/金币/统计)
        public uint TotalExp;
        public uint BossTotalExp;
        public uint ChampionTotalExp;
        public uint SuperChampionTotalExp;
        public uint NamedMonsterTotalExp;
        public uint MonsterGrowthContractBonusExp;
        public int TotalGold;

        // 掉落物追踪
        public ushort SceneSlotCounter;
        public Dictionary<ushort, DropInfo> Drops = new Dictionary<ushort, DropInfo>();

        // 通关翻牌
        public List<ClearRewardGenerator.CardReward> CardRewards;
        public int CardFlipCount;
        public byte[] FreeCardSlots = { 0xFF, 0xFF, 0xFF, 0xFF };
        public byte[] PaidCardSlots = { 0xFF, 0xFF, 0xFF, 0xFF };

        // 死亡之塔: 塔是一局副本的变体, 塔专属状态(层数/推进状态/序号)封装在 DeathTowerSession,
        // 挂在局对象上; null=本局不是塔。
        public DeathTower.DeathTowerSession Tower;

        // 翻牌自动流程定时器句柄。保持字段(而非属性): 取消路径用 Interlocked.Exchange(ref ...)。
        // 定时器回调必须捕获所属的 DungeonRun 实例并在动作前校验它仍是当前局,
        // 防止残留回调污染下一局。
        public CancellationTokenSource AutoFlipCts;

        internal bool TryMarkClearMapQuestSynced(int dungeonId, int mapId)
        {
            return _syncedClearMapQuestTargets.Add((dungeonId, mapId));
        }
    }
}
