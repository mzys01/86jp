using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        private static readonly Lazy<PetCreatureEvolutionCatalog> PetCreatureEvolutionCatalogCache =
            new Lazy<PetCreatureEvolutionCatalog>(PetCreatureEvolutionCatalog.Load);

        internal static PetCreatureEvolutionResult TryEvolveEquippedPetCreature(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int creatureKey,
            int afterLevel)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (characterId <= 0 || creatureKey <= 0 || afterLevel <= 0)
                return PetCreatureEvolutionResult.Noop;

            var equipped = LoadPetCreatureEquippedEntry(connection, transaction, characterId);
            if (!equipped.HasValue || equipped.Value.Serial != creatureKey)
                return PetCreatureEvolutionResult.Noop;

            var catalog = PetCreatureEvolutionCatalogCache.Value;
            if (!catalog.TryResolveByItemId(equipped.Value.ItemId, out var current))
                return PetCreatureEvolutionResult.Noop;

            // 旧服 CCreature::IsAbleEvolute: PVF 必须配置 [evolution level],
            // 且当前宠物等级达到该值。
            if (!current.CanAutoEvolve || afterLevel < current.EvolutionLevel)
                return PetCreatureEvolutionResult.Noop;

            if (current.EvolutionItemTemplateId <= 0
                || !catalog.TryResolveByItemId(current.EvolutionItemTemplateId, out var next)
                || next.ItemTemplateId <= 0
                || next.ItemTemplateId == equipped.Value.ItemId)
            {
                FileLogger.Log($"[PetCreatureEvolution] skipped: missing target currentCreature={current.CreatureId} targetCreature={current.EvolutionCreatureId} targetItem=0x{current.EvolutionItemTemplateId:X8} item=0x{equipped.Value.ItemId:X8}");
                return PetCreatureEvolutionResult.Noop;
            }

            UpsertPetEquippedEntry(
                connection,
                transaction,
                characterId,
                PetCreatureEquipSlot,
                next.ItemTemplateId,
                creatureKey,
                NormalizePetCreatureExtra(equipped.Value.ExpireTime, equipped.Value.CreatureExtra),
                equipped.Value.ExpireTime);
            UpsertPetCreatureRuntimeState(connection, transaction, characterId, next.ItemTemplateId, creatureKey);

            FileLogger.Log($"[PetCreatureEvolution] evolved cid={characterId} key={creatureKey} creature={current.CreatureId}->{next.CreatureId} item=0x{equipped.Value.ItemId:X8}->0x{next.ItemTemplateId:X8} level={afterLevel}");
            return new PetCreatureEvolutionResult(
                changed: true,
                creatureKey: creatureKey,
                currentCreatureId: current.CreatureId,
                evolvedCreatureId: next.CreatureId,
                evolvedCreatureParam: next.CreatureParam,
                previousItemTemplateId: equipped.Value.ItemId,
                evolvedItemTemplateId: next.ItemTemplateId,
                equipmentSlot: PetCreatureEquipSlot);
        }

        internal static PetCreatureEvolutionResult TryCompletePetCreatureEvolutionQuest(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int requiredCreatureId,
            int requiredLevel,
            int targetCreatureId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (characterId <= 0 || requiredCreatureId <= 0 || targetCreatureId <= 0)
                return PetCreatureEvolutionResult.Noop;

            var equipped = LoadPetCreatureEquippedEntry(connection, transaction, characterId);
            if (!equipped.HasValue || equipped.Value.Serial <= 0)
                return PetCreatureEvolutionResult.Noop;

            var catalog = PetCreatureEvolutionCatalogCache.Value;
            if (!catalog.TryResolveByItemId(equipped.Value.ItemId, out var current))
                return PetCreatureEvolutionResult.Noop;

            if (!current.HasEvolutionQuest || current.CreatureId != requiredCreatureId)
            {
                FileLogger.Log($"[PetCreatureEvolution] quest skipped: current mismatch cid={characterId} current={current.CreatureId} required={requiredCreatureId} hasQuest={current.HasEvolutionQuest}");
                return PetCreatureEvolutionResult.Noop;
            }

            var level = LoadEquippedCreatureLevel(
                connection,
                transaction,
                characterId,
                equipped.Value.Serial);
            var minLevel = Math.Max(requiredLevel, current.EvolutionLevel);
            if (minLevel > 0 && level < minLevel)
            {
                FileLogger.Log($"[PetCreatureEvolution] quest skipped: level too low cid={characterId} creature={current.CreatureId} level={level} required={minLevel}");
                return PetCreatureEvolutionResult.Noop;
            }

            if (current.EvolutionCreatureId != targetCreatureId
                || !catalog.TryResolveByCreatureId(targetCreatureId, out var next)
                || next.ItemTemplateId <= 0
                || next.ItemTemplateId == equipped.Value.ItemId)
            {
                FileLogger.Log($"[PetCreatureEvolution] quest skipped: target mismatch cid={characterId} current={current.CreatureId} expected={current.EvolutionCreatureId} reward={targetCreatureId}");
                return PetCreatureEvolutionResult.Noop;
            }

            UpsertPetEquippedEntry(
                connection,
                transaction,
                characterId,
                PetCreatureEquipSlot,
                next.ItemTemplateId,
                equipped.Value.Serial,
                NormalizePetCreatureExtra(equipped.Value.ExpireTime, equipped.Value.CreatureExtra),
                equipped.Value.ExpireTime);
            UpsertPetCreatureRuntimeState(connection, transaction, characterId, next.ItemTemplateId, equipped.Value.Serial);

            FileLogger.Log($"[PetCreatureEvolution] quest evolved cid={characterId} key={equipped.Value.Serial} creature={current.CreatureId}->{next.CreatureId} item=0x{equipped.Value.ItemId:X8}->0x{next.ItemTemplateId:X8} level={level}");
            return new PetCreatureEvolutionResult(
                changed: true,
                creatureKey: equipped.Value.Serial,
                currentCreatureId: current.CreatureId,
                evolvedCreatureId: next.CreatureId,
                evolvedCreatureParam: next.CreatureParam,
                previousItemTemplateId: equipped.Value.ItemId,
                evolvedItemTemplateId: next.ItemTemplateId,
                equipmentSlot: PetCreatureEquipSlot);
        }

        internal static HashSet<int> LoadEligiblePetCreatureEvolutionQuestKinds(
            string databasePath,
            string schemaFilePath,
            int characterId)
        {
            var result = new HashSet<int>();
            if (characterId <= 0)
                return result;

            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var current = LoadEquippedPetEvolutionQuestState(connection, transaction, characterId);
                    if (current.HasValue)
                        result.Add(current.Value.CreatureId);
                    transaction.Commit();
                }
            }

            return result;
        }

        private static PetCreatureEvolutionQuestState? LoadEquippedPetEvolutionQuestState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var equipped = LoadPetCreatureEquippedEntry(connection, transaction, characterId);
            if (!equipped.HasValue || equipped.Value.Serial <= 0)
                return null;

            var catalog = PetCreatureEvolutionCatalogCache.Value;
            if (!catalog.TryResolveByItemId(equipped.Value.ItemId, out var current))
                return null;

            if (current.EvolutionLevel <= 0
                || current.EvolutionItemTemplateId <= 0
                || !current.HasEvolutionQuest)
                return null;

            var level = LoadEquippedCreatureLevel(
                connection,
                transaction,
                characterId,
                equipped.Value.Serial);
            if (level < current.EvolutionLevel)
                return null;

            return new PetCreatureEvolutionQuestState(
                current.CreatureId,
                current.EvolutionCreatureId,
                current.EvolutionItemTemplateId,
                current.EvolutionLevel);
        }

        private static int LoadEquippedCreatureLevel(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int creatureKey)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
SELECT field_after_value
FROM character_creatures
WHERE character_id = @cid AND creature_key = @key
LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@key", creatureKey);
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? 0
                    : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }
    }

    internal readonly struct PetCreatureEvolutionQuestState
    {
        public PetCreatureEvolutionQuestState(
            int creatureId,
            int evolutionCreatureId,
            int evolutionItemTemplateId,
            int evolutionLevel)
        {
            CreatureId = creatureId;
            EvolutionCreatureId = evolutionCreatureId;
            EvolutionItemTemplateId = evolutionItemTemplateId;
            EvolutionLevel = evolutionLevel;
        }

        public int CreatureId { get; }
        public int EvolutionCreatureId { get; }
        public int EvolutionItemTemplateId { get; }
        public int EvolutionLevel { get; }
    }

    public readonly struct PetCreatureEvolutionResult
    {
        public PetCreatureEvolutionResult(
            bool changed,
            int creatureKey,
            int currentCreatureId,
            int evolvedCreatureId,
            int evolvedCreatureParam,
            int previousItemTemplateId,
            int evolvedItemTemplateId,
            short equipmentSlot)
        {
            Changed = changed;
            CreatureKey = creatureKey;
            CurrentCreatureId = currentCreatureId;
            EvolvedCreatureId = evolvedCreatureId;
            EvolvedCreatureParam = evolvedCreatureParam;
            PreviousItemTemplateId = previousItemTemplateId;
            EvolvedItemTemplateId = evolvedItemTemplateId;
            EquipmentSlot = equipmentSlot;
        }

        public bool Changed { get; }
        public int CreatureKey { get; }
        public int CurrentCreatureId { get; }
        public int EvolvedCreatureId { get; }
        public int EvolvedCreatureParam { get; }
        public int PreviousItemTemplateId { get; }
        public int EvolvedItemTemplateId { get; }
        public short EquipmentSlot { get; }

        public static PetCreatureEvolutionResult Noop { get; } =
            new PetCreatureEvolutionResult(false, 0, 0, 0, 0, 0, 0, 0);
    }

    internal sealed class PetCreatureEvolutionCatalog
    {
        private readonly Dictionary<int, PetCreatureEvolutionEntry> _byCreatureId;
        private readonly Dictionary<int, PetCreatureEvolutionEntry> _byItemId;

        private PetCreatureEvolutionCatalog(
            Dictionary<int, PetCreatureEvolutionEntry> byCreatureId,
            Dictionary<int, PetCreatureEvolutionEntry> byItemId)
        {
            _byCreatureId = byCreatureId;
            _byItemId = byItemId;
        }

        internal bool TryResolveByItemId(int itemTemplateId, out PetCreatureEvolutionEntry entry)
            => _byItemId.TryGetValue(itemTemplateId, out entry);

        internal bool TryResolveByCreatureId(int creatureId, out PetCreatureEvolutionEntry entry)
            => _byCreatureId.TryGetValue(creatureId, out entry);

        internal bool TryResolvePreviousByEvolutionItemId(int evolutionItemTemplateId, out PetCreatureEvolutionEntry entry)
        {
            foreach (var candidate in _byItemId.Values)
            {
                if (candidate.EvolutionItemTemplateId == evolutionItemTemplateId)
                {
                    entry = candidate;
                    return true;
                }
            }

            entry = default(PetCreatureEvolutionEntry);
            return false;
        }

        internal static PetCreatureEvolutionCatalog Load()
        {
            var byCreatureId = new Dictionary<int, PetCreatureEvolutionEntry>();
            var byItemId = new Dictionary<int, PetCreatureEvolutionEntry>();

            try
            {
                var creatureList = LstFile.Parse(ReadPvfText("Creature/Creature.lst", "creature/creature.lst"));
                var creatureFiles = LoadCreatureFiles(creatureList);
                var equipmentFiles = LoadCreatureEquipmentFiles(creatureList);
                var itemByCreatureId = new Dictionary<int, int>();

                foreach (var equipment in equipmentFiles.Values)
                {
                    if (equipment.CreatureId > 0 && !itemByCreatureId.ContainsKey(equipment.CreatureId))
                        itemByCreatureId[equipment.CreatureId] = equipment.ItemTemplateId;
                }

                foreach (var equipment in equipmentFiles.Values)
                {
                    try
                    {
                        if (!creatureFiles.TryGetValue(equipment.CreatureId, out var creature))
                            continue;

                        var evolutionCreatureId = ParseInt(creature.EvolutionCreatureId);
                        var evolutionLevel = creature.EvolutionLevel > 0 ? creature.EvolutionLevel : 0;
                        var hasEvolutionQuest = HasEvolutionQuest(creature.EvolutionQuest);
                        var evolutionItemTemplateId = ResolveEvolutionItemTemplateId(
                            equipment,
                            evolutionCreatureId,
                            equipmentFiles,
                            itemByCreatureId);

                        var entry = new PetCreatureEvolutionEntry(
                            equipment.CreatureId,
                            equipment.ItemTemplateId,
                            equipment.CreatureParam,
                            evolutionCreatureId,
                            evolutionItemTemplateId,
                            evolutionLevel,
                            hasEvolutionQuest);
                        byCreatureId[equipment.CreatureId] = entry;
                        if (!byItemId.ContainsKey(equipment.ItemTemplateId))
                            byItemId[equipment.ItemTemplateId] = entry;
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[PetCreatureEvolution] catalog entry skipped item=0x{equipment.ItemTemplateId:X8} creature={equipment.CreatureId}: {ex.Message}");
                    }
                }

                FileLogger.Log($"[PetCreatureEvolution] loaded creature entries={byCreatureId.Count} itemMappings={byItemId.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[PetCreatureEvolution] catalog load failed: {ex.Message}");
            }

            return new PetCreatureEvolutionCatalog(byCreatureId, byItemId);
        }

        private static Dictionary<int, CreatureFile> LoadCreatureFiles(LstFile creatureList)
        {
            var result = new Dictionary<int, CreatureFile>();
            foreach (var entry in creatureList.Entries)
            {
                if (entry == null || entry.Id <= 0 || string.IsNullOrWhiteSpace(entry.FilePath))
                    continue;

                try
                {
                    var text = ReadPvfText(
                        Path.Combine("Creature", entry.FilePath),
                        Path.Combine("creature", entry.FilePath));
                    result[entry.Id] = CreatureFile.Parse(text);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[PetCreatureEvolution] creature file skipped creature={entry.Id} file={entry.FilePath}: {ex.Message}");
                }
            }

            return result;
        }

        private static Dictionary<int, PetCreatureEquipmentInfo> LoadCreatureEquipmentFiles(LstFile creatureList)
        {
            var result = new Dictionary<int, PetCreatureEquipmentInfo>();
            var creatureIdByFileName = BuildCreatureIdByFileName(creatureList);

            foreach (var equipment in ItemMetadataResolver.EquipmentList.Value.Entries)
            {
                if (equipment == null || equipment.Id <= 0 || string.IsNullOrWhiteSpace(equipment.FilePath))
                    continue;

                var normalizedPath = equipment.FilePath.Replace('\\', '/');
                if (!normalizedPath.StartsWith("creature/", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (!ItemMetadataResolver.TryLoadEquipmentFile(equipment.Id, out var file) || file == null)
                        continue;
                    if (!IsCreatureEquipment(file))
                        continue;

                    var creatureId = ResolveCreatureIdFromEquipmentPath(equipment.FilePath, creatureIdByFileName);
                    if (creatureId <= 0)
                        continue;

                    result[equipment.Id] = new PetCreatureEquipmentInfo(
                        equipment.Id,
                        creatureId,
                        file.OutputIndex,
                        ResolveCreatureParam(file, creatureId));
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[PetCreatureEvolution] creature equipment skipped item=0x{equipment.Id:X8} file={equipment.FilePath}: {ex.Message}");
                }
            }

            return result;
        }

        private static Dictionary<string, int> BuildCreatureIdByFileName(LstFile creatureList)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in creatureList.Entries)
            {
                if (entry == null || entry.Id <= 0 || string.IsNullOrWhiteSpace(entry.FilePath))
                    continue;

                var fileName = Path.GetFileNameWithoutExtension(entry.FilePath);
                if (!string.IsNullOrWhiteSpace(fileName) && !result.ContainsKey(fileName))
                    result[fileName] = entry.Id;
            }

            return result;
        }

        private static int ResolveCreatureIdFromEquipmentPath(
            string equipmentPath,
            Dictionary<string, int> creatureIdByFileName)
        {
            if (string.IsNullOrWhiteSpace(equipmentPath) || creatureIdByFileName == null)
                return 0;

            var fileName = Path.GetFileNameWithoutExtension(equipmentPath);
            return !string.IsNullOrWhiteSpace(fileName)
                && creatureIdByFileName.TryGetValue(fileName, out var creatureId)
                ? creatureId
                : 0;
        }

        private static int ResolveEvolutionItemTemplateId(
            PetCreatureEquipmentInfo equipment,
            int evolutionCreatureId,
            Dictionary<int, PetCreatureEquipmentInfo> equipmentFiles,
            Dictionary<int, int> itemByCreatureId)
        {
            if (equipment.OutputIndex > 0
                && equipment.OutputIndex != equipment.ItemTemplateId
                && equipmentFiles.ContainsKey(equipment.OutputIndex))
            {
                return equipment.OutputIndex;
            }

            if (evolutionCreatureId > 0
                && itemByCreatureId != null
                && itemByCreatureId.TryGetValue(evolutionCreatureId, out var itemTemplateId))
            {
                return itemTemplateId;
            }

            return 0;
        }

        private static string ReadPvfText(params string[] paths)
        {
            Exception last = null;
            foreach (var path in paths)
            {
                try
                {
                    return PvfArchiveAccessor.ReadText(path);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw last ?? new FileNotFoundException("PVF creature script not found.");
        }

        private static int ParseInt(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            value = value.Trim().Trim('`');
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }

        private static bool HasEvolutionQuest(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim().Trim('`');
            if (value.Length == 0 || value == "0" || value == "-1")
                return false;

            return !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                || parsed > 0;
        }

        private static bool IsCreatureEquipment(EquipmentFile equipment)
        {
            var type = equipment?.EquipmentType;
            return !string.IsNullOrWhiteSpace(type)
                && type.IndexOf("[creature]", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int ResolveCreatureParam(EquipmentFile equipment, int creatureId)
        {
            // 客户端 EVOLUTE_CREATURE 会把这个值传入 createCreature()。
            // 这里需要 Creature.lst 的宠物类型编号，不是 .equ [icon] 帧号。
            return creatureId > 0 ? creatureId : 0;
        }
    }

    internal readonly struct PetCreatureEquipmentInfo
    {
        public PetCreatureEquipmentInfo(int itemTemplateId, int creatureId, int outputIndex, int creatureParam)
        {
            ItemTemplateId = itemTemplateId;
            CreatureId = creatureId;
            OutputIndex = outputIndex;
            CreatureParam = creatureParam;
        }

        public int ItemTemplateId { get; }
        public int CreatureId { get; }
        public int OutputIndex { get; }
        public int CreatureParam { get; }
    }

    internal readonly struct PetCreatureEvolutionEntry
    {
        public PetCreatureEvolutionEntry(
            int creatureId,
            int itemTemplateId,
            int creatureParam,
            int evolutionCreatureId,
            int evolutionItemTemplateId,
            int evolutionLevel,
            bool hasEvolutionQuest)
        {
            CreatureId = creatureId;
            ItemTemplateId = itemTemplateId;
            CreatureParam = creatureParam;
            EvolutionCreatureId = evolutionCreatureId;
            EvolutionItemTemplateId = evolutionItemTemplateId;
            EvolutionLevel = evolutionLevel;
            HasEvolutionQuest = hasEvolutionQuest;
        }

        public int CreatureId { get; }
        public int ItemTemplateId { get; }
        public int CreatureParam { get; }
        public int EvolutionCreatureId { get; }
        public int EvolutionItemTemplateId { get; }
        public int EvolutionLevel { get; }
        public bool HasEvolutionQuest { get; }
        public bool CanAutoEvolve => EvolutionLevel > 0 && EvolutionItemTemplateId > 0 && !HasEvolutionQuest;
    }
}
