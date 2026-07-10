using DfoServer.Game.Dungeon;
using DfoServer.Network.Handlers.Pets;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    // 一局副本的生命周期唯一入口。
    // 真相载体是 PlayerContext.CurrentRun: 进本 new 一个 DungeonRun, 结束置 null,
    // 单局字段随对象消失 -- 全仓不再有逐字段重置清单。
    internal static class DungeonRunLifecycle
    {
        // 进本: 掐掉旧局残留定时器 -> 换新局。
        internal static void BeginRun(EnhancedClientSession session, int dungeonId, byte difficulty)
        {
            CancelAutoFlip(session);
            Game.DeathTower.DeathTowerHandler.ClearTowerState(session);

            session.Player.CurrentRun = new DungeonRun((short)dungeonId, difficulty);
            PetCreatureRuntimeService.BeginDungeon(session, dungeonId, "begin_run");
        }

        // 进塔: 塔是一局副本的变体, 同样换新局(顺带丢弃上一局的全部残留状态)。
        internal static void BeginTowerRun(EnhancedClientSession session, int dungeonId, Game.DeathTower.DeathTowerSession tower)
        {
            CancelAutoFlip(session);

            session.Player.CurrentRun = new DungeonRun((short)dungeonId, 0);
            session.Player.CurrentRun.Tower = tower;
            PetCreatureRuntimeService.BeginDungeon(session, dungeonId, "begin_tower_run");
        }

        // 返城(EPLP/回城/放弃): 先掐定时器(残留的翻牌定时器不能对下一局或城镇状态发包), 再丢弃本局。
        internal static async Task EndRunToTownAsync(EnhancedClientSession session)
        {
            CancelAutoFlip(session);
            PersistSessionExp(session, "town");
            Game.DeathTower.DeathTowerHandler.ClearTowerState(session);
            await PetCreatureRuntimeService.EndDungeonToTownAsync(session, "town");

            session.Player.DungeonSceneUniqueId = 0;
            session.Player.CurrentRun = null;
        }

        // 断线/换角色: 同样丢弃本局。
        // 换角色时必须丢弃当前局 -- PlayerContext 实例跨角色复用, 不丢会把上个角色的副本状态带给下个角色。
        internal static void EndRunOnTeardown(EnhancedClientSession session, string source)
        {
            CancelAutoFlip(session);
            PersistSessionExp(session, source);
            Game.DeathTower.DeathTowerHandler.ClearTowerState(session);
            PetCreatureRuntimeService.EndCharacterSession(session, source);

            session.Player.DungeonSceneUniqueId = 0;
            session.Player.CurrentRun = null;
        }

        // 离开一局时把会话内存的等级/经验落库。
        // 副本杀怪经验只写内存(升级/通关结算才落库): 放弃副本/断线/换角色若不在此落库,
        // 这段经验要么随会话消失, 要么之后被读库的结算逻辑用旧值覆盖掉。
        private static void PersistSessionExp(EnhancedClientSession session, string source)
        {
            var player = session?.Player;
            if (player == null || player.CurrentRun == null || player.CharacterId <= 0)
                return;

            try
            {
                Game.Characters.CharacterProgressService.PersistLevelAndExp(player.CharacterId, player.Level, player.Exp);
            }
            catch (System.Exception ex)
            {
                FileLogger.Log($"[DungeonRunLifecycle] ERROR: exp persist failed on {source}: cid={player.CharacterId} lv={player.Level} exp={player.Exp}: {ex.Message}");
            }
        }

        // 取消当前局的翻牌自动流程定时器(结算界面 2s 布局 + 4s 自动翻免费卡)。
        // 用 Interlocked 交换句柄, 与定时器回调竞争时最多一方拿到实例。
        internal static void CancelAutoFlip(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;

            var cts = Interlocked.Exchange(ref run.AutoFlipCts, null);
            if (cts == null) return;
            try { cts.Cancel(); } catch { /* 与回调线程竞争 Dispose 时的良性竞态, 吞掉即可 */ }
            cts.Dispose();
        }
    }
}
