using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Game.Accounts
{
    public sealed class GrowthCapsuleSyncService
    {
        private readonly ICharacterRepository _characterRepository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly GrowthCapsuleProgressRepository _growthCapsuleRepository;
        private readonly SqliteCharacterProgressRepository _progressRepository;
        private readonly SqliteSubtype1Repository _subtype1Repository;

        public GrowthCapsuleSyncService(ICharacterRepository characterRepository)
            : this(characterRepository, ServerPaths.DatabasePath, ServerPaths.SchemaFilePath)
        {
        }

        public GrowthCapsuleSyncService(
            ICharacterRepository characterRepository,
            string databasePath,
            string schemaFilePath)
        {
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _honorLevel = new HonorLevelSyncService(characterRepository, databasePath, schemaFilePath);
            _growthCapsuleRepository = new GrowthCapsuleProgressRepository(databasePath, schemaFilePath);
            _progressRepository = new SqliteCharacterProgressRepository(databasePath, schemaFilePath);
            _subtype1Repository = new SqliteSubtype1Repository(databasePath, schemaFilePath);
        }

        public async Task SendExpProgressAsync(
            EnhancedClientSession session,
            string reason,
            GrowthCapsuleSummary growthCapsule = null,
            HonorLevelSummary honor = null)
        {
            if (session?.Player == null)
                return;

            if (session.Player.Level < ExpTableProvider.MaxLevel)
                return;

            var accountId = session.Account?.AccountId ?? 0;
            if (accountId <= 0 || session.Player.CharacterId <= 0)
                return;

            honor = honor ?? _honorLevel.LoadSummary(accountId);
            growthCapsule = growthCapsule ?? _growthCapsuleRepository.LoadSummary(accountId);
            ResolveRemainingSkillPoints(session, out var remainSp, out var remainTp);

            var displayExp = GrowthCapsuleDataProvider.GetDisplayProgress(
                session.Player.Level, growthCapsule);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0025,
                ExpNotificationBuilder.Build(
                    session.Player.Level,
                    session.Player.Exp,
                    remainSp,
                    remainTp,
                    honor,
                    growthCapsuleExp: displayExp)));
            FileLogger.Log($"[GameProtocol] GROWTH_CAPSULE_SYNC {reason}: account={accountId} cid={session.Player.CharacterId} level={session.Player.Level} total={growthCapsule.TotalExp} display={displayExp} claimable={growthCapsule.TotalExp >= growthCapsule.RequiredExp} honorLevel={honor.HonorLevel} honorExp={honor.HonorExp}");
        }

        private void ResolveRemainingSkillPoints(
            EnhancedClientSession session,
            out ushort remainSp,
            out ushort remainTp)
        {
            remainSp = 0;
            remainTp = 0;
            try
            {
                var record = _characterRepository.GetById(session.Player.CharacterId);
                if (record == null)
                    return;

                var synced = SkillStateService.LoadAndSync(
                    _progressRepository,
                    record.CharacterId,
                    record.Job,
                    session.Player.Level,
                    record.BonusSp,
                    record.BonusTp,
                    persist: false);
                if (synced.Points == null)
                    return;

                var skillTreeIndex = session.Player.Subtype0Tail?.SkillTreeIndex
                    ?? _subtype1Repository.LoadSkillTreeIndex(record.CharacterId)
                    ?? 0;
                remainSp = SkillStateService.GetPageRemainingSp(
                    synced.Skills, synced.Points, skillTreeIndex == 1 ? 1 : 0);
                remainTp = (ushort)synced.Points.RemainingTp;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GameProtocol] GROWTH_CAPSULE_SYNC skill points failed: {ex.Message}");
            }
        }
    }
}
