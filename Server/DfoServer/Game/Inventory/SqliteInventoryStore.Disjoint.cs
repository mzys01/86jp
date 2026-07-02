using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryDisjointItem(int characterId, int accountId, DisjointItemRequest request, out DisjointItemResult result)
        {
            result = CreateDisjointErrorResult(request, DisjointItemResult.ErrorInvalidRequest);
            if (request == null || request.TargetSlotIndex < 0)
                return false;

            if (request.ItemSpace != InventoryListType.Main || request.DisjointItemSlotIndex < -1)
                return false;

            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var source = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.TargetSlotIndex);
                if (source == null)
                {
                    result = CreateDisjointErrorResult(request, DisjointItemResult.ErrorInvalidTarget);
                    return false;
                }

                var metadata = ItemMetadataResolver.Resolve(source.ItemTemplateId);
                if (!TryValidateDisjoint(source, metadata, out var errorCode))
                {
                    result = CreateDisjointErrorResult(request, errorCode);
                    result.SourceItemTemplateId = source.ItemTemplateId;
                    return false;
                }

                if (!TryValidatePortableDisjointItem(connection, transaction, characterId, request, metadata, out var disjointTool, out errorCode))
                {
                    result = CreateDisjointErrorResult(request, errorCode);
                    result.SourceItemTemplateId = source.ItemTemplateId;
                    return false;
                }

                var materials = DisjointResultCalculator.Calculate(metadata);
                if (materials.Count == 0)
                {
                    result = CreateDisjointErrorResult(request, DisjointItemResult.ErrorInvalidTarget);
                    result.SourceItemTemplateId = source.ItemTemplateId;
                    return false;
                }

                _db.DeleteItem(connection, transaction, source.ItemUid);
                _auditLogger.WriteDeleteAuditLog(connection, transaction, characterId, source, 1);

                CommonInventoryItem disjointToolRefresh = null;
                if (disjointTool != null)
                    disjointToolRefresh = ConsumePortableDisjointItem(connection, transaction, characterId, disjointTool);

                foreach (var material in materials)
                {
                    if (!TryPickupItemCore(connection, transaction, characterId, accountId, material.ItemTemplateId, material.Count, out var assignedSlot))
                    {
                        result = CreateDisjointErrorResult(request, DisjointItemResult.ErrorInventoryFull);
                        result.SourceItemTemplateId = source.ItemTemplateId;
                        return false;
                    }

                    material.SlotIndex = assignedSlot;
                }

                transaction.Commit();

                result = new DisjointItemResult
                {
                    Request = request,
                    ErrorCode = 0,
                    SourceItemTemplateId = source.ItemTemplateId,
                };
                result.RefreshItems.Add(new CommonInventoryItem { SlotIndex = request.TargetSlotIndex });
                if (disjointToolRefresh != null)
                    result.RefreshItems.Add(disjointToolRefresh);
                result.Materials.AddRange(materials);
                AddRefreshItems(connection, characterId, accountId, result);
                return true;
                }
            }
        }

        private void AddRefreshItems(SqliteConnection connection, int characterId, int accountId, DisjointItemResult result)
        {
            if (result == null)
                return;

            foreach (var material in result.Materials)
            {
                if (material.SlotIndex < 0)
                    continue;

                var item = LoadCommonRefreshItem(connection, null, characterId, accountId, material.SlotIndex, material.ItemTemplateId);
                if (item != null)
                    result.RefreshItems.Add(item);
            }
        }

        private CommonInventoryItem LoadCommonRefreshItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, int accountId, short slotIndex, int itemTemplateId)
        {
            if (Game.Currency.CurrencyService.IsCubeFragment(itemTemplateId))
            {
                var cubes = Game.Currency.CurrencyService.LoadCubeFragments(connection, transaction, accountId);
                foreach (var cube in cubes)
                {
                    if (cube.ItemId == itemTemplateId)
                    {
                        return new CommonInventoryItem
                        {
                            SlotIndex = (short)cube.Slot,
                            ItemTemplateId = cube.ItemId,
                            CountOrInstanceValue = cube.Count,
                        };
                    }
                }

                return new CommonInventoryItem
                {
                    SlotIndex = slotIndex,
                    ItemTemplateId = itemTemplateId,
                    CountOrInstanceValue = 0,
                };
            }

            return _db.LoadCommonItem(connection, transaction, characterId, InventoryListType.Main, slotIndex);
        }

        private static bool TryValidateDisjoint(ItemRecord source, ItemMetadata metadata, out byte errorCode)
        {
            errorCode = DisjointItemResult.ErrorInvalidTarget;
            if (source == null || metadata == null)
                return false;

            if (!string.Equals(source.ItemKind, "equipment", StringComparison.Ordinal)
                || !string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal))
                return false;

            if (ContainsImpossibleContent(metadata, "disjoint"))
                return false;

            if (IsTradeDeleteAttachType(metadata.AttachType))
                return false;

            if (IsUnidentifiedAmplifyEquipment(source))
                return false;

            return true;
        }

        private bool TryValidatePortableDisjointItem(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            DisjointItemRequest request,
            ItemMetadata targetMetadata,
            out ItemRecord disjointTool,
            out byte errorCode)
        {
            disjointTool = null;
            errorCode = DisjointItemResult.ErrorInvalidTarget;

            if (request.DisjointItemSlotIndex == -1)
                return true;

            if (request.DisjointItemSlotIndex == request.TargetSlotIndex)
                return false;

            disjointTool = _db.LoadItemRecord(connection, transaction, characterId, InventoryListType.Main, request.DisjointItemSlotIndex);
            if (disjointTool == null
                || disjointTool.StackCount <= 0
                || !string.Equals(disjointTool.ItemKind, "stackable", StringComparison.Ordinal))
                return false;

            if (!ItemMetadataResolver.TryLoadStackableFile(disjointTool.ItemTemplateId, out var stackable))
                return false;

            var maxLevel = GetPortableDisjointMaxLevel(stackable.PortableDisjoint);
            if (maxLevel < 0)
                return false;

            // 便携分解机沿用 NPC 分解规则，但按 [portable disjoint] 限制可分解装备等级。
            var targetLevel = Math.Max(0, targetMetadata?.MinimumLevel ?? 0);
            return targetLevel <= maxLevel;
        }

        private CommonInventoryItem ConsumePortableDisjointItem(SqliteConnection connection, SqliteTransaction transaction, int characterId, ItemRecord disjointTool)
        {
            var remainingCount = disjointTool.StackCount - 1;
            if (remainingCount > 0)
            {
                _db.UpdateStackCount(connection, transaction, disjointTool.ItemUid, remainingCount);
                return _db.LoadCommonItem(connection, transaction, characterId, InventoryListType.Main, disjointTool.SlotIndex)
                    ?? new CommonInventoryItem
                    {
                        SlotIndex = disjointTool.SlotIndex,
                        ItemTemplateId = disjointTool.ItemTemplateId,
                        CountOrInstanceValue = remainingCount,
                    };
            }

            _db.DeleteItem(connection, transaction, disjointTool.ItemUid);
            return new CommonInventoryItem { SlotIndex = disjointTool.SlotIndex };
        }

        private static int GetPortableDisjointMaxLevel(int portableDisjoint)
        {
            switch (portableDisjoint)
            {
                case 0: return 30;
                case 1: return 50;
                case 2: return 70;
                case 3: return 85;
                default: return -1;
            }
        }

        private static bool IsUnidentifiedAmplifyEquipment(ItemRecord source)
        {
            var prefix = InventoryItemCodec.ReadHexValue(source.ExtraJson ?? "{}", "prefixData0E", 8);
            var amplifyType = prefix.Length >= 6 ? prefix[5] : (byte)0;

            // 最高位为未鉴定标志，低 7 位保留增幅属性类型。
            return (amplifyType & 0x80) != 0;
        }

        private static bool ContainsImpossibleContent(ItemMetadata metadata, string expected)
        {
            if (metadata.ImpossibleContents == null)
                return false;

            foreach (var item in metadata.ImpossibleContents)
            {
                if (string.Equals(NormalizePvfToken(item), expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsTradeDeleteAttachType(string attachType)
        {
            return string.Equals(NormalizePvfToken(attachType), "trade delete", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePvfToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim().Trim('`').Trim();
            if (normalized.Length >= 2 && normalized[0] == '[' && normalized[normalized.Length - 1] == ']')
                normalized = normalized.Substring(1, normalized.Length - 2);

            return normalized.Trim();
        }

        private static DisjointItemResult CreateDisjointErrorResult(DisjointItemRequest request, byte errorCode)
        {
            return new DisjointItemResult
            {
                Request = request,
                ErrorCode = errorCode,
            };
        }
    }
}
