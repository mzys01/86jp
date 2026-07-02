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
        internal static readonly Random SeedGen = new Random();

        private readonly IAssetService _assetService;

        internal DungeonSharedServices(IAssetService assetService)
        {
            _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
        }

        public static void ResetDungeonState(EnhancedClientSession session)
        {
            session.Player.CurDungeon = 0;
            session.Player.CurDungeonClearState = 0;
            session.Player.CurDungeonTotalExp = 0;
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
        }

        internal void PersistLevelAndExp(int characterId, byte level, uint exp)
        {
            try
            {
                var repo = new SqliteCharacterRepository(
                    ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                repo.UpdateLevelAndExp(characterId, level, exp);

                // After level-up, recompute combat stats for the new level and persist subtype1.
                // growType selects the growth table (15-49 advancement / 50+ awakening); it must come from the character record.
                var rec = repo.GetById(characterId);
                if (rec != null)
                {
                    CharacterStatComputer.DecodeGrowType(rec.GrowType, out int first, out int second);
                    var blob = CharacterStatComputer.BuildAdditionalInfo(rec.Job, level, first, second);
                    new SqliteSubtype1Repository(
                        ServerPaths.DatabasePath, ServerPaths.SchemaFilePath)
                        .UpdateCombatStats(characterId, blob);
                }
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
                    _assetService.AddGold(scope, goldGained);
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
                    if (cost > 0)
                        _assetService.AddGold(scope, -cost);
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
                    if (wallet.Gold < goldCost)
                        return false;

                    updatedGold = wallet.Gold - goldCost;
                    _assetService.AddGold(scope, -goldCost);
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

            var candidates = QuestDropProvider.CheckMonsterDrop(
                activeQuestIds, session.Player.CurDungeon, session.Player.CurDungeonDifficulty, monsterCode);
            if (candidates == null) return;

            var accountId = session.Account?.AccountId ?? 1;

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
                if (dropCount <= 0) continue;

                short slot;
                if (!TryPickupItemToInventory(session.Player.CharacterId, accountId, candidate.ItemId, dropCount, out slot))
                    continue;

                // NOTI 14 UPDATE_ITEM_LIST (independent send, 86JP 84B fixed format)
                var w = new GamePacketWriter();
                w.WriteByte(0);                     // updateType = 0
                w.WriteUInt16(1);                   // count = 1
                w.WriteBytes(BuildItemEntry(slot, (uint)candidate.ItemId, (uint)(currentHeld + dropCount)));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, w.ToArray()));

                FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: monster={monsterCode} -> item={candidate.ItemId} x{dropCount} slot={slot} (held={currentHeld}->{currentHeld + dropCount})");
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
