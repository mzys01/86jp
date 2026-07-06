using DfoServer.Game.Inventory;
using DfoServer.Network;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class ItemListUpdateBuilder
    {
        public static byte[] BuildCommonUpdates(IReadOnlyList<CommonInventoryItem> items)
            => BuildCommonUpdates(InventoryListType.Main, items);

        public static byte[] BuildCommonUpdates(InventoryListType itemSpace, IReadOnlyList<CommonInventoryItem> items)
        {
            EnsureCommonUpdateItemSpace(itemSpace);

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)itemSpace);
            writer.WriteUInt16((ushort)(items != null ? items.Count : 0));

            if (items != null)
            {
                foreach (var item in items)
                    WriteCommonUpdateEntry(writer, item);
            }

            return writer.ToArray();
        }

        public static byte[] BuildCommonSlotUpdate(short slotIndex, int itemTemplateId, int countOrInstanceValue)
            => BuildCommonSlotUpdate(InventoryListType.Main, slotIndex, itemTemplateId, countOrInstanceValue);

        public static byte[] BuildCommonSlotUpdate(InventoryListType itemSpace, short slotIndex, int itemTemplateId, int countOrInstanceValue)
            => BuildCommonUpdates(itemSpace, new[] { CreateCommonSlotUpdate(slotIndex, itemTemplateId, countOrInstanceValue) });

        public static byte[] BuildGoldUpdate(int gold)
            => BuildCommonSlotUpdate(InventoryListType.Main, 0, 0, gold);

        // NOTI 14 UPDATE_ITEM_LIST - 84B raw item entry (Reverse/SUBSYSTEMS/inventory_system.md)
        // 客户端按 PacketPopBuffer(84) 整块 memcpy, 不逐字段读取。全仓 84B 条目构造只此一处。
        public static byte[] BuildRawItemEntry(short slotIndex, uint itemId, uint instanceValue)
        {
            var buf = new byte[84];
            BitConverter.GetBytes(slotIndex).CopyTo(buf, 0);
            BitConverter.GetBytes(itemId).CopyTo(buf, 2);
            BitConverter.GetBytes(instanceValue).CopyTo(buf, 6);
            return buf;
        }

        public static byte[] BuildRawEquipEntry(short slotIndex, uint itemId,
            uint qualitySeed = Game.Inventory.ItemQuality.TopQualitySeed, ushort durability = 32)
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

        public static byte[] BuildPetUpdates(IReadOnlyList<PetInventoryItem> items)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte((byte)InventoryListType.Pet);
            writer.WriteUInt16((ushort)(items != null ? items.Count : 0));

            if (items != null)
            {
                foreach (var item in items)
                    ItemListPacketBuilder.WritePetEntry(writer, item);
            }

            return writer.ToArray();
        }

        public static byte[] BuildEquipmentUpdates(IReadOnlyList<CommonInventoryItem> items)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)InventoryListType.Equipment);
            writer.WriteUInt16((ushort)(items != null ? items.Count : 0));

            if (items != null)
            {
                foreach (var item in items)
                    WriteCommonUpdateEntry(writer, item);
            }

            return writer.ToArray();
        }

        public static byte[] BuildAvatarUpdates(IReadOnlyList<AvatarInventoryItem> items)
            => BuildAvatarUpdates(InventoryListType.Avatar, items);

        public static byte[] BuildAvatarUpdates(InventoryListType itemSpace, IReadOnlyList<AvatarInventoryItem> items)
        {
            EnsureAvatarUpdateItemSpace(itemSpace);

            var writer = new GamePacketWriter();

            writer.WriteByte((byte)itemSpace);
            writer.WriteUInt16((ushort)(items != null ? items.Count : 0));

            if (items != null)
            {
                foreach (var item in items)
                    ItemListPacketBuilder.WriteAvatarEntry(writer, item);
            }

            return writer.ToArray();
        }

        public static byte[] BuildEmptyUpdates(InventoryListType itemSpace, IReadOnlyList<short> slotIndexes)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)itemSpace);
            writer.WriteUInt16((ushort)(slotIndexes != null ? slotIndexes.Count : 0));

            if (slotIndexes != null)
            {
                var entrySize = GetUpdateEntrySize(itemSpace);
                foreach (var slotIndex in slotIndexes)
                    WriteEmptyEntry(writer, slotIndex, entrySize);
            }

            return writer.ToArray();
        }

        public static void WriteCommonUpdateEntry(GamePacketWriter writer, CommonInventoryItem item)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (item == null) throw new ArgumentNullException(nameof(item));

            writer.WriteInt16(item.SlotIndex);
            if (item.ItemTemplateId < 0)
            {
                writer.WriteInt32(-1);
                WriteEmptyEntryTail(writer, 0x54);
                return;
            }

            writer.WriteInt32(item.ItemTemplateId);
            writer.WriteInt32(item.CountOrInstanceValue);
            writer.WriteByte(item.ExtData0);
            writer.WriteUInt16(item.Durability);
            writer.WriteByte(item.SealFlag);
            WriteFixedBytes(writer, item.PrefixData0E, 8);
            writer.WriteInt32(item.Marker16);
            WriteFixedBytes(writer, item.MiddleData1A, 17);
            writer.WriteInt32(item.ExpireTime);
            WriteFixedBytes(writer, item.TailData2F, 37);
        }

        public static CommonInventoryItem CreateEmptyCommonItem(short slotIndex)
        {
            return new CommonInventoryItem
            {
                SlotIndex = slotIndex,
                ItemTemplateId = -1,
                CountOrInstanceValue = 0,
            };
        }

        public static CommonInventoryItem CreateCommonSlotUpdate(short slotIndex, int itemTemplateId, int countOrInstanceValue)
        {
            var normalizedItemId = countOrInstanceValue > 0 || itemTemplateId <= 0 ? itemTemplateId : -1;
            var normalizedCount = countOrInstanceValue > 0 ? countOrInstanceValue : 0;
            return new CommonInventoryItem
            {
                SlotIndex = slotIndex,
                ItemTemplateId = normalizedItemId,
                CountOrInstanceValue = normalizedCount,
            };
        }

        public static InventoryPacketType GetInventoryPacketTypeFromItemSpace(InventoryListType itemSpace)
        {
            switch ((byte)itemSpace)
            {
                case 0:
                    return InventoryPacketType.Item;
                case 1:
                    return InventoryPacketType.Avatar;
                case 2:
                    return InventoryPacketType.Cargo;
                case 3:
                    return InventoryPacketType.Body;
                case 7:
                    return InventoryPacketType.Creature;
                case 18:
                    return InventoryPacketType.Unknown5;
                default:
                    return InventoryPacketType.Default;
            }
        }

        public static bool IsCommonUpdateItemSpace(InventoryListType itemSpace)
        {
            return itemSpace == InventoryListType.Main
                || itemSpace == InventoryListType.PersonalCargo
                || itemSpace == InventoryListType.AccountCargo;
        }

        private static void EnsureCommonUpdateItemSpace(InventoryListType itemSpace)
        {
            if (!IsCommonUpdateItemSpace(itemSpace))
                throw new ArgumentOutOfRangeException(nameof(itemSpace), itemSpace, "Unsupported common item update space.");
        }

        private static void EnsureAvatarUpdateItemSpace(InventoryListType itemSpace)
        {
            if (itemSpace != InventoryListType.Avatar && itemSpace != InventoryListType.Equipment)
                throw new ArgumentOutOfRangeException(nameof(itemSpace), itemSpace, "Unsupported avatar item update space.");
        }

        private static int GetUpdateEntrySize(InventoryListType itemSpace)
        {
            switch (itemSpace)
            {
                case InventoryListType.Avatar:
                    return 0x7E;
                case InventoryListType.Equipment:
                case InventoryListType.Main:
                case InventoryListType.PersonalCargo:
                case InventoryListType.AccountCargo:
                case InventoryListType.Pet:
                    return 0x54;
                default:
                    return 0x54;
            }
        }

        private static void WriteEmptyEntry(GamePacketWriter writer, short slotIndex, int entrySize)
        {
            // 空槽刷新先按通用结构测试：slot + item_id=-1 + 剩余字节清零。
            writer.WriteInt16(slotIndex);
            writer.WriteInt32(-1);
            WriteEmptyEntryTail(writer, entrySize);
        }

        private static void WriteEmptyEntryTail(GamePacketWriter writer, int entrySize)
        {
            writer.WriteZeroBytes(entrySize - 6);
        }

        private static void WriteFixedBytes(GamePacketWriter writer, byte[] value, int length)
        {
            if (value == null || value.Length == 0)
            {
                writer.WriteZeroBytes(length);
                return;
            }

            if (value.Length == length)
            {
                writer.WriteBytes(value);
                return;
            }

            var buffer = new byte[length];
            Array.Copy(value, 0, buffer, 0, Math.Min(value.Length, length));
            writer.WriteBytes(buffer);
        }
    }
}
