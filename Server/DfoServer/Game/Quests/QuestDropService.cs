using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Game.Quests
{
    // 击杀怪物/摧毁被动物体后的任务掉落判定与发放。
    // 规则数据来自 QuestDropProvider(PVF), 发放走资产服务事务, 发放后同步寻物任务进度。
    // 原先寄居在副本共享服务里, 拆出归任务域。
    public sealed class QuestDropService
    {
        private const string ProtocolLogName = "GameProtocol";

        private readonly IAssetService _assetService;

        public QuestDropService(IAssetService assetService)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
        }

        public async Task CheckMonsterDrop(EnhancedClientSession session, int monsterCode)
        {
            var run = session.Player.CurrentRun;
            if (run == null || run.DungeonId <= 0 || monsterCode <= 0) return;

            await CheckDrop(session, monsterCode, "monster", activeQuestIds =>
                QuestDropProvider.CheckMonsterDrop(
                    activeQuestIds, run.DungeonId, run.Difficulty, monsterCode));
        }

        public async Task CheckPassiveObjectDrop(EnhancedClientSession session, int objectCode)
        {
            var run = session.Player.CurrentRun;
            if (run == null || run.DungeonId <= 0 || objectCode <= 0) return;

            await CheckDrop(session, objectCode, "passive", activeQuestIds =>
                QuestDropProvider.CheckEnemyDrop(
                    activeQuestIds,
                    run.DungeonId,
                    run.Difficulty,
                    objectCode,
                    QuestDropProvider.EnemyTypePassiveObject));
        }

        private async Task CheckDrop(
            EnhancedClientSession session,
            int sourceCode,
            string sourceName,
            Func<ICollection<int>, List<QuestDropCandidate>> getCandidates)
        {
            HashSet<int> activeQuestIds = null;
            try
            {
                var connStr = SqliteDatabaseBootstrap.Initialize(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var quests = QuestService.LoadActiveQuests(connStr, session.Player.CharacterId);
                if (quests.Count > 0)
                    activeQuestIds = new HashSet<int>(quests.ConvertAll(q => (int)q.QuestId));
            }
            catch { return; }

            var candidates = getCandidates(activeQuestIds);
            if (candidates == null) return;

            var accountId = session.Account?.AccountId ?? 1;
            var grantedItemIds = new HashSet<int>();

            foreach (var candidate in candidates)
            {
                int currentHeld = 0;
                try
                {
                    using (var scope = _assetService.OpenScope(session.Player.CharacterId, accountId))
                        currentHeld = _assetService.CountItem(scope, candidate.ItemId);
                }
                catch { }

                int dropCount = QuestDropProvider.RollDrop(candidate, currentHeld);
                if (dropCount <= 0)
                {
                    if (candidate.MaxStack != -1 && currentHeld >= candidate.MaxStack)
                        FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: skipped maxStack {sourceName}={sourceCode} item={candidate.ItemId} held={currentHeld} max={candidate.MaxStack}");
                    else if (candidate.DropRate >= 100)
                        FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: skipped despite guaranteed rate {sourceName}={sourceCode} item={candidate.ItemId} held={currentHeld} count={candidate.Count}");
                    continue;
                }

                short slot;
                if (!TryPickupItemToInventory(session.Player.CharacterId, accountId, candidate.ItemId, dropCount, out slot))
                {
                    FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: failed to insert {sourceName}={sourceCode} item={candidate.ItemId} x{dropCount} held={currentHeld}");
                    continue;
                }

                // NOTI 14 UPDATE_ITEM_LIST (independent send, 86JP 84B fixed format)
                var w = new GamePacketWriter();
                w.WriteByte(0);                     // updateType = 0
                w.WriteUInt16(1);                   // count = 1
                w.WriteBytes(ItemListUpdateBuilder.BuildRawItemEntry(slot, (uint)candidate.ItemId, (uint)(currentHeld + dropCount)));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, w.ToArray()));

                grantedItemIds.Add(candidate.ItemId);
                FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: {sourceName}={sourceCode} -> item={candidate.ItemId} x{dropCount} slot={slot} (held={currentHeld}->{currentHeld + dropCount})");
            }

            if (grantedItemIds.Count <= 0)
                return;

            if (session.GameSession?.QuestManager == null)
            {
                FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: granted {grantedItemIds.Count} item kinds but quest progress sync skipped because QuestManager is missing");
                return;
            }

            await session.GameSession.QuestManager.SyncItemSeekingQuestProgressAsync(grantedItemIds);
        }

        private bool TryPickupItemToInventory(int characterId, int accountId, int itemTemplateId, int stackCount, out short assignedSlot)
        {
            assignedSlot = -1;
            try
            {
                using (var scope = _assetService.OpenScope(characterId, accountId))
                {
                    var result = _assetService.TryAddItem(scope, itemTemplateId, stackCount, out assignedSlot);
                    if (result) scope.Commit();
                    return result;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestDropService] TryPickupItemToInventory ERROR: {ex.Message}");
                return false;
            }
        }
    }
}
