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
            var ack = QuestService.HandleAcceptQuest(_connStr, cid, qBody);
            await _sender.SendCmdAckAsync(wireType, ack);
        }

        public async Task HandleGiveupQuestAsync(ushort wireType, byte[] body)
        {
            var qBody = StripEcho(body);
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            var ack = QuestService.HandleGiveupQuest(_connStr, cid, qBody);
            await _sender.SendCmdAckAsync(wireType, ack);
        }

        public async Task HandleSetTriggerAsync(ushort wireType, byte[] body)
        {
            var qBody = StripEcho(body);
            int cid = _sender.CharacterId;
            if (cid <= 0) return;

            byte[] deferredAck;
            if (TryBuildDeferredClearMapSetTriggerAck(cid, qBody, out deferredAck))
            {
                await _sender.SendCmdAckAsync(wireType, deferredAck);
                return;
            }

            var ack = QuestService.HandleSetTrigger(_connStr, cid, qBody);
            await _sender.SendCmdAckAsync(wireType, ack);
        }

        public async Task HandleFinishQuestAsync(ushort wireType, byte[] body)
        {
            var qBody = StripEcho(body);
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            var ack = QuestService.HandleFinishQuest(_connStr, cid, qBody, _assetService);
            await _sender.SendCmdAckAsync(wireType, ack);

            if (ack != null && ack.Length > 1 && ack[0] == 0x01)
            {
                var player = _sender.Player;
                var prevLevel = player.Level;

                if (ack.Length >= 8)
                {
                    uint questExp = BitConverter.ToUInt32(ack, 4);
                    if (questExp > 0)
                    {
                        player.Exp += questExp;

                        while (player.Level < 86 && player.Exp >= (uint)ExpTableProvider.GetLevelThreshold(player.Level))
                            player.Level++;

                        try
                        {
                            var repo = new SqliteCharacterRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                            repo.UpdateLevelAndExp(cid, player.Level, player.Exp);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log($"[QuestManager] PersistLevelAndExp ERROR: {ex.Message}");
                        }
                    }
                }

                ushort remainSp = 0, remainTp = 0;
                try
                {
                    var charRepo = new SqliteCharacterRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                    var rec = charRepo.GetById(cid);
                    var skillRepo = new SqliteCharacterProgressRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                    if (rec != null)
                    {
                        var synced = SkillStateService.LoadAndSync(
                            skillRepo, cid, rec.Job, player.Level, rec.BonusSp, rec.BonusTp, persist: player.Level > prevLevel);
                        var skillTreeIndex = player.Subtype0Tail?.SkillTreeIndex
                            ?? new SqliteSubtype1Repository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath)
                                .LoadSkillTreeIndex(cid)
                            ?? 0;
                        remainSp = SkillStateService.GetPageRemainingSp(synced.Skills, synced.Points, skillTreeIndex == 1 ? 1 : 0);
                        remainTp = (ushort)synced.Points.RemainingTp;
                    }
                }
                catch (Exception ex) { FileLogger.Log($"[QuestManager] SP calc ERROR: {ex.Message}"); }

                await _sender.SendNotiAsync(0x0025,
                    ExpNotificationBuilder.Build(player.Level, player.Exp, remainSp, remainTp));

                if (player.Level > prevLevel)
                {
                    FileLogger.Log($"[QuestManager] LEVEL UP from quest: cid={cid} {prevLevel}→{player.Level} exp={player.Exp}");
                    await SendUserInfoBroadcast(cid);
                }

                if (ack.Length >= 13)
                {
                    int consumedCount = ack[12];
                    int chainTypeOffset = 13 + consumedCount * 7;
                    if (ack.Length >= chainTypeOffset + 1)
                    {
                        int chainType = ack[chainTypeOffset];
                        if (chainType == 1 || chainType == 2)
                        {
                            await SendJobChangeNotification(cid);
                            await SendUserInfoBroadcast(cid);
                        }
                        else if (chainType == 20 && ack.Length >= chainTypeOffset + 2)
                        {
                            int expertJobType = ack[chainTypeOffset + 1];
                            await SendExpertJobChangeNotification(cid, expertJobType);
                            await SendUserInfoBroadcast(cid);
                        }
                        else if (chainType == GameWorld.QuestData.ChainTypeSlotExpansion)
                        {
                            // The ACK completes the quest, but the client opens the visual slot from refreshed subtype1 data.
                            await SendUserInfoBroadcast(cid);
                        }
                    }
                }

                var noti = BuildAcceptedQuestNoti(cid);
                await _sender.SendNotiAsync(0x023F, noti);
                await SendAcceptableQuestListAsync();
            }
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
            int cid = _sender.CharacterId;
            if (cid <= 0) return;

            bool matched = QuestService.SyncMonsterRewardItemProgress(
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

        private bool TryBuildDeferredClearMapSetTriggerAck(int characterId, byte[] qBody, out byte[] ack)
        {
            ack = null;
            if (qBody == null || qBody.Length < 3)
                return false;

            var player = _sender.Player;
            if (player == null || player.CurDungeon <= 0)
                return false;

            ushort questId = BitConverter.ToUInt16(qBody, 0);
            byte triggerType = qBody[2];
            bool isIncrement = qBody.Length >= 4 && qBody[3] != 0;
            if (!ShouldDeferQuestConnectedStartMapSetTrigger(
                    questId,
                    triggerType,
                    isIncrement,
                    player.CurDungeonCleared,
                    player.CurMazeQuestConnected,
                    player.CurMazeStartMapId))
                return false;

            var active = QuestService.LoadActiveQuests(_connStr, characterId);
            var quest = QuestService.FindByQuestId(active, questId);
            if (quest == null || quest.TriggerValue == 0)
                return false;

            ack = BuildSetTriggerAck(questId, quest.TriggerValue);
            FileLogger.Log($"[QuestManager] SET_TRIGGER deferred clear-map start target: cid={characterId} quest={questId} trigger={quest.TriggerValue} dungeon={player.CurDungeon} maze={player.CurMazeIndex} map={player.CurMazeStartMapId}");
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

            var clearedSet = new System.Collections.Generic.HashSet<int>();
            var clearedFlags = new System.Collections.Generic.Dictionary<int, int>();
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connStr))
            {
                conn.Open();
                using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
                    "SELECT slot_index, flag_value FROM character_invisible_falgs WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", cid);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            int si = r.GetInt32(0), fv = r.GetInt32(1);
                            if (fv != 0) { clearedSet.Add(si); clearedFlags[si] = fv; }
                        }
                }
            }
            var questIds = GameWorld.QuestData.ComputeAcceptableQuests(level, job, growType, clearedSet, clearedFlags);
            var w = new Network.GamePacketWriter();
            w.WriteByte((byte)level);
            w.WriteUInt16((ushort)questIds.Count);
            foreach (var qid in questIds)
                w.WriteUInt16(qid);
            await _sender.SendNotiAsync(0x0015, w.ToArray());
        }

        private async Task SendUserInfoBroadcast(int characterId)
        {
            try
            {
                var charRepo = new SqliteCharacterRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var record = charRepo.GetById(characterId);
                var subtype1Repo = new SqliteSubtype1Repository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var addition = subtype1Repo.HasData(characterId) ? subtype1Repo.Load(characterId) : null;
                var skillRepo = new SqliteCharacterProgressRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);

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

        private async Task SendJobChangeNotification(int characterId)
        {
            try
            {
                var charRepo = new SqliteCharacterRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var record = charRepo.GetById(characterId);
                if (record == null) return;

                _sender.Player.GrowType = record.GrowType;

                var w = new Network.GamePacketWriter();
                w.WriteByte(0);
                w.WriteUInt16(1);
                w.WriteUInt16((ushort)record.CharacterId);
                w.WriteDstr(record.Name);
                w.WriteBytes(UserInfoSubtype0Builder.BuildRemainingBytes(record));
                await _sender.SendNotiAsync(0x0002, w.ToArray());

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

                var tail = _sender.Player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
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

        private static byte[] BuildSetTriggerAck(ushort questId, uint triggerValue)
        {
            var w = new Network.GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(questId);
            w.WriteUInt32(triggerValue);
            return w.ToArray();
        }
    }
}
