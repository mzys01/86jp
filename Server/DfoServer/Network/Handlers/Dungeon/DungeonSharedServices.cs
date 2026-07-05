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
        internal static readonly Random SeedGen = new Random();

        private readonly IAssetService _assetService;

        internal Game.ReviveCoin.ReviveCoinService ReviveCoin { get; }
        internal Game.DeathTower.DeathTowerHandler DeathTower { get; }

        internal DungeonSharedServices(IAssetService assetService, Game.ReviveCoin.ReviveCoinService reviveCoin)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
            ReviveCoin = reviveCoin ?? throw new ArgumentNullException(nameof(reviveCoin));
            DeathTower = new Game.DeathTower.DeathTowerHandler();
        }

        internal static DungeonRoomProgress GetCurrentRoomProgress(EnhancedClientSession session)
            => GetRoomProgress(session, session?.Player?.CurRoomKilledSeqIds);

        internal static DungeonRoomProgress GetRoomProgress(
            EnhancedClientSession session,
            ISet<ushort> killedSeqIds)
        {
            var player = session?.Player;
            var monsters = player?.CurRoomMonsters ?? Array.Empty<DungeonData.MonsterSumInfo>();
            var killed = killedSeqIds ?? new HashSet<ushort>();
            var startSeq = player?.CurRoomStartSequence ?? 0;

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

        public static void ResetDungeonState(EnhancedClientSession session)
            => ResetDungeonStateAsync(session).GetAwaiter().GetResult();

        public static async Task ResetDungeonStateAsync(EnhancedClientSession session)
        {
            await CheckPetCreatureDeathAsync(session, "reset_dungeon_state");
            PersistPetCreatureSatiety(session, "reset_dungeon_state");
            await TryRevivePetCreatureOnTownReturnAsync(session, "reset_dungeon_state");
            Game.DeathTower.DeathTowerHandler.ClearTowerState(session);
            session.Player.CurDungeon = 0;
            session.Player.CurDungeonClearState = 0;
            session.Player.CurDungeonTotalExp = 0;
            session.Player.CurDungeonPetFatigueConsumed = 0;
            session.Player.CurDungeonBossTotalExp = 0;
            session.Player.CurDungeonChampionTotalExp = 0;
            session.Player.CurDungeonSuperChampionTotalExp = 0;
            session.Player.CurDungeonNamedMonsterTotalExp = 0;
            session.Player.CurDungeonMonsterGrowthContractBonusExp = 0;
            session.Player.CurDungeonTotalGold = 0;
            session.Player.CurDungeonDifficulty = 0;
            session.Player.CurDungeonFlag1 = 0;
            session.Player.CurDungeonFlag2 = 0;
            session.Player.CurMazeQuestConnected = false;
            session.Player.CurMazeStartMapId = 0;
            session.Player.CurMazeStartX = -1;
            session.Player.CurMazeStartY = -1;
            session.Player.CurDungeonHellMode = false;
            session.Player.CurDungeonHellPartyMode = 0;
            session.Player.CurDungeonVeryDifficultHell = false;
            session.Player.CurDungeonHellGorgeousChallenge = false;
            session.Player.CurDungeonHellMapId = -1;
            session.Player.CurDungeonHellMapX = 0xFF;
            session.Player.CurDungeonHellMapY = 0xFF;
            session.Player.CurDungeonHellRoomInfo = null;
            session.Player.CurMazeIndex = -1;
            session.Player.CurLayeredMapIndex = -1;
            session.Player.CurMap = 0;
            session.Player.CurMonsterCnt = 0;
            session.Player.CurRoomStartSequence = 0;
            session.Player.CurRoomMonsters = Array.Empty<DungeonData.MonsterSumInfo>();
            session.Player.CurRoomKilledSeqIds.Clear();
            session.Player.CurBossKilled = false;
            session.Player.CurDungeonCleared = false;
            session.Player.CurBossCode = 0;
            session.Player.CurBossMapPos = null;
            session.Player.CurClearCondition = null;
            session.Player.CurSceneSlotCounter = 0;
            session.Player.CurDungeonSeed = 0;
            session.Player.CurRoomLcg = null;
            session.Player.CurMoveMapU15 = 0;
            session.Player.CurMoveMapU19 = 0;
            session.Player.CurDungeonDrops.Clear();
            session.Player.DungeonRoomStates.Clear();
            session.Player.CurDungeonRidableObjects.Clear();
            session.Player.CurCardRewards = null;
            session.Player.CurCardFlipCount = 0;
            session.Player.CurFreeCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
            session.Player.CurPaidCardSlots = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
            BeginPetCreatureTownRecovery(session);
        }

        public static void BeginPetCreatureSatiety(EnhancedClientSession session)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0 || session.Player.CurDungeon <= 0)
                return;

            PersistPetCreatureSatiety(session, "begin_dungeon");
            PersistPetCreatureTownRecovery(session, "begin_dungeon");
            session.Player.PetCreatureSatietyDungeonStartUtc = DateTime.UtcNow;
            session.Player.PetCreatureSatietyDungeonId = session.Player.CurDungeon;
            var timerVersion = AdvancePetCreatureDeathTimerVersion(session);
            FileLogger.Log($"[{ProtocolLogName}] PetCreatureSatiety: begin cid={session.Player.CharacterId} dungeon={session.Player.CurDungeon} deathTimerVersion={timerVersion}");
            StartPetCreatureDeathTimer(session, timerVersion);
        }

        internal static async Task HandlePetCreatureChangedInDungeonAsync(EnhancedClientSession session, string source)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0 || session.Player.CurDungeon <= 0)
                return;

            var startedAt = DateTime.UtcNow;
            session.Player.PetCreatureSatietyDungeonStartUtc = startedAt;
            session.Player.PetCreatureSatietyDungeonId = session.Player.CurDungeon;
            var timerVersion = AdvancePetCreatureDeathTimerVersion(session);

            PetCreatureSatietyUpdate current;
            try
            {
                current = PetCreatureSatietyService.LoadEquippedCreatureSatiety(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    session.Player.CharacterId);
            }
            catch (Exception ex)
            {
                session.Player.PetCreatureSatietyDungeonStartUtc = DateTime.MinValue;
                session.Player.PetCreatureSatietyDungeonId = 0;
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeathTimer: pet changed start failed source={source} cid={session.Player.CharacterId} version={timerVersion}: {ex.Message}");
                return;
            }

            if (current.CreatureKey <= 0)
            {
                session.Player.PetCreatureSatietyDungeonStartUtc = DateTime.MinValue;
                session.Player.PetCreatureSatietyDungeonId = 0;
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeathTimer: pet changed no active creature source={source} cid={session.Player.CharacterId} version={timerVersion}");
                return;
            }

            FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeathTimer: pet changed source={source} cid={session.Player.CharacterId} dungeon={session.Player.CurDungeon} key={current.CreatureKey} satiety={current.Before} foodRate={current.FoodConsumeRatePercent}% multiplier={current.FoodConsumeMultiplier:0.###} version={timerVersion}");

            if (current.Before <= 1)
            {
                await CheckPetCreatureDeathAsync(session, source, timerVersion);
                return;
            }

            StartPetCreatureDeathTimer(session, timerVersion);
        }

        public static void BeginPetCreatureTownRecovery(EnhancedClientSession session)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0)
                return;
            if (session.Player.CurDungeon > 0 || session.Player.PetCreatureSatietyDungeonStartUtc != DateTime.MinValue)
                return;
            if (session.Player.PetCreatureSatietyTownStartUtc != DateTime.MinValue)
                return;

            session.Player.PetCreatureSatietyTownStartUtc = DateTime.UtcNow;
            FileLogger.Log($"[{ProtocolLogName}] PetCreatureSatiety: town begin cid={session.Player.CharacterId}");
        }

        public static void PersistPetCreatureSatiety(EnhancedClientSession session, string source)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0)
                return;

            var startUtc = session.Player.PetCreatureSatietyDungeonStartUtc;
            if (startUtc == DateTime.MinValue)
                return;

            var dungeonId = session.Player.PetCreatureSatietyDungeonId;
            session.Player.PetCreatureSatietyDungeonStartUtc = DateTime.MinValue;
            session.Player.PetCreatureSatietyDungeonId = 0;
            AdvancePetCreatureDeathTimerVersion(session);

            try
            {
                var update = PetCreatureSatietyService.ApplyDungeonElapsed(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    session.Player.CharacterId,
                    startUtc,
                    DateTime.UtcNow);

                FileLogger.Log(
                    $"[{ProtocolLogName}] PetCreatureSatiety: persist source={source} cid={session.Player.CharacterId} dungeon={dungeonId} key={update.CreatureKey} elapsed={update.ElapsedSeconds:0.0}s foodRate={update.FoodConsumeRatePercent}% multiplier={update.FoodConsumeMultiplier:0.###} consumed={update.ConsumedSatiety} satiety={update.Before}->{update.After} changed={update.Changed}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureSatiety: persist failed source={source} cid={session.Player.CharacterId} dungeon={dungeonId}: {ex.Message}");
            }
        }

        public static void PersistPetCreatureTownRecovery(EnhancedClientSession session, string source)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0)
                return;

            var startUtc = session.Player.PetCreatureSatietyTownStartUtc;
            if (startUtc == DateTime.MinValue)
                return;

            session.Player.PetCreatureSatietyTownStartUtc = DateTime.MinValue;

            try
            {
                var update = PetCreatureSatietyService.ApplyTownElapsed(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    session.Player.CharacterId,
                    startUtc,
                    DateTime.UtcNow);

                FileLogger.Log(
                    $"[{ProtocolLogName}] PetCreatureSatiety: town persist source={source} cid={session.Player.CharacterId} key={update.CreatureKey} elapsed={update.ElapsedSeconds:0.0}s recovered={update.RecoveredSatiety} satiety={update.Before}->{update.After} changed={update.Changed}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureSatiety: town persist failed source={source} cid={session.Player.CharacterId}: {ex.Message}");
            }
        }

        internal async Task HandleUseSkillAsync(EnhancedClientSession session, byte[] body)
        {
            var player = session?.Player;
            var timerState = player?.PetCreatureSatietyDungeonStartUtc == DateTime.MinValue
                ? "none"
                : player?.PetCreatureSatietyDungeonStartUtc.ToString("O");
            FileLogger.Log($"[{ProtocolLogName}] CMD_0026 USE_SKILL: uid={player?.UserId ?? 0} cid={player?.CharacterId ?? 0} dungeon={player?.CurDungeon ?? 0} satietyTimer={timerState} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
            await Task.CompletedTask;
        }

        private static int AdvancePetCreatureDeathTimerVersion(EnhancedClientSession session)
        {
            if (session?.Player == null)
                return 0;

            unchecked
            {
                session.Player.PetCreatureDeathTimerVersion++;
                if (session.Player.PetCreatureDeathTimerVersion == 0)
                    session.Player.PetCreatureDeathTimerVersion = 1;
            }

            return session.Player.PetCreatureDeathTimerVersion;
        }

        private static void StartPetCreatureDeathTimer(EnhancedClientSession session, int timerVersion)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0)
                return;

            PetCreatureSatietyUpdate current;
            try
            {
                current = PetCreatureSatietyService.LoadEquippedCreatureSatiety(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    session.Player.CharacterId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeathTimer: start failed cid={session.Player.CharacterId} version={timerVersion}: {ex.Message}");
                return;
            }

            if (current.CreatureKey <= 0)
            {
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeathTimer: no active creature cid={session.Player.CharacterId} version={timerVersion}");
                return;
            }

            var satiety = Math.Max(0, current.Before);
            var multiplier = current.FoodConsumeMultiplier;
            var delay = satiety <= 1
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((satiety - 1) * 60.0 / multiplier);
            FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeathTimer: start cid={session.Player.CharacterId} dungeon={session.Player.CurDungeon} key={current.CreatureKey} satiety={satiety} foodRate={current.FoodConsumeRatePercent}% multiplier={multiplier:0.###} delay={delay.TotalSeconds:0.0}s version={timerVersion}");

            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay);

                    await CheckPetCreatureDeathAsync(session, "timer", timerVersion);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeathTimer: task failed cid={session?.Player?.CharacterId ?? 0} version={timerVersion}: {ex}");
                }
            });
        }

        internal static async Task CheckPetCreatureDeathAsync(EnhancedClientSession session, string source, int? expectedTimerVersion = null)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0)
            {
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeath: skip source={source} reason=no-player");
                return;
            }

            if (expectedTimerVersion.HasValue
                && session.Player.PetCreatureDeathTimerVersion != expectedTimerVersion.Value)
            {
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeath: skip source={source} cid={session.Player.CharacterId} reason=stale-timer expected={expectedTimerVersion.Value} actual={session.Player.PetCreatureDeathTimerVersion}");
                return;
            }

            var startUtc = session.Player.PetCreatureSatietyDungeonStartUtc;
            if (startUtc == DateTime.MinValue)
            {
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeath: skip source={source} cid={session.Player.CharacterId} reason=no-dungeon-satiety-timer");
                return;
            }

            try
            {
                var update = PetCreatureSatietyService.ApplyDungeonDeathIfExpired(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    session.Player.CharacterId,
                    startUtc,
                    DateTime.UtcNow);

                if (update.CreatureKey <= 0)
                {
                    FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeath: check source={source} cid={session.Player.CharacterId} no active creature elapsed={update.ElapsedSeconds:0.0}s");
                    return;
                }

                if (update.After > 0)
                {
                    FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeath: check source={source} cid={session.Player.CharacterId} key={update.CreatureKey} elapsed={update.ElapsedSeconds:0.0}s foodRate={update.FoodConsumeRatePercent}% multiplier={update.FoodConsumeMultiplier:0.###} satiety={update.Before}->{update.After} alive");
                    return;
                }

                session.Player.PetCreatureSatietyDungeonStartUtc = DateTime.MinValue;
                session.Player.PetCreatureSatietyDungeonId = 0;
                AdvancePetCreatureDeathTimerVersion(session);

                var writer = new GamePacketWriter();
                writer.WriteUInt16(session.Player.UserId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0064, writer.ToArray()));
                SetSessionPetCreatureAliveFlag(session, 0);

                FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeath: DIED_CREATURE source={source} uid={session.Player.UserId} cid={session.Player.CharacterId} key={update.CreatureKey} elapsed={update.ElapsedSeconds:0.0}s foodRate={update.FoodConsumeRatePercent}% multiplier={update.FoodConsumeMultiplier:0.###} satiety={update.Before}->0");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureDeath: check failed source={source} cid={session.Player.CharacterId}: {ex}");
            }
        }

        private static async Task TryRevivePetCreatureOnTownReturnAsync(EnhancedClientSession session, string source)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0)
                return;

            PetCreatureRevivalUpdate update;
            try
            {
                update = PetCreatureSatietyService.ReviveEquippedCreatureIfDead(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath,
                    session.Player.CharacterId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureRevival: failed source={source} cid={session.Player.CharacterId}: {ex}");
                return;
            }

            if (update.CreatureKey <= 0)
            {
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureRevival: skip source={source} cid={session.Player.CharacterId} reason=no-active-creature");
                return;
            }

            if (!update.Revived)
            {
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureRevival: skip source={source} cid={session.Player.CharacterId} key={update.CreatureKey} satiety={update.Before}->{update.After} reason=alive");
                return;
            }

            await SendPetCreatureRevivalPacketsAsync(session, update, source);
        }

        private static async Task SendPetCreatureRevivalPacketsAsync(
            EnhancedClientSession session,
            PetCreatureRevivalUpdate update,
            string source)
        {
            try
            {
                var revival = new GamePacketWriter();
                revival.WriteUInt16(session.Player.UserId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x006B, revival.ToArray()));
                SetSessionPetCreatureAliveFlag(session, update.After > 0 ? (byte)1 : (byte)0);

                await SendPetCreatureStatePacketAsync(session, update.CreatureKey, update.After);

                FileLogger.Log($"[{ProtocolLogName}] PetCreatureRevival: REVIVAL_CREATURE source={source} uid={session.Player.UserId} cid={session.Player.CharacterId} key={update.CreatureKey} satiety={update.Before}->{update.After}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] PetCreatureRevival: send failed source={source} cid={session?.Player?.CharacterId ?? 0}: {ex}");
            }
        }

        private static Task SendPetCreatureStatePacketAsync(
            EnhancedClientSession session,
            int creatureKey,
            int stateValue)
        {
            var state = new GamePacketWriter();
            state.WriteInt32(creatureKey);
            state.WriteInt32(stateValue);
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0067, state.ToArray()));
        }

        private static void SetSessionPetCreatureAliveFlag(EnhancedClientSession session, byte value)
        {
            var tail = session?.Player?.Subtype0Tail;
            if (tail == null)
                return;

            tail.PetCreatureAliveFlag = value;
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
            catch { return 0; }
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
            catch { }
            return totalBonus;
        }

        internal async Task HandleRecoverStaminaAsync(EnhancedClientSession session, byte[] body)
        {
            FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: uid={session?.Player?.UserId ?? 0} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");

            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0)
                return;

            var accountId = session?.Account?.AccountId ?? 1;
            if (accountId <= 0)
                accountId = 1;

            try
            {
                var repo = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var tail = repo.Load(characterId) ?? session.Player.Subtype0Tail;
                if (tail == null || tail.Stamina == 0)
                {
                    await SendRecoverStaminaErrorAsync(session, 18);
                    FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: no weakness state cid={characterId}");
                    return;
                }

                var cost = CalculateRecoverStaminaGoldCost(session.Player.Level, tail.Stamina);
                int updatedGold;
                using (var scope = _assetService.OpenScope(characterId, accountId))
                {
                    var wallet = _assetService.LoadWallet(scope);
                    if (wallet.Gold < cost)
                    {
                        await SendRecoverStaminaErrorAsync(session, 22);
                        FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: insufficient gold cid={characterId} need={cost} have={wallet.Gold} stamina={tail.Stamina}");
                        return;
                    }

                    updatedGold = wallet.Gold - cost;
                    if (cost > 0 && !_assetService.TrySpendGold(scope, cost))
                    {
                        await SendRecoverStaminaErrorAsync(session, 22);
                        FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: TrySpendGold refused cid={characterId} need={cost}");
                        return;
                    }
                    scope.Commit();
                }

                tail.Stamina = 0;
                tail.FatiguePenalty = 0;
                SaveSubtype0Tail(characterId, tail);
                session.Player.Subtype0Tail = tail;

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0021, new[] { (byte)100 }));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                    ItemListUpdateBuilder.BuildGoldUpdate(updatedGold)));

                FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: success cid={characterId} cost={cost} gold={updatedGold}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA ERROR: cid={characterId} {ex}");
                await SendRecoverStaminaErrorAsync(session, 4);
            }
        }

        internal static async Task HandlePremiumServiceQueryAsync(EnhancedClientSession session, byte[] body)
        {
            var aid = session?.Account?.AccountId ?? 0;
            FileLogger.Log($"[{ProtocolLogName}] CMD_0312: uid={session?.Player?.UserId ?? 0} aid={aid} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");

            var connStr = SqliteDatabaseBootstrap.Initialize(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            var serviceData = Game.Premium.PremiumService.BuildPremiumServiceData(connStr, aid);

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteUInt16(1);
            writer.WriteBytes(serviceData);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0312, writer.ToArray()));
            FileLogger.Log($"[{ProtocolLogName}] CMD_0312: responded with dynamic PremiumServiceData account={aid}");
        }

        private static void SaveSubtype0Tail(int characterId, UserInfoMinimumTailSnapshot tail)
        {
            var connStr = SqliteDatabaseBootstrap.Initialize(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
            {
                conn.Open();
                SqliteSubtype0FieldsRepository.Save(conn, characterId, tail);
            }
        }

        private static Task SendRecoverStaminaErrorAsync(EnhancedClientSession session, byte errorCode)
        {
            if (session == null || session.TcpClient == null || !session.TcpClient.Connected)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0009, new[] { (byte)0, errorCode, (byte)0 }));
        }

        internal static int CalculateRecoverStaminaGoldCost(byte level, byte stamina)
        {
            if (stamina == 0)
                return 0;

            var basePrice = RecoverStaminaPriceProvider.GetBasePrice(level);
            var normalizedStamina = Math.Min((byte)10, stamina);
            var officialCurrentStamina = Math.Max(0, 100 - normalizedStamina * 9);
            var cost = basePrice * (100 - officialCurrentStamina) / 90;
            return Math.Max(0, cost);
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
                var repo = new SqliteCharacterStateRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                if (!repo.UpsertDungeonPermission(characterId, dungeonId, newClearState))
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
            var charRepo = new SqliteCharacterRepository(
                ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            var record = charRepo.GetById(characterId);
            var skillRepo = new SqliteCharacterProgressRepository(
                ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);

            if (record == null)
                return (skillRepo.LoadSkills(characterId), null);

            return SkillStateService.LoadAndSync(
                skillRepo,
                characterId,
                record.Job,
                currentLevel > 0 ? currentLevel : record.Level,
                record.BonusSp,
                record.BonusTp,
                persist: persist);
        }

        internal async Task SendQuestListRefresh(EnhancedClientSession session)
        {
            try
            {
                var charRepo = new SqliteCharacterRepository(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var rec = charRepo.GetById(session.Player.CharacterId);
                if (rec == null) return;

                var stateRepo = new SqliteCharacterStateRepository(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var initSnap = new SelectCharacterInitializationSnapshot();
                stateRepo.LoadFlags(session.Player.CharacterId, initSnap);

                var clearedSet = new HashSet<int>();
                var clearedFlags = new Dictionary<int, int>();
                foreach (var entry in initSnap.CharacInvisibleFalgs)
                {
                    if (entry.FlagValue != 0)
                    {
                        clearedSet.Add(entry.SlotIndex);
                        clearedFlags[entry.SlotIndex] = entry.FlagValue;
                    }
                }

                var questIds = QuestData.ComputeAcceptableQuests(
                    session.Player.Level, rec.Job, rec.GrowType, clearedSet, clearedFlags);

                var w = new GamePacketWriter();
                w.WriteByte(session.Player.Level);
                w.WriteUInt16((ushort)questIds.Count);
                foreach (var qid in questIds)
                    w.WriteUInt16(qid);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0015, w.ToArray()));
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
                var charRepo = new SqliteCharacterRepository(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var record = charRepo.GetById(cid);
                var subtype1Repo = new SqliteSubtype1Repository(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var addition = subtype1Repo.HasData(cid) ? subtype1Repo.Load(cid) : null;
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
                var charRepo = new SqliteCharacterRepository(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                var record = charRepo.GetById(cid);
                if (record == null)
                    return;

                record.Subtype0Tail = new SqliteSubtype0FieldsRepository(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath).Load(cid);

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

        // NOTI 14 UPDATE_ITEM_LIST - 84B item entry (Reverse/SUBSYSTEMS/inventory_system.md)
        // PacketPopBuffer(84) memcpy, not per-field read
        internal static byte[] BuildItemEntry(short slotIndex, uint itemId, uint instanceValue)
        {
            var buf = new byte[84];
            BitConverter.GetBytes(slotIndex).CopyTo(buf, 0);
            BitConverter.GetBytes(itemId).CopyTo(buf, 2);
            BitConverter.GetBytes(instanceValue).CopyTo(buf, 6);
            return buf;
        }

        internal static byte[] BuildEquipEntry(short slotIndex, uint itemId,
            uint qualitySeed = 999999998, ushort durability = 32)
        {
            var buf = new byte[84];
            BitConverter.GetBytes(slotIndex).CopyTo(buf, 0);    // [0:2]  slot
            BitConverter.GetBytes(itemId).CopyTo(buf, 2);        // [2:6]  itemId
            BitConverter.GetBytes(qualitySeed).CopyTo(buf, 6);   // [6:10] quality seed
            // buf[10] = 0 enhance level
            BitConverter.GetBytes(durability).CopyTo(buf, 11);   // [11:13] durability
            // buf[13] = 0 isSealed
            BitConverter.GetBytes(0xFFFFFFFF).CopyTo(buf, 22);   // [22:26] equipment marker
            return buf;
        }

        internal async Task CheckQuestMonsterDrop(EnhancedClientSession session, int monsterCode)
        {
            if (session.Player.CurDungeon <= 0 || monsterCode <= 0) return;

            await CheckQuestDrop(session, monsterCode, "monster", activeQuestIds =>
                QuestDropProvider.CheckMonsterDrop(
                    activeQuestIds, session.Player.CurDungeon, session.Player.CurDungeonDifficulty, monsterCode));
        }

        internal async Task CheckQuestPassiveObjectDrop(EnhancedClientSession session, int objectCode)
        {
            if (session.Player.CurDungeon <= 0 || objectCode <= 0) return;

            await CheckQuestDrop(session, objectCode, "passive", activeQuestIds =>
                QuestDropProvider.CheckEnemyDrop(
                    activeQuestIds,
                    session.Player.CurDungeon,
                    session.Player.CurDungeonDifficulty,
                    objectCode,
                    QuestDropProvider.EnemyTypePassiveObject));
        }

        private async Task CheckQuestDrop(
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
                w.WriteBytes(BuildItemEntry(slot, (uint)candidate.ItemId, (uint)(currentHeld + dropCount)));
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
