using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Network;

namespace DfoServer.Game.DeathTower
{
    public sealed class DeathTowerHandler
    {
        public bool TryCreateSession(int dungeonId, out DeathTowerSession tower)
        {
            tower = null;
            var config = DeathTowerData.GetConfig(dungeonId);
            if (config == null)
                return false;

            tower = new DeathTowerSession(config);
            return true;
        }

        public async Task SendEntryPacketsAsync(EnhancedClientSession session, DeathTowerSession tower, byte difficulty = 0)
        {
            var dungeonId = tower.Config.DungeonId;
            var hasRun = session.Player.CurrentRun != null;
            FileLogger.Log($"[DeathTower] ENTER: cid={session.Player.CharacterId} dungeon={dungeonId} difficulty={difficulty} hasRun={hasRun} stages={tower.Config.TotalStages} basisLv={tower.Config.BasisLevel}");

            var dungeonInfoBody = DeathTowerPacketBuilder.BuildTowerDungeonInfo(dungeonId, difficulty);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001C, dungeonInfoBody));
            FileLogger.Log($"[DeathTower] SENT 0x001C DUNGEON_INFO(formal tower): bodyLen={dungeonInfoBody.Length}");

            // NOTI 142 DEATH_TOWER_INFO (8B)
            var infoBody = DeathTowerPacketBuilder.BuildTowerInfo(dungeonId, (ushort)tower.Config.TotalStages);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x008E, infoBody));
            FileLogger.Log($"[DeathTower] SENT 0x008E TOWER_INFO: bodyLen={infoBody.Length}");

            // NOTI 143 首层
            await SendStageMap(session, tower);

            // NOTI 0x1E FINISH_LOADING
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001E, new byte[0]));
            FileLogger.Log($"[DeathTower] SENT 0x001E FINISH_LOADING (entry)");
        }

        public async Task<bool> TryHandleMoveMap(EnhancedClientSession session)
        {
            var tower = session.Player.DeathTowerState;
            if (tower == null)
                return false;

            var prevState = tower.State;
            if (prevState >= 1)
                await SyncCurrentStageClearMapAsync(session, tower, "tower_move_map");

            if (!tower.TryAdvanceStage())
            {
                FileLogger.Log($"[DeathTower] MOVE_MAP rejected: cid={session.Player.CharacterId} stage={tower.CurrentStage}/{tower.EndStage} state={tower.State} (need state>=1, not last stage)");
                return true;
            }

            if (prevState == 1)
                FileLogger.Log($"[DeathTower] MOVE_MAP advance from state=1 (0x009F(2) not received, 86JP may skip it)");

            FileLogger.Log($"[DeathTower] ADVANCE: cid={session.Player.CharacterId} stage={tower.CurrentStage}/{tower.EndStage} map={tower.GetCurrentMapId()}");

            await SendStageMap(session, tower);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001E, new byte[0]));
            FileLogger.Log($"[DeathTower] SENT 0x001E FINISH_LOADING (advance)");

            return true;
        }

        public async Task HandleStageCommand(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var tower = session.Player.DeathTowerState;
            if (tower == null)
            {
                FileLogger.Log($"[DeathTower] STAGE_CMD ignored: cid={session.Player?.CharacterId} not in tower");
                return;
            }
            if (body == null || body.Length < 1)
            {
                FileLogger.Log($"[DeathTower] STAGE_CMD ignored: body null or empty");
                return;
            }

            var commandType = body[0];
            switch (commandType)
            {
                case 1:
                    tower.SetFighting();
                    FileLogger.Log($"[DeathTower] STAGE_CMD(1) fight start: cid={session.Player.CharacterId} stage={tower.CurrentStage}");
                    break;
                case 2:
                    tower.SetCleared();
                    FileLogger.Log($"[DeathTower] STAGE_CMD(2) stage clear: cid={session.Player.CharacterId} stage={tower.CurrentStage}/{tower.EndStage} isLast={tower.IsLastStage}");
                    await SyncCurrentStageClearMapAsync(session, tower, "tower_stage_cmd");
                    if (tower.IsLastStage)
                    {
                        await SendSettlement(session, tower);
                        return;
                    }
                    break;
                default:
                    FileLogger.Log($"[DeathTower] STAGE_CMD unknown commandType={commandType}: cid={session.Player.CharacterId} bodyHex={BitConverter.ToString(body)}");
                    break;
            }
        }

        private static Task SyncCurrentStageClearMapAsync(EnhancedClientSession session, DeathTowerSession tower, string source)
        {
            var mapId = tower.GetCurrentMapId();
            return Network.Handlers.Dungeon.DungeonClearMapQuestSync.SyncAsync(session, 0, mapId, source);
        }

        // 返城时清除塔状态(由生命周期统一清理路径调用; run 置换后本方法只负责日志与提前摘除)
        public static void ClearTowerState(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run?.Tower != null)
            {
                FileLogger.Log($"[DeathTower] CLEAR: cid={session.Player.CharacterId} wasStage={run.Tower.CurrentStage}");
                run.Tower = null;
            }
        }

        private async Task SendSettlement(EnhancedClientSession session, DeathTowerSession tower)
        {
            var cid = session.Player.CharacterId;
            FileLogger.Log($"[DeathTower] SETTLEMENT begin: cid={cid} dungeon={tower.Config.DungeonId} stages={tower.Config.TotalStages}");

            // NOTI 0x05 DUNGEON_PERMISSION
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0005, new byte[0]));
            FileLogger.Log($"[DeathTower] SENT 0x0005 DUNGEON_PERMISSION (empty)");

            // NOTI 144 排行(空安全版)
            var rankingBody = DeathTowerPacketBuilder.BuildEmptyRanking(tower.Config.DungeonId);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0090, rankingBody));
            FileLogger.Log($"[DeathTower] SENT 0x0090 RANKING: bodyLen={rankingBody.Length}");

            // NOTI 145 奖励(空)
            var rewardBody = DeathTowerPacketBuilder.BuildEmptyReward();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0091, rewardBody));
            FileLogger.Log($"[DeathTower] SENT 0x0091 REWARD: bodyLen={rewardBody.Length}");

            // NOTI 146 EPLP(通关=1)
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0092, DeathTowerPacketBuilder.BuildEplp(true)));
            FileLogger.Log($"[DeathTower] SENT 0x0092 EPLP: cleared=true");

            FileLogger.Log($"[DeathTower] SETTLEMENT complete: cid={cid}");
        }

        private async Task SendStageMap(EnhancedClientSession session, DeathTowerSession tower)
        {
            var mapId = tower.GetCurrentMapId();
            var monsters = DeathTowerMapLoader.LoadStageMonsters(tower);
            if (monsters.Count == 0)
                FileLogger.Log($"[DeathTower] WARNING: stage={tower.CurrentStage} map={mapId} loaded 0 monsters (map may have only [apc random point] or PVF read failed)");

            var body = DeathTowerPacketBuilder.BuildStageMap(tower, monsters);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x008F, body));
            FileLogger.Log($"[DeathTower] SENT 0x008F STAGE_MAP: stage={tower.CurrentStage} map={mapId} monsters={monsters.Count} bodyLen={body.Length}");
        }
    }
}
