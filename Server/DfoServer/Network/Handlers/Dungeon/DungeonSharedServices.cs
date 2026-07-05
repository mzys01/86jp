using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonSharedServices
    {
        internal const string ProtocolLogName = "GameProtocol";

        private readonly IAssetService _assetService;

        internal Game.ReviveCoin.ReviveCoinService ReviveCoin { get; }
        internal Game.DeathTower.DeathTowerHandler DeathTower { get; }
        internal Game.Quests.QuestDropService QuestDrops { get; }

        // 副本域用到的仓储集中在这里构造一次, 各方法不再就地 new。
        internal SqliteCharacterRepository CharacterRepository { get; }
        internal SqliteSubtype1Repository Subtype1Repository { get; }
        internal SqliteCharacterStateRepository CharacterStateRepository { get; }
        internal SqliteCharacterProgressRepository ProgressRepository { get; }
        internal SqliteSubtype0FieldsRepository Subtype0FieldsRepository { get; }

        internal DungeonSharedServices(
            IAssetService assetService,
            Game.ReviveCoin.ReviveCoinService reviveCoin,
            SqliteCharacterRepository characterRepository)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
            ReviveCoin = reviveCoin ?? throw new ArgumentNullException(nameof(reviveCoin));
            CharacterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            DeathTower = new Game.DeathTower.DeathTowerHandler();
            QuestDrops = new Game.Quests.QuestDropService(assetService);
            Subtype1Repository = new SqliteSubtype1Repository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            CharacterStateRepository = new SqliteCharacterStateRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            ProgressRepository = new SqliteCharacterProgressRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            Subtype0FieldsRepository = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
        }

        internal static DungeonRoomProgress GetCurrentRoomProgress(EnhancedClientSession session)
            => GetRoomProgress(session, session?.Player?.CurrentRun?.RoomKilledSeqIds);

        internal static DungeonRoomProgress GetRoomProgress(
            EnhancedClientSession session,
            ISet<ushort> killedSeqIds)
        {
            var run = session?.Player?.CurrentRun;
            var monsters = run?.RoomMonsters ?? Array.Empty<DungeonData.MonsterSumInfo>();
            var killed = killedSeqIds ?? new HashSet<ushort>();
            var startSeq = run?.RoomStartSequence ?? 0;

            int trackable = 0;
            int killedTrackable = 0;
            int remaining = 0;
            int blocking = 0;
            int blockingRemaining = 0;
            int apc = 0;
            int normal = 0;
            int killedNormal = 0;

            for (var i = 0; i < monsters.Count; i++)
            {
                var monster = monsters[i];
                if (monster.Type == 9)
                    continue;

                trackable++;
                if (monster.Type >= 5)
                    apc++;
                else
                    normal++;

                if (monster.IsBlocking)
                    blocking++;

                var seqId = (ushort)(startSeq + i);
                if (killed.Contains(seqId))
                {
                    killedTrackable++;
                    if (monster.Type < 5)
                        killedNormal++;
                    continue;
                }

                remaining++;
                if (monster.IsBlocking)
                    blockingRemaining++;
            }

            return new DungeonRoomProgress(
                trackable,
                killedTrackable,
                remaining,
                blocking,
                blockingRemaining,
                apc,
                normal,
                killedNormal);
        }

        internal static bool ShouldClearAfterApcDialog(DungeonRoomProgress progress)
            => progress.ApcCount > 0
                && progress.KilledNormalCount >= progress.NormalCount
                && progress.BlockingRemainingCount == 0;

        internal readonly struct DungeonRoomProgress
        {
            internal DungeonRoomProgress(
                int trackableCount,
                int killedTrackableCount,
                int remainingCount,
                int blockingCount,
                int blockingRemainingCount,
                int apcCount,
                int normalCount,
                int killedNormalCount)
            {
                TrackableCount = trackableCount;
                KilledTrackableCount = killedTrackableCount;
                RemainingCount = remainingCount;
                BlockingCount = blockingCount;
                BlockingRemainingCount = blockingRemainingCount;
                ApcCount = apcCount;
                NormalCount = normalCount;
                KilledNormalCount = killedNormalCount;
            }

            internal int TrackableCount { get; }
            internal int KilledTrackableCount { get; }
            internal int RemainingCount { get; }
            internal int BlockingCount { get; }
            internal int BlockingRemainingCount { get; }
            internal int ApcCount { get; }
            internal int NormalCount { get; }
            internal int KilledNormalCount { get; }
            internal bool RoomPassable => BlockingRemainingCount == 0;
        }

        internal void PersistLevelAndExp(int characterId, byte level, uint exp)
        {
            try
            {
                CharacterProgressService.PersistLevelAndExp(characterId, level, exp);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] PersistLevelAndExp ERROR: {ex.Message}");
            }
        }

        internal void PersistGold(int characterId, int accountId, int goldGained)
        {
            if (goldGained <= 0) return;
            try
            {
                using (var scope = _assetService.OpenScope(characterId, accountId))
                {
                    _assetService.GrantGold(scope, goldGained);
                    scope.Commit();
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] PersistGold ERROR: {ex.Message}");
            }
        }

        internal int ReadGold(int characterId, int accountId)
        {
            try
            {
                using (var scope = _assetService.OpenScope(characterId, accountId))
                {
                    var wallet = _assetService.LoadWallet(scope);
                    return wallet.Gold;
                }
            }
            catch (Exception ex) { FileLogger.Log($"[DungeonHandler] ReadGold ERROR: cid={characterId}, returning 0: {ex.Message}"); return 0; }
        }

        private static readonly Dictionary<int, int> GoldBonusEquipments = new()
        {
            {100320775, 12},
            {24191, 10},
            {100341606, 30},
            {100331240, 10},
            {100331319, 3},
            {26626, 3},
            {26627, 4},
            {26341, 3},
            {26342, 4},
            {26115, 3},
            {104000181, 3},
            {101020286, 3},
            {101020526, 3},
            {109000133, 3}
        };

        internal int GetEquippedGoldBonus(int characterId)
        {
            var totalBonus = 0;
            try
            {
                using (var scope = _assetService.OpenScope(characterId, 0))
                {
                    using var cmd = scope.Connection.CreateCommand();
                    cmd.CommandText = "SELECT item_id FROM character_equipped_entries WHERE character_id = @cid";
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var itemId = reader.GetInt32(0);
                        if (GoldBonusEquipments.TryGetValue(itemId, out var bonus))
                            totalBonus += bonus;
                    }
                }
            }
            catch (Exception ex) { FileLogger.Log($"[DungeonHandler] GetEquippedGoldBonus ERROR: cid={characterId}, bonus treated as {totalBonus}: {ex.Message}"); }
            return totalBonus;
        }

        internal bool TrySpendGold(int characterId, int accountId, int goldCost, out int currentGold, out int updatedGold)
        {
            currentGold = 0;
            updatedGold = 0;
            if (characterId <= 0 || goldCost <= 0)
                return false;

            try
            {
                using (var scope = _assetService.OpenScope(characterId, accountId))
                {
                    var wallet = _assetService.LoadWallet(scope);
                    currentGold = wallet.Gold;
                    updatedGold = wallet.Gold;
                    if (!_assetService.TrySpendGold(scope, goldCost))
                        return false;

                    updatedGold = wallet.Gold - goldCost;
                    scope.Commit();
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] TrySpendGold ERROR: {ex.Message}");
                return false;
            }
        }

        internal bool TryConsumeHellPartyTicket(
            EnhancedClientSession session,
            WorldMapArea area,
            int dungeonMinLevel,
            out HellPartyTicketConsumeResult result)
        {
            result = new HellPartyTicketConsumeResult();
            var characterId = session.Player?.CharacterId ?? 0;
            var accountId = session.Account?.AccountId ?? 1;
            if (characterId <= 0)
            {
                result.Reason = "invalid character";
                return false;
            }

            if (area == null)
            {
                result.Reason = "worldmap area missing";
                return false;
            }

            if (!area.HellDungeon)
            {
                result.Reason = "area is not hell dungeon";
                return false;
            }

            if (!CheckHellQuestRequirement(characterId, area, out var missingQuestId))
            {
                result.Reason = $"hell quest not cleared quest={missingQuestId}";
                return false;
            }

            try
            {
                using (var scope = _assetService.OpenScope(characterId, accountId))
                {
                    foreach (var ticket in area.HellFreePassItems)
                    {
                        if (ticket.ItemId <= 0 || ticket.Count <= 0)
                            continue;

                        if (_assetService.CountItem(scope, ticket.ItemId) < ticket.Count)
                            continue;

                        if (_assetService.TryRemoveItem(scope, ticket.ItemId, ticket.Count, out var slot, out var remaining))
                        {
                            scope.Commit();
                            result.Success = true;
                            result.IsFreePass = true;
                            result.Updates.Add(new HellPartyTicketItemUpdate
                            {
                                ItemId = ticket.ItemId,
                                Count = ticket.Count,
                                SlotIndex = slot,
                                RemainingCount = remaining,
                            });
                            return true;
                        }
                    }

                    var normalNeedCount = WorldMap.GetHellNormalTicketNeedCount(dungeonMinLevel);
                    if (normalNeedCount <= 0)
                    {
                        result.Reason = $"dungeon min level too low minLevel={dungeonMinLevel}";
                        return false;
                    }

                    var normalTicketItemIds = area.HellNormalTicketItemIds;
                    if (normalTicketItemIds.Count == 0)
                    {
                        result.Reason = "normal ticket item missing";
                        return false;
                    }

                    var selectedNormalTicketItemId = 0;
                    foreach (var itemId in normalTicketItemIds)
                    {
                        if (itemId > 0 && _assetService.CountItem(scope, itemId) >= normalNeedCount)
                        {
                            selectedNormalTicketItemId = itemId;
                            break;
                        }
                    }

                    if (selectedNormalTicketItemId <= 0)
                    {
                        result.Reason = $"ticket missing normalNeed={normalNeedCount}";
                        return false;
                    }

                    if (_assetService.TryRemoveItem(scope, selectedNormalTicketItemId, normalNeedCount, out var normalSlot, out var normalRemaining))
                    {
                        result.Success = true;
                        result.IsFreePass = false;
                        result.Updates.Add(new HellPartyTicketItemUpdate
                        {
                            ItemId = selectedNormalTicketItemId,
                            Count = normalNeedCount,
                            SlotIndex = normalSlot,
                            RemainingCount = normalRemaining,
                        });
                    }
                    else
                    {
                        result.Reason = $"ticket delete failed item={selectedNormalTicketItemId} normalNeed={normalNeedCount}";
                        return false;
                    }

                    scope.Commit();
                    return result.Updates.Count > 0;
                }
            }
            catch (Exception ex)
            {
                result.Reason = ex.Message;
                FileLogger.Log($"[DungeonHandler] TryConsumeHellPartyTicket ERROR: {ex.Message}");
                return false;
            }
        }

        private static bool CheckHellQuestRequirement(int characterId, WorldMapArea area, out int missingQuestId)
        {
            missingQuestId = 0;
            if (area.HellQuestIds.Count == 0)
                return true;

            var connStr = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            foreach (var questId in area.HellQuestIds)
            {
                if (questId <= 0)
                    continue;

                if (questId > ushort.MaxValue || !QuestService.IsQuestCleared(connStr, characterId, (ushort)questId))
                {
                    missingQuestId = questId;
                    return false;
                }
            }

            return true;
        }

        internal async Task UpdateDungeonPermission(EnhancedClientSession session, int dungeonId, int difficulty)
        {
            if (dungeonId <= 0) return;
            int characterId = session.Player.CharacterId;
            int maxClearState = GameWorld.Dungeon.GetMaxDifficultyCount(dungeonId) - 1;
            if (maxClearState <= 0) return;
            byte newClearState = (byte)(difficulty + 1);
            if (newClearState < 1) newClearState = 1;
            if (newClearState > maxClearState) newClearState = (byte)maxClearState;

            try
            {
                if (!CharacterStateRepository.UpsertDungeonPermission(characterId, dungeonId, newClearState))
                    return;

                var w = new GamePacketWriter();
                w.WriteUInt16(1);
                w.WriteUInt16((ushort)dungeonId);
                w.WriteByte(newClearState);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0005, w.ToArray()));
                FileLogger.Log($"[DungeonHandler] DungeonPermission: dungeon={dungeonId} diff={difficulty} -> clearState={newClearState}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] DungeonPermission ERROR: {ex.Message}");
            }
        }

        internal (SkillInfoSnapshot Skills, SkillPointState Points) LoadSyncedSkillState(
            int characterId,
            byte currentLevel,
            bool persist = false)
        {
            var record = CharacterRepository.GetById(characterId);

            if (record == null)
                return (ProgressRepository.LoadSkills(characterId), null);

            return SkillStateService.LoadAndSync(
                ProgressRepository,
                characterId,
                record.Job,
                currentLevel > 0 ? currentLevel : record.Level,
                record.BonusSp,
                record.BonusTp,
                persist: persist);
        }

        // 经验入口共用: 升级/结算后的 SP/TP 剩余点计算, 失败按 0 发并留日志。
        internal (ushort RemainSp, ushort RemainTp) GetRemainingSpTp(EnhancedClientSession session, bool persist, string logTag)
        {
            try
            {
                var synced = LoadSyncedSkillState(session.Player.CharacterId, session.Player.Level, persist: persist);
                if (synced.Points != null)
                {
                    var pageIndex = session.Player.Subtype0Tail?.SkillTreeIndex == 1 ? 1 : 0;
                    return (SkillStateService.GetPageRemainingSp(synced.Skills, synced.Points, pageIndex),
                            (ushort)synced.Points.RemainingTp);
                }
            }
            catch (Exception ex) { FileLogger.Log($"[DungeonHandler] {logTag} ERROR: skill state sync failed, SP/TP sent as 0: {ex.Message}"); }
            return (0, 0);
        }

        // 副本内升级的后续通知: 刷新可接任务列表 + 补属性(subtype1)。
        // 绝不发角色状态包(subtype0) -- 它会打乱客户端的副本内角色状态,
        // 实测导致清房后无法进下一个门。
        internal async Task SendInDungeonLevelUpFollowups(EnhancedClientSession session)
        {
            await SendQuestListRefresh(session);
            await SendUserInfoBroadcast(session);
        }

        internal async Task SendQuestListRefresh(EnhancedClientSession session)
        {
            try
            {
                var rec = CharacterRepository.GetById(session.Player.CharacterId);
                if (rec == null) return;

                var clearedFlags = new Game.Quests.QuestRepository(
                    SqliteDatabaseBootstrap.BuildConnectionString(ServerPaths.DatabasePath))
                    .LoadClearedFlags(session.Player.CharacterId);

                var body = Builders.QuestListBodyBuilder.BuildBody(
                    session.Player.Level, rec.Job, rec.GrowType, clearedFlags);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0015, body));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] SendQuestListRefresh ERROR: {ex.Message}");
            }
        }

        internal async Task SendUserInfoBroadcast(EnhancedClientSession session)
        {
            try
            {
                int cid = session.Player.CharacterId;
                var record = CharacterRepository.GetById(cid);
                var addition = Subtype1Repository.HasData(cid) ? Subtype1Repository.Load(cid) : null;
                if (record != null && addition != null)
                {
                    var skillSnap = LoadSyncedSkillState(cid, session.Player.Level).Skills;
                    var w = new GamePacketWriter();
                    w.WriteByte(1);
                    w.WriteUInt16(1);
                    w.WriteUInt16((ushort)record.CharacterId);
                    w.WriteBytes(UserInfoSubtype1Builder.BuildFromSnapshot(addition, skillSnap));
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, w.ToArray()));
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] SendUserInfoBroadcast ERROR: {ex.Message}");
            }
        }

        internal async Task SendUserInfoSubtype0Broadcast(EnhancedClientSession session)
        {
            try
            {
                int cid = session.Player.CharacterId;
                var record = CharacterRepository.GetById(cid);
                if (record == null)
                    return;

                record.Subtype0Tail = Subtype0FieldsRepository.Load(cid);

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00, 0x0002, UserInfoSubtype0Builder.BuildNotificationBody(record)));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] SendUserInfoSubtype0Broadcast ERROR: {ex.Message}");
            }
        }

        internal bool TryPickupItemToInventory(int characterId, int accountId, int itemTemplateId, int stackCount, out short assignedSlot)
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
                FileLogger.Log($"[DungeonHandler] TryPickupItemToInventory ERROR: {ex.Message}");
                return false;
            }
        }

    }

    internal sealed class HellPartyTicketConsumeResult
    {
        public bool Success { get; set; }
        public bool IsFreePass { get; set; }
        public string Reason { get; set; }
        public List<HellPartyTicketItemUpdate> Updates { get; } = new List<HellPartyTicketItemUpdate>();
    }

    internal sealed class HellPartyTicketItemUpdate
    {
        public int ItemId { get; set; }
        public int Count { get; set; }
        public short SlotIndex { get; set; }
        public int RemainingCount { get; set; }
    }
}
