using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryCompoundEmblems(int characterId, int accountId, EmblemCompoundRequest request, out EmblemCompoundResult result)
        {
            result = Error(EmblemCompoundResult.ErrorInvalidRequest);
            if (request?.Inputs == null || request.Inputs.Count < 2 || request.Inputs.Count > 5)
                return false;

            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var consumedBySlot = request.Inputs
                .GroupBy(input => input.SlotIndex)
                .ToDictionary(group => group.Key, group => group.ToList());
            var sources = new Dictionary<short, ItemRecord>();
            var grades = new List<int>();

            foreach (var pair in consumedBySlot)
            {
                var source = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, pair.Key);
                if (source == null || source.StackCount < pair.Value.Count || !string.Equals(source.ItemKind, "stackable", StringComparison.Ordinal))
                    return false;

                foreach (var input in pair.Value)
                {
                    if (input.ItemTemplateId != source.ItemTemplateId)
                        return false;
                }

                var metadata = ItemMetadataResolver.Resolve(source.ItemTemplateId);
                if (metadata == null || !metadata.IsStackable || !IsAvatarEmblem(metadata.StackableType) || metadata.Grade <= 0)
                    return false;

                sources[pair.Key] = source;
                for (var index = 0; index < pair.Value.Count; index++)
                    grades.Add(metadata.Grade);
            }

            // 原版配置按最低品级选择对应的合成奖励池；同品级合成即直接命中该品级与数量的 PVF 映射。
            var compoundGrade = grades.Min();
            if (!EmblemCompoundConfigProvider.TryRollReward(compoundGrade, request.Inputs.Count,
                    out var boosterItemTemplateId, out var rewardItemTemplateId, out var rewardCount))
            {
                FileLogger.Log($"[EmblemCompound] no PVF mapping grade={compoundGrade} count={request.Inputs.Count} grades={string.Join(",", grades)}");
                return false;
            }

            foreach (var pair in consumedBySlot)
            {
                var source = sources[pair.Key];
                var consumedCount = pair.Value.Count;
                var remainingCount = source.StackCount - consumedCount;
                if (remainingCount > 0)
                    _db.UpdateStackCount(connection, transaction, source.ItemUid, remainingCount);
                else
                    _db.DeleteItem(connection, transaction, source.ItemUid);
                _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, source, consumedCount);
            }

            if (!TryPickupItemCore(connection, transaction, characterId, accountId, rewardItemTemplateId, rewardCount, out var rewardSlotIndex))
            {
                result = Error(EmblemCompoundResult.ErrorInventoryFull);
                return false;
            }

            var rewardRecord = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, rewardSlotIndex);
            transaction.Commit();

            result = new EmblemCompoundResult
            {
                ErrorCode = 0,
                RewardItemTemplateId = rewardItemTemplateId,
                RewardSlotIndex = rewardSlotIndex,
                RewardGrantedCount = rewardCount,
                RewardStackCount = rewardRecord?.StackCount ?? rewardCount,
                PvfBoosterItemTemplateId = boosterItemTemplateId,
            };
            foreach (var slot in consumedBySlot.Keys.OrderBy(slot => slot))
                result.ChangedSlots.Add(slot);
            if (!result.ChangedSlots.Contains(rewardSlotIndex))
                result.ChangedSlots.Add(rewardSlotIndex);
            return true;
        }

        private static bool IsAvatarEmblem(string stackableType)
        {
            if (string.IsNullOrWhiteSpace(stackableType))
                return false;
            var normalized = stackableType.Replace("`", string.Empty).Trim();
            return normalized.StartsWith("[avatar emblem]", StringComparison.OrdinalIgnoreCase);
        }

        private static EmblemCompoundResult Error(byte code) => new EmblemCompoundResult { ErrorCode = code };
    }
}
