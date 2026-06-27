using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
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

                ushort spTree0 = 0, spTree1 = 0;
                try
                {
                    var charRepo = new SqliteCharacterRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                    var rec = charRepo.GetById(cid);
                    var skillRepo = new SqliteCharacterProgressRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                    if (rec != null)
                    {
                        var synced = SkillStateService.LoadAndSync(
                            skillRepo, cid, rec.Job, player.Level, rec.BonusSp, rec.BonusTp, persist: player.Level > prevLevel);
                        spTree0 = (ushort)synced.Points.RemainingSp;
                        spTree1 = (ushort)synced.Points.RemainingSp;
                    }
                }
                catch (Exception ex) { FileLogger.Log($"[QuestManager] SP calc ERROR: {ex.Message}"); }

                await _sender.SendNotiAsync(0x0025,
                    ExpNotificationBuilder.Build(player.Level, player.Exp, spTree0, spTree1));

                if (player.Level > prevLevel)
                {
                    FileLogger.Log($"[QuestManager] LEVEL UP from quest: cid={cid} {prevLevel}→{player.Level} exp={player.Exp}");
                    await SendUserInfoBroadcast(cid);
                }

                if (ack.Length >= 14)
                {
                    int chainType = ack[13];
                    if (chainType == 1 || chainType == 2)
                    {
                        await SendJobChangeNotification(cid);
                        await SendUserInfoBroadcast(cid);
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
