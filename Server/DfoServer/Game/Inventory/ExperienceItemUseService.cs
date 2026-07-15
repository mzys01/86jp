using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Skills;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    internal sealed class ExperienceItemUseService
    {
        private readonly SqliteInventoryStore _inventoryStore;
        private readonly IRentalTimeProvider _timeProvider;
        private readonly ExperienceItemCooldownTracker _cooldowns;
        private readonly SqliteCharacterProgressRepository _progressRepository;

        internal ExperienceItemUseService(
            SqliteInventoryStore inventoryStore,
            IRentalTimeProvider timeProvider,
            ExperienceItemCooldownTracker cooldowns)
        {
            _inventoryStore = inventoryStore
                ?? throw new ArgumentNullException(nameof(inventoryStore));
            _timeProvider = timeProvider
                ?? throw new ArgumentNullException(nameof(timeProvider));
            _cooldowns = cooldowns
                ?? throw new ArgumentNullException(nameof(cooldowns));
            _progressRepository = SqliteCharacterProgressRepository.FromConnectionString(
                inventoryStore.ConnectionString);
        }

        internal ExperienceItemUseResult UseBySlot(
            int characterId,
            int accountId,
            InventoryListType listType,
            short slotIndex,
            ExperienceItemUseLocation location)
        {
            if (listType != InventoryListType.Main || characterId <= 0 || slotIndex < 0)
                return Reject(ExperienceItemUseStatus.NotApplicable, 0, "invalid source slot");

            var resolvedItemId = 0;
            ExperienceItemCooldownReservation cooldownReservation = null;
            try
            {
                using (var connection = new SqliteConnection(_inventoryStore.ConnectionString))
                {
                    connection.Open();
                    var preflightItem = _inventoryStore._db.LoadItemRecord(
                        connection, null, characterId, listType, slotIndex);
                    if (preflightItem == null)
                        return Reject(ExperienceItemUseStatus.NotApplicable, 0, "source slot is empty");

                    resolvedItemId = preflightItem.ItemTemplateId;
                    var definition = ExperienceItemDataProvider.Resolve(resolvedItemId);
                    if (!definition.IsExperienceLike)
                    {
                        return Reject(
                            ExperienceItemUseStatus.UnsupportedDefinition,
                            resolvedItemId,
                            "source item is not ordinary character experience");
                    }

                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        var item = _inventoryStore._db.LoadItemRecord(
                            connection, transaction, characterId, listType, slotIndex);
                        if (item == null || item.ItemTemplateId != resolvedItemId)
                        {
                            return Reject(
                                ExperienceItemUseStatus.NotApplicable,
                                resolvedItemId,
                                "source slot changed during use");
                        }

                        if (item.StackCount <= 0)
                        {
                            return Reject(
                                ExperienceItemUseStatus.ConsumeFailed,
                                item.ItemTemplateId,
                                "source stack is empty");
                        }

                        var character = _progressRepository.LoadProgressSnapshot(
                            connection, transaction, characterId);
                        if (character == null
                            || accountId <= 0
                            || character.AccountId != accountId)
                        {
                            return Reject(
                                ExperienceItemUseStatus.InvalidOwner,
                                item.ItemTemplateId,
                                "character/account ownership mismatch");
                        }

                        var usePlan = ExperienceItemUsePolicy.Evaluate(
                            new ExperienceItemUseContext
                            {
                                Definition = definition,
                                SourceExpireTime = item.ExpireTime,
                                NowUnixTime = _timeProvider.UtcNowUnixSeconds(),
                                Job = character.Job,
                                Level = character.Level,
                                Exp = character.Exp,
                                IsHardcore = character.IsHardcore,
                                Location = location,
                            });
                        if (!usePlan.Success)
                        {
                            return Reject(
                                usePlan.Status,
                                item.ItemTemplateId,
                                usePlan.Detail);
                        }

                        if (!_cooldowns.TryReserve(
                                characterId,
                                definition,
                                out cooldownReservation,
                                out var remainingCooldown))
                        {
                            return Reject(
                                ExperienceItemUseStatus.CooldownActive,
                                item.ItemTemplateId,
                                $"cooldown remaining={remainingCooldown}ms");
                        }

                        if (!_inventoryStore.TryDeleteItemCore(
                                connection,
                                transaction,
                                characterId,
                                listType,
                                listType,
                                slotIndex,
                                1,
                                out var consumedItem,
                                treatSourceAsStackable: true)
                            || consumedItem?.AppliedCount != 1
                            || consumedItem.ItemTemplateId != item.ItemTemplateId)
                        {
                            return Reject(
                                ExperienceItemUseStatus.ConsumeFailed,
                                item.ItemTemplateId,
                                "inventory deduction failed");
                        }

                        // 经验/等级/荣誉/胶囊统一走经验系统事务内入口, 随本事务提交或回滚。
                        // 输入与 Evaluate 预演相同, 结果按构造一致。
                        var grant = Progression.CharacterExperienceService.GrantInTransaction(
                            connection,
                            transaction,
                            characterId,
                            accountId,
                            character.Level,
                            character.Exp,
                            usePlan.GrantedExp);
                        if (!grant.Persisted)
                        {
                            return Reject(
                                ExperienceItemUseStatus.PersistenceFailed,
                                item.ItemTemplateId,
                                "level/experience persistence failed");
                        }

                        var syncedSkills = SkillStateService.LoadAndSync(
                            _progressRepository,
                            connection,
                            transaction,
                            characterId,
                            character.Job,
                            grant.NewLevel,
                            character.BonusSp,
                            character.BonusTp,
                            persist: grant.LeveledUp);
                        if (syncedSkills.Points == null)
                        {
                            return Reject(
                                ExperienceItemUseStatus.PersistenceFailed,
                                item.ItemTemplateId,
                                "skill-point synchronization failed");
                        }

                        var totalGrowthCapsuleExp = grant.TotalGrowthCapsuleExp;
                        if (grant.HonorExpGain == 0 && grant.NewLevel >= ExpTableProvider.MaxLevel)
                        {
                            totalGrowthCapsuleExp = GrowthCapsuleProgressRepository.LoadTotalExp(
                                connection,
                                transaction,
                                accountId);
                        }

                        var result = new ExperienceItemUseResult
                        {
                            Status = ExperienceItemUseStatus.Success,
                            AccountId = accountId,
                            ItemTemplateId = item.ItemTemplateId,
                            ConsumedItem = consumedItem,
                            PreviousLevel = character.Level,
                            NewLevel = grant.NewLevel,
                            PreviousExp = character.Exp,
                            NewExp = grant.NewExp,
                            GrantedExp = usePlan.GrantedExp,
                            HonorExpGain = grant.HonorExpGain,
                            TotalHonorExp = grant.TotalHonorExp,
                            TotalGrowthCapsuleExp = totalGrowthCapsuleExp,
                            SyncedSkills = syncedSkills.Skills,
                            SkillPoints = SkillStateService.GetProtocolState(
                                syncedSkills.Skills,
                                syncedSkills.Points),
                        };

                        transaction.Commit();
                        try
                        {
                            cooldownReservation?.Commit();
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log(
                                $"[ExperienceItem] cooldown commit failed after database commit: item={item.ItemTemplateId} cid={characterId} error={ex.Message}");
                        }
                        return result;
                    }
                }
            }
            catch (SqliteException ex)
            {
                FileLogger.Log(
                    $"[ExperienceItem] SQLite failure item={resolvedItemId} cid={characterId} slot={slotIndex}: {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode} {ex.Message}");
                return Reject(
                    ExperienceItemUseStatus.PersistenceFailed,
                    resolvedItemId,
                    "database transaction failed");
            }
            finally
            {
                cooldownReservation?.Dispose();
            }
        }

        private static ExperienceItemUseResult Reject(
            ExperienceItemUseStatus status,
            int itemTemplateId,
            string detail)
            => new ExperienceItemUseResult
            {
                Status = status,
                ItemTemplateId = itemTemplateId,
                Detail = detail,
            };
    }
}
