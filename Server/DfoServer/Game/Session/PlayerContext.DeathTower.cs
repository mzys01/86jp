namespace DfoServer.Game.Session
{
    public partial class PlayerContext
    {
        // 塔状态(进塔时创建, 返城/断线时清除; null=不在塔中)
        public DeathTower.DeathTowerSession DeathTowerState { get; set; }

        public bool IsInDeathTower => DeathTowerState != null;
    }
}
