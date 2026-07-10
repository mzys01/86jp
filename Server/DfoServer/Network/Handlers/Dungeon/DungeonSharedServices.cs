using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
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
        internal IAssetService AssetService => _assetService;
        internal SqliteSelectCharacterDataSource SelectCharacterDataSource { get; }
        internal IRentalTimeProvider RentalTimeProvider { get; }

        internal Game.ReviveCoin.ReviveCoinService ReviveCoin { get; }
        internal Game.DeathTower.DeathTowerHandler DeathTower { get; }
        internal Game.Quests.QuestDropService QuestDrops { get; }

        // 副本域用到的仓储集中在这里构造一次, 各方法不再就地 new。
        internal SqliteCharacterRepository CharacterRepository { get; }
        internal SqliteSubtype1Repository Subtype1Repository { get; }
        internal SqliteCharacterStateRepository CharacterStateRepository { get; }
        internal SqliteCharacterProgressRepository ProgressRepository { get; }
        internal SqliteSubtype0FieldsRepository Subtype0FieldsRepository { get; }
        internal HonorLevelSyncService HonorLevel { get; }

        internal DungeonSharedServices(
            IAssetService assetService,
            Game.ReviveCoin.ReviveCoinService reviveCoin,
            SqliteCharacterRepository characterRepository,
            SqliteSelectCharacterDataSource selectCharacterDataSource,
            IRentalTimeProvider rentalTimeProvider)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
            ReviveCoin = reviveCoin ?? throw new ArgumentNullException(nameof(reviveCoin));
            CharacterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            SelectCharacterDataSource = selectCharacterDataSource ?? throw new ArgumentNullException(nameof(selectCharacterDataSource));
            RentalTimeProvider = rentalTimeProvider ?? SystemRentalTimeProvider.Instance;
            DeathTower = new Game.DeathTower.DeathTowerHandler();
            QuestDrops = new Game.Quests.QuestDropService(assetService);
            Subtype1Repository = new SqliteSubtype1Repository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            CharacterStateRepository = new SqliteCharacterStateRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            ProgressRepository = new SqliteCharacterProgressRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            Subtype0FieldsRepository = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            HonorLevel = new HonorLevelSyncService(CharacterRepository);
            CardRewards = new Game.Dungeon.CardRewardService(this, assetService);
            Drops = new Game.Dungeon.DropService(assetService);
            EntryCost = new Game.Dungeon.DungeonEntryCostService(assetService);
        }

        internal HonorLevelSummary ResolveHonorLevelForExp(
            EnhancedClientSession session,
            HonorLevelSummary summary = null)
        {
            var tail = session?.Player?.Subtype0Tail;
            if (summary == null && tail != null)
            {
                return new HonorLevelSummary
                {
                    HonorLevel = (byte)Math.Min(byte.MaxValue, tail.ProgressA),
                    HonorExp = tail.ProgressB,
                };
            }

            summary = summary ?? HonorLevel.LoadSummary(session?.Account?.AccountId ?? 0);
            if (session?.Player != null)
            {
                tail = tail ?? new UserInfoMinimumTailSnapshot();
                HonorLevelDataProvider.ApplyToSubtype0Tail(tail, summary);
                session.Player.Subtype0Tail = tail;
            }

            return summary;
        }

        internal Game.Dungeon.CardRewardService CardRewards { get; }
        internal Game.Dungeon.DropService Drops { get; }
        internal Game.Dungeon.DungeonEntryCostService EntryCost { get; }

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

        internal async Task SendUserInfoBroadcast(
            EnhancedClientSession session)
        {
            try
            {
                int cid = session.Player.CharacterId;
                var record = CharacterRepository.GetById(cid);
                var addition = Subtype1Repository.HasData(cid) ? Subtype1Repository.Load(cid) : null;
                if (record != null && addition != null)
                {
                    var accountId = session.Account?.AccountId ?? record.AccountId;
                    var accountCharacters = CharacterRepository.ListByAccount(accountId);
                    AdventureGroupUserInfoSynchronizer.ApplyToUserInfoAddition(addition, accountCharacters);
                    HonorLevel.ApplyToUserInfoAddition(addition, accountId, accountCharacters);
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

                record.Subtype0Tail = Subtype0FieldsRepository.Load(cid) ?? new UserInfoMinimumTailSnapshot();
                var accountId = session.Account?.AccountId ?? record.AccountId;
                var accountCharacters = CharacterRepository.ListByAccount(accountId);
                HonorLevel.ApplyToSubtype0Tail(record.Subtype0Tail, accountId, accountCharacters);

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00, 0x0002, UserInfoSubtype0Builder.BuildNotificationBody(record)));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] SendUserInfoSubtype0Broadcast ERROR: {ex.Message}");
            }
        }

    }

}
