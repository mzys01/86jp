namespace DfoServer.Game.Session
{
    public partial class PlayerContext
    {
        // 副本状态真相: 一局一个 DungeonRun 对象, null = 不在副本中。
        // 进本由 DungeonRunLifecycle.BeginRun/BeginTowerRun 置换新实例,
        // 返城/断线/换角色置 null -- 单局字段随对象消失, 不存在漏重置。
        public Game.Dungeon.DungeonRun CurrentRun { get; internal set; }

        // ---- 跨局存活字段(刻意不随 run 重建) ----

        // 深渊华丽挑战 UI 开关: 在选图界面(进本之前)切换。
        public bool HellPartyGorgeousChallengeEnabled { get; set; }
    }
}
