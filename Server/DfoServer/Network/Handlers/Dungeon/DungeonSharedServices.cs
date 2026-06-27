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
            session.Player.CurDungeonTotalGold = 0;
            session.Player.CurDungeonDifficulty = 0;
            session.Player.CurDungeonFlag1 = 0;
            session.Player.CurDungeonFlag2 = 0;
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
                activeQuestIds, session.Player.CurDungeon, monsterCode);
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
}
