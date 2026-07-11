using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryDisjointAvatar(int characterId, int accountId, AvatarDisjointRequest request, out AvatarDisjointResult result)
        {
            result = Error(request, AvatarDisjointResult.ErrorInvalidRequest);
            if (request == null || request.SlotIndex < 0)
                return false;

            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var source = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Avatar, request.SlotIndex);
            if (source == null || !string.Equals(source.ItemKind, "avatar", StringComparison.Ordinal))
                return false;
            if (request.ExpectedItemTemplateId > 0 && request.ExpectedItemTemplateId != source.ItemTemplateId)
                return false;
            if (IsEquipmentItemLocked(connection, transaction, characterId, source))
                return false;

            var metadata = ItemMetadataResolver.Resolve(source.ItemTemplateId);
            if (metadata == null || string.IsNullOrWhiteSpace(metadata.EquipmentType)
                || metadata.EquipmentType.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) < 0
                || ContainsImpossibleContent(metadata, "disjoint"))
                return false;

            var materials = AvatarDisjointConfigProvider.Calculate(metadata.Grade);
            if (materials.Count == 0)
            {
                FileLogger.Log($"[AvatarDisjoint] no PVF reward pool item=0x{source.ItemTemplateId:X8} grade={metadata.Grade}");
                return false;
            }

            foreach (var reward in materials)
            {
                if (!TryPickupItemCore(connection, transaction, characterId, accountId, reward.ItemTemplateId, reward.Count, out var slot))
                {
                    result = Error(request, AvatarDisjointResult.ErrorInventoryFull);
                    result.SourceItemTemplateId = source.ItemTemplateId;
                    return false;
                }
                reward.SlotIndex = slot;
            }

            _db.DeleteItem(connection, transaction, source.ItemUid);
            _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, source, 1);
            transaction.Commit();

            result = new AvatarDisjointResult { Request = request, SourceItemTemplateId = source.ItemTemplateId, ErrorCode = 0 };
            result.Materials.AddRange(materials);
            return true;
        }

        private static AvatarDisjointResult Error(AvatarDisjointRequest request, byte code) =>
            new AvatarDisjointResult { Request = request, ErrorCode = code };
    }
}
