using DfoServer.Game.ItemUpgrade;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class CharmInventoryPolicy
    {
        internal static bool IsCharm(int itemTemplateId)
            => EquipmentTypeInfo.ParseOrUnknown(ItemMetadataResolver.ResolveEquipmentType(itemTemplateId)) == EquipmentType.Charm;

        internal static bool CanApplyQuickSlotMove(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            SqliteInventoryStore.ItemRecord source,
            SqliteInventoryStore.ItemRecord destination,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            InventoryListType destinationListType,
            short destinationSlotIndex)
        {
            var sourceIsCharm = source != null && IsCharm(source.ItemTemplateId);
            var destinationIsCharm = destination != null && IsCharm(destination.ItemTemplateId);
            if (!sourceIsCharm && !destinationIsCharm)
                return true;

            var movingCharmCount = 0;
            if (sourceIsCharm
                && IsQuickSlot(destinationListType, destinationSlotIndex))
                movingCharmCount++;
            if (destinationIsCharm
                && IsQuickSlot(sourceListType, sourceSlotIndex))
                movingCharmCount++;

            if (movingCharmCount > 1)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT item_template_id FROM character_items
WHERE character_id=@characterId AND list_type=@mainList
  AND slot_index BETWEEN @quickSlotStart AND @quickSlotEnd
  AND item_uid<>@sourceItemUid AND item_uid<>@destinationItemUid;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@mainList", (int)InventoryListType.Main);
                command.Parameters.AddWithValue("@quickSlotStart", SqliteInventoryStore.QuickSlotStart);
                command.Parameters.AddWithValue("@quickSlotEnd", SqliteInventoryStore.QuickSlotEnd);
                command.Parameters.AddWithValue("@sourceItemUid",
                    sourceListType == InventoryListType.Main ? source?.ItemUid ?? 0 : 0);
                command.Parameters.AddWithValue("@destinationItemUid",
                    destinationListType == InventoryListType.Main ? destination?.ItemUid ?? 0 : 0);
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        if (IsCharm(reader.GetInt32(0)) && ++movingCharmCount > 1)
                            return false;
            }
            return true;
        }

        private static bool IsQuickSlot(InventoryListType listType, short slotIndex)
            => listType == InventoryListType.Main
                && slotIndex >= SqliteInventoryStore.QuickSlotStart
                && slotIndex <= SqliteInventoryStore.QuickSlotEnd;
    }
}
