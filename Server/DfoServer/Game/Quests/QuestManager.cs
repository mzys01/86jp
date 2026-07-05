using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Game.Quests
{
    public sealed class QuestManager
    {
        private readonly ISessionPacketSender _sender;
        private readonly string _connStr;
        private readonly IAssetService _assetService;

        public QuestManager(ISessionPacketSender sender, string connStr, IAssetService assetService)
        {
            _sender = sender;
            _connStr = connStr;
            _assetService = assetService;
        }

        private static byte[] StripEcho(byte[] body)
        {
            if (body == null || body.Length <= 2) return body;
            var stripped = new byte[body.Length - 2];
            Buffer.BlockCopy(body, 2, stripped, 0, stripped.Length);
            return stripped;
        }

        public async Task HandleAcceptQuestAsync(ushort wireType, byte[] body)
        {
            var qBody = StripEcho(body);
            FileLogger.Log($"[GameProtocol] ACCEPT_QUEST payload: {(qBody != null ? BitConverter.ToString(qBody) : "null")} ({qBody?.Length ?? 0}B)");
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            var result = QuestService.HandleAcceptQuest(_connStr, cid, qBody, _assetService, _sender.AccountId);
            await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildAccept(result));
        }

        public async Task HandleGiveupQuestAsync(ushort wireType, byte[] body)
        {
            var qBody = StripEcho(body);
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            var result = QuestService.HandleGiveupQuest(_connStr, cid, qBody);
            await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildGiveup(result));
        }

        public async Task HandleSetTriggerAsync(ushort wireType, byte[] body)
        {
            var qBody = StripEcho(body);
            int cid = _sender.CharacterId;
            if (cid <= 0) return;

            QuestSetTriggerResult deferred;
            if (TryBuildDeferredClearMapSetTrigger(cid, qBody, out deferred))
            {
                await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildSetTrigger(deferred));
                return;
            }

            var result = QuestService.HandleSetTrigger(_connStr, cid, qBody);
            await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildSetTrigger(result));
        }

        public async Task HandleFinishQuestAsync(ushort wireType, byte[] body)
        {
            var qBody = StripEcho(body);
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            var result = QuestService.HandleFinishQuest(_connStr, cid, qBody, _assetService);
            await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildFinish(result));

            if (!result.Success)
                return;

            var player = _sender.Player;
            var prevLevel = player.Level;

            // 经验/等级已在完成事务内落库(见 QuestService), 这里只同步会话内存。
            if (result.Exp > 0)
            {
                player.Exp = result.NewExp;
                player.Level = result.NewLevel;
            }

            ushort remainSp = 0, remainTp = 0;
            try
            {
                CharacterRecord rec;
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connStr))
                {
                    conn.Open();
                    rec = SqliteCharacterRepository.LoadById(conn, cid);
                }
                var skillRepo = SqliteCharacterProgressRepository.FromConnectionString(_connStr);
                if (rec != null)
                {
                    var synced = SkillStateService.LoadAndSync(
                        skillRepo, cid, rec.Job, player.Level, rec.BonusSp, rec.BonusTp, persist: player.Level > prevLevel);
                    var skillTreeIndex = player.Subtype0Tail?.SkillTreeIndex
                        ?? SqliteSubtype1Repository.FromConnectionString(_connStr).LoadSkillTreeIndex(cid)
                        ?? 0;
                    remainSp = SkillStateService.GetPageRemainingSp(synced.Skills, synced.Points, skillTreeIndex == 1 ? 1 : 0);
                    remainTp = (ushort)synced.Points.RemainingTp;
                }
            }
            catch (Exception ex) { FileLogger.Log($"[QuestManager] SP calc ERROR: {ex.Message}"); }

            var leveledUp = player.Level > prevLevel;
            var inDungeon = player.CurrentRun != null;
            // 城镇内升级: 先推角色状态(subtype0)+属性(subtype1)再发经验包, 面板即时刷新。
            // 副本内升级绝不能发角色状态包(subtype0) -- 它会打乱客户端的副本内角色状态,
            // 实测导致清房后无法进下一个门; 副本内沿用旧时序(经验包之后只补属性)。
            if (leveledUp && !inDungeon)
            {
                await SendUserInfoSubtype0Broadcast(cid, "LevelUp");
                await SendUserInfoBroadcast(cid);
            }

            await _sender.SendNotiAsync(0x0025,
                ExpNotificationBuilder.Build(player.Level, player.Exp, remainSp, remainTp));

            if (leveledUp)
            {
                FileLogger.Log($"[QuestManager] LEVEL UP from quest: cid={cid} {prevLevel}->{player.Level} exp={player.Exp} inDungeon={inDungeon}");
                if (inDungeon)
                    await SendUserInfoBroadcast(cid);
            }

            if (result.ChainType == 1 || result.ChainType == 2)
            {
                await SendJobChangeNotification(cid);
                await SendUserInfoBroadcast(cid);
            }
            else if (result.ChainType == 20)
            {
                await SendExpertJobChangeNotification(cid, result.GrowNumber);
                await SendUserInfoBroadcast(cid);
            }
            else if (result.ChainType == GameWorld.QuestData.ChainTypeSlotExpansion)
            {
                // The ACK completes the quest, but the client opens the visual slot from refreshed subtype1 data.
                await SendUserInfoBroadcast(cid);
            }

            var noti = BuildAcceptedQuestNoti(cid);
            await _sender.SendNotiAsync(0x023F, noti);
            await SendAcceptableQuestListAsync();
        }

        public async Task SendActiveQuestListAsync()
        {
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            var noti = BuildAcceptedQuestNoti(cid);
            await _sender.SendNotiAsync(0x023F, noti);
        }

        public async Task SyncMonsterRewardItemProgressAsync(ICollection<int> itemFilter)
        {
            await SyncItemSeekingQuestProgressAsync(itemFilter);
        }

        public async Task SyncItemSeekingQuestProgressAsync(ICollection<int> itemFilter)
        {
            int cid = _sender.CharacterId;
            if (cid <= 0) return;

            bool matched = QuestService.SyncItemSeekingQuestProgress(
                _connStr, cid, _assetService, _sender.AccountId, itemFilter);
            if (!matched)
                return;

            var noti = BuildAcceptedQuestNoti(cid);
            await _sender.SendNotiAsync(0x023F, noti);
        }

        public async Task SyncClearMapQuestProgressAsync(int dungeonId, int mapId)
        {
            int cid = _sender.CharacterId;
            if (cid <= 0) return;

            bool changed = QuestService.SyncClearMapQuestProgress(
                _connStr, cid, dungeonId, mapId);
            if (!changed)
                return;

            var noti = BuildAcceptedQuestNoti(cid);
            await _sender.SendNotiAsync(0x023F, noti);
        }

        private bool TryBuildDeferredClearMapSetTrigger(int characterId, byte[] qBody, out QuestSetTriggerResult result)
        {
            result = null;
            if (qBody == null || qBody.Length < 3)
                return false;

            var run = _sender.Player?.CurrentRun;
            if (run == null || run.DungeonId <= 0)
                return false;

            ushort questId = BitConverter.ToUInt16(qBody, 0);
            byte triggerType = qBody[2];
            bool isIncrement = qBody.Length >= 4 && qBody[3] != 0;
            if (!ShouldDeferQuestConnectedStartMapSetTrigger(
                    questId,
                    triggerType,
                    isIncrement,
                    run.Phase >= Dungeon.DungeonRunPhase.Cleared,
                    run.MazeQuestConnected,
                    run.MazeStartMapId))
                return false;

            var active = QuestService.LoadActiveQuests(_connStr, characterId);
            var quest = QuestService.FindByQuestId(active, questId);
            if (quest == null || quest.TriggerValue == 0)
                return false;

            result = new QuestSetTriggerResult { QuestId = questId, TriggerValue = quest.TriggerValue };
            FileLogger.Log($"[QuestManager] SET_TRIGGER deferred clear-map start target: cid={characterId} quest={questId} trigger={quest.TriggerValue} dungeon={run.DungeonId} maze={run.MazeIndex} map={run.MazeStartMapId}");
            return true;
        }

        internal static bool ShouldDeferQuestConnectedStartMapSetTrigger(
            ushort questId,
            byte triggerType,
            bool isIncrement,
            bool dungeonCleared,
            bool mazeQuestConnected,
            int mazeStartMapId)
        {
            if (questId == 0 || dungeonCleared || !mazeQuestConnected || mazeStartMapId <= 0)
                return false;
            if (triggerType != 0 || isIncrement)
                return false;

            return ShouldDeferQuestConnectedStartMapQuest(questId, mazeStartMapId);
        }

        internal bool HasDeferredQuestConnectedStartMapClearQuest(int characterId, int mazeStartMapId)
        {
            if (characterId <= 0 || mazeStartMapId <= 0)
                return false;

            var active = QuestService.LoadActiveQuests(_connStr, characterId);
            foreach (var quest in active)
            {
                if (quest.TriggerValue == 0)
                    continue;
                if (ShouldDeferQuestConnectedStartMapQuest(quest.QuestId, mazeStartMapId))
                    return true;
            }

            return false;
        }

        private static bool ShouldDeferQuestConnectedStartMapQuest(ushort questId, int mazeStartMapId)
        {
            var qst = GameWorld.QuestData.GetQuestFile(questId);
            if (qst == null || qst.CompleteNpcIndex < 0)
                return false;

            return GameWorld.QuestData.MatchesClearMapTarget(qst, dungeonId: 0, mapId: mazeStartMapId);
        }

        private async Task SendAcceptableQuestListAsync()
        {
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            var character = _sender.Player;
            int level = character != null ? character.Level : 1;
            int job = character != null ? character.Job : 0;
            int growType = character != null ? character.GrowType : -1;

            var clearedFlags = new QuestRepository(_connStr).LoadClearedFlags(cid);
            await _sender.SendNotiAsync(0x0015, QuestListBodyBuilder.BuildBody(level, job, growType, clearedFlags));
        }

        private async Task SendUserInfoBroadcast(int characterId)
        {
            try
            {
                CharacterRecord record;
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connStr))
                {
                    conn.Open();
                    record = SqliteCharacterRepository.LoadById(conn, characterId);
                }
                var addition = SqliteSubtype1Repository.FromConnectionString(_connStr).Load(characterId);
                var skillRepo = SqliteCharacterProgressRepository.FromConnectionString(_connStr);

                if (record != null && addition != null)
                {
                    var synced = SkillStateService.LoadAndSync(
                        skillRepo, characterId, record.Job, record.Level, record.BonusSp, record.BonusTp, persist: false);
                    var w = new Network.GamePacketWriter();
                    w.WriteByte(1);
                    w.WriteUInt16(1);
                    w.WriteUInt16((ushort)record.CharacterId);
                    w.WriteBytes(UserInfoSubtype1Builder.BuildFromSnapshot(addition, synced.Skills));
                    await _sender.SendNotiAsync(0x0002, w.ToArray());
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestManager] SendUserInfoBroadcast ERROR: {ex.Message}");
            }
        }

        private async Task SendUserInfoSubtype0Broadcast(int characterId, string reason)
        {
            try
            {
                byte[] body;
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connStr))
                {
                    conn.Open();
                    var record = SqliteCharacterRepository.LoadById(conn, characterId);
                    if (record == null) return;
                    record.Subtype0Tail = SqliteSubtype0FieldsRepository.Load(conn, characterId);
                    body = UserInfoSubtype0Builder.BuildNotificationBody(record);
                }

                await _sender.SendNotiAsync(0x0002, body);
                FileLogger.Log($"[QuestManager] {reason} NOTI 2 subtype0 sent: cid={characterId}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestManager] SendUserInfoSubtype0Broadcast ERROR: {ex.Message}");
            }
        }

        private async Task SendJobChangeNotification(int characterId)
        {
            try
            {
                var charRepo = new SqliteCharacterRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var record = charRepo.GetById(characterId);
                if (record == null) return;
                record.Subtype0Tail = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath)
                    .Load(characterId);

                _sender.Player.GrowType = record.GrowType;

                await _sender.SendNotiAsync(0x0002, UserInfoSubtype0Builder.BuildNotificationBody(record));

                FileLogger.Log($"[QuestManager] JobChange NOTI 2 subtype0 sent: cid={characterId} growType=0x{record.GrowType:X2}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestManager] SendJobChangeNotification ERROR: {ex.Message}");
            }
        }

        private async Task SendExpertJobChangeNotification(int characterId, int expertJobType)
        {
            try
            {
                var charRepo = new SqliteCharacterRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var record = charRepo.GetById(characterId);
                if (record == null || _sender.Player == null) return;

                var tail = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath)
                    .Load(characterId)
                    ?? _sender.Player.Subtype0Tail
                    ?? new UserInfoMinimumTailSnapshot();
                tail.ExpertJobType = (byte)expertJobType;
                _sender.Player.Subtype0Tail = tail;
                record.Subtype0Tail = tail;

                // NOTI 0x00CD ExpertJobInfo
                var ejw = new Network.GamePacketWriter();
                ejw.WriteByte(1);          // State0
                ejw.WriteByte(1);          // Mode
                ejw.WriteByte(1);          // Count
                ejw.WriteInt32(expertJobType);
                await _sender.SendNotiAsync(0x00CD, ejw.ToArray());

                // NOTI 0x0002 subtype 0 USERINFO Minimum
                var w = new Network.GamePacketWriter();
                w.WriteByte(0);
                w.WriteUInt16(1);
                w.WriteUInt16((ushort)record.CharacterId);
                w.WriteDstr(record.Name);
                w.WriteBytes(UserInfoSubtype0Builder.BuildRemainingBytes(record));
                await _sender.SendNotiAsync(0x0002, w.ToArray());

                FileLogger.Log($"[QuestManager] ExpertJobChange NOTI sent: cid={characterId} expertJobType={expertJobType}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestManager] SendExpertJobChangeNotification ERROR: {ex.Message}");
            }
        }

        private byte[] BuildAcceptedQuestNoti(int characterId)
        {
            var active = QuestService.LoadActiveQuests(_connStr, characterId);
            var w = new Network.GamePacketWriter();
            w.WriteUInt32((uint)active.Count);
            foreach (var q in active)
            {
                w.WriteUInt16(q.QuestId);
                w.WriteUInt32(q.TriggerValue);
            }
            return w.ToArray();
        }
    }
}
