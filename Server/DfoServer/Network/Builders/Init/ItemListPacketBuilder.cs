using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class ItemListPacketBuilder
    {
        public static IEnumerable<byte[]> BuildBodies(CharacterItemListSnapshot snapshot)
        {
            yield return BuildBody(snapshot, InventoryListType.Main);
            yield return BuildBody(snapshot, InventoryListType.Avatar);
            yield return BuildBody(snapshot, InventoryListType.PersonalCargo);
            yield return BuildBody(snapshot, InventoryListType.Pet);
            yield return BuildBody(snapshot, InventoryListType.AccountCargo);
        }

        public static byte[] BuildBody(CharacterItemListSnapshot snapshot, InventoryListType listType)
        {
            return BuildBody(snapshot, listType, false);
        }

        public static byte[] BuildBody(CharacterItemListSnapshot snapshot, InventoryListType listType, bool includeEquipment)
        {
            switch (listType)
            {
                case InventoryListType.Main:
                    return BuildCommonContainerBody(InventoryListType.Main, snapshot.MainListParam16, snapshot.MainItems);
                case InventoryListType.Avatar:
                    if (includeEquipment)
                        return BuildAvatarContainerBody(snapshot.AvatarListParam16, snapshot.AvatarItems, snapshot.EquipmentItems);
                    return BuildAvatarContainerBody(snapshot.AvatarListParam16, snapshot.AvatarItems, null);
                case InventoryListType.PersonalCargo:
                    return BuildCommonContainerBody(InventoryListType.PersonalCargo, snapshot.PersonalCargoListParam16, snapshot.PersonalCargoItems);
                case InventoryListType.Pet:
                    return BuildPetContainerBody(snapshot.PetItems);
                case InventoryListType.AccountCargo:
                    return BuildAccountCargoBody(snapshot.AccountCargoState, snapshot.AccountCargoItems);
                default:
                    throw new ArgumentOutOfRangeException(nameof(listType), listType, "Unsupported inventory list type.");
            }
        }

        private static byte[] BuildCommonContainerBody(InventoryListType listType, ushort listParam16, List<CommonInventoryItem> items)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)listType);
            writer.WriteUInt16(listParam16);
            writer.WriteUInt16((ushort)items.Count);

            foreach (var item in items)
            {
                writer.WriteInt16(item.SlotIndex);
                writer.WriteInt32(item.ItemTemplateId);
                writer.WriteInt32(item.CountOrInstanceValue);
                writer.WriteByte(item.ExtData0);
                writer.WriteUInt16(item.Durability);
                writer.WriteByte(item.SealFlag);
                writer.WriteBytes(item.PrefixData0E);
                writer.WriteInt32(item.Marker16);
                writer.WriteBytes(item.MiddleData1A);
                writer.WriteInt32(item.ExpireTime);
                writer.WriteBytes(item.TailData2F);
            }

            return writer.ToArray();
        }

        private static byte[] BuildAvatarContainerBody(ushort listParam16, List<AvatarInventoryItem> items, List<AvatarInventoryItem> equipmentItems)
        {
            var totalCount = items.Count + (equipmentItems != null ? equipmentItems.Count : 0);
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)InventoryListType.Avatar);
            writer.WriteUInt16(listParam16);
            writer.WriteUInt16((ushort)totalCount);

            foreach (var item in items)
            {
                WriteAvatarEntry(writer, item);
            }

            if (equipmentItems != null)
            {
                foreach (var item in equipmentItems)
                {
                    WriteAvatarEntry(writer, item);
                }
            }

            return writer.ToArray();
        }

        private static void WriteAvatarEntry(GamePacketWriter writer, AvatarInventoryItem item)
        {
            writer.WriteInt16(item.SlotIndex);
            writer.WriteInt32(item.AvatarItemId);
            writer.WriteBytes(item.Reserved0);
            writer.WriteByte(item.OptionValue);
            writer.WriteBytes(item.Reserved1);
            writer.WriteInt32(item.UnknownFixed30);
            writer.WriteBytes(item.Reserved2);
            writer.WriteUInt16(item.UnknownFixed4);
            writer.WriteBytes(item.TailData);
        }

        private static byte[] BuildPetContainerBody(List<PetInventoryItem> items)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)InventoryListType.Pet);
            writer.WriteUInt16((ushort)items.Count);

            foreach (var item in items)
            {
                writer.WriteInt16(item.SlotIndex);
                writer.WriteInt32(item.CreatureItemId);
                writer.WriteInt32(item.CreatureSerialOrHandle);
                writer.WriteBytes(item.TailData0A);
            }

            return writer.ToArray();
        }

        private static byte[] BuildAccountCargoBody(AccountCargoStateSnapshot state, List<CommonInventoryItem> items)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)InventoryListType.AccountCargo);
            writer.WriteUInt16(state.SelectionKey);
            writer.WriteUInt16(state.ItemCount);
            writer.WriteInt32(state.Value32);

            foreach (var item in items)
            {
                writer.WriteInt16(item.SlotIndex);
                writer.WriteInt32(item.ItemTemplateId);
                writer.WriteInt32(item.CountOrInstanceValue);
                writer.WriteByte(item.ExtData0);
                writer.WriteUInt16(item.Durability);
                writer.WriteByte(item.SealFlag);
                writer.WriteBytes(item.PrefixData0E);
                writer.WriteInt32(item.Marker16);
                writer.WriteBytes(item.MiddleData1A);
                writer.WriteInt32(item.ExpireTime);
                writer.WriteBytes(item.TailData2F);
            }

            return writer.ToArray();
        }
    }
}