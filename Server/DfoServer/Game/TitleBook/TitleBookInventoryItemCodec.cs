using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Game.TitleBook
{
    public static class TitleBookInventoryItemCodec
    {
        public const int CommonNetworkSize = 84;
        public const int PersistedRecordSize = CommonNetworkSize + 1;
        public const int TitleBookListEntrySize = 22;

        public static TitleBookInventoryItem FromCommon(int category, ushort bookIndex, CommonInventoryItem common)
        {
            if (common == null) throw new ArgumentNullException(nameof(common));

            var prefix = Normalize(common.PrefixData0E, 8);
            var middle = Normalize(common.MiddleData1A, 17);

            return new TitleBookInventoryItem
            {
                Category = category,
                BookIndex = bookIndex,
                Slot = unchecked((ushort)common.SlotIndex),
                ItemId = common.ItemTemplateId,
                Value = common.CountOrInstanceValue,
                Attr = common.ExtData0,
                Durability = common.Durability,
                SealFlag = common.SealFlag,
                EnchantIndex = BitConverter.ToInt32(prefix, 0),
                EnchantUpgradeCount = prefix[4],
                AmplifyType = prefix[5],
                AmplifyValue = BitConverter.ToUInt16(prefix, 6),
                Marker16 = common.Marker16,
                Chronicle = DecodeChronicle(middle),
                ExpireTime = common.ExpireTime,
                TailData = Normalize(common.TailData2F, 37),
                EquipmentLockId = common.EquipmentLockId,
            };
        }

        public static CommonInventoryItem ToCommon(TitleBookInventoryItem item, short? slotOverride = null)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            var prefix = new byte[8];
            BitConverter.GetBytes(item.EnchantIndex).CopyTo(prefix, 0);
            prefix[4] = item.EnchantUpgradeCount;
            prefix[5] = item.AmplifyType;
            BitConverter.GetBytes(item.AmplifyValue).CopyTo(prefix, 6);

            return new CommonInventoryItem
            {
                SlotIndex = slotOverride ?? unchecked((short)item.Slot),
                ItemTemplateId = item.ItemId,
                CountOrInstanceValue = item.Value,
                ExtData0 = item.Attr,
                Durability = item.Durability,
                SealFlag = item.SealFlag,
                PrefixData0E = prefix,
                Marker16 = item.Marker16,
                MiddleData1A = EncodeChronicle(item.Chronicle),
                ExpireTime = item.ExpireTime,
                TailData2F = Normalize(item.TailData, 37),
                JewelSocket = new byte[30],
                EquipmentLockId = item.EquipmentLockId,
            };
        }

        public static TitleBookListEntrySnapshot ToListEntry(TitleBookInventoryItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            return new TitleBookListEntrySnapshot
            {
                SlotIndex = item.Slot,
                ItemId = item.ItemId,
                Value = item.Value,
                Attr = item.Attr,
                Durability = item.Durability,
                SealFlag = item.SealFlag,
                EnchantIndex = item.EnchantIndex,
                EnchantUpgradeCount = item.EnchantUpgradeCount,
                AmplifyType = item.AmplifyType,
                AmplifyValue = item.AmplifyValue,
            };
        }

        public static byte[] Serialize(TitleBookInventoryItem item)
        {
            if (item == null || item.IsEmpty)
                return new byte[PersistedRecordSize];

            var common = ToCommon(item);
            var buf = new byte[PersistedRecordSize];
            WriteInt16(buf, 0, common.SlotIndex);
            WriteInt32(buf, 2, common.ItemTemplateId);
            WriteInt32(buf, 6, common.CountOrInstanceValue);
            buf[10] = common.ExtData0;
            WriteUInt16(buf, 11, common.Durability);
            buf[13] = common.SealFlag;
            Buffer.BlockCopy(Normalize(common.PrefixData0E, 8), 0, buf, 14, 8);
            WriteInt32(buf, 22, common.Marker16);
            Buffer.BlockCopy(Normalize(common.MiddleData1A, 17), 0, buf, 26, 17);
            WriteInt32(buf, 43, common.ExpireTime);
            Buffer.BlockCopy(Normalize(common.TailData2F, 37), 0, buf, 47, 37);
            buf[CommonNetworkSize] = common.EquipmentLockId;
            return buf;
        }

        public static TitleBookInventoryItem Deserialize(int category, ushort bookIndex, byte[] record)
        {
            var data = Normalize(record, PersistedRecordSize);
            var itemId = BitConverter.ToInt32(data, 2);
            if (itemId <= 0)
                return CreateEmpty(category, bookIndex);

            var common = new CommonInventoryItem
            {
                SlotIndex = BitConverter.ToInt16(data, 0),
                ItemTemplateId = itemId,
                CountOrInstanceValue = BitConverter.ToInt32(data, 6),
                ExtData0 = data[10],
                Durability = BitConverter.ToUInt16(data, 11),
                SealFlag = data[13],
                PrefixData0E = Slice(data, 14, 8),
                Marker16 = BitConverter.ToInt32(data, 22),
                MiddleData1A = Slice(data, 26, 17),
                ExpireTime = BitConverter.ToInt32(data, 43),
                TailData2F = Slice(data, 47, 37),
                JewelSocket = new byte[30],
                EquipmentLockId = data[CommonNetworkSize],
            };

            return FromCommon(category, bookIndex, common);
        }

        public static TitleBookInventoryItem CreateEmpty(int category, ushort bookIndex)
        {
            return new TitleBookInventoryItem
            {
                Category = category,
                BookIndex = bookIndex,
                Slot = bookIndex,
                ItemId = -1,
            };
        }

        public static TitleBookChronicleData DecodeChronicle(byte[] raw)
        {
            var data = Normalize(raw, 17);
            var chronicle = new TitleBookChronicleData { Count = data[0] };
            var count = Math.Min(chronicle.Count, (byte)2);
            var off = 1;
            for (var i = 0; i < count; i++)
            {
                chronicle.Options.Add(new TitleBookChronicleOption
                {
                    OptionId = BitConverter.ToInt32(data, off),
                    CharacJob = data[off + 4],
                    FirstGrowType = data[off + 5],
                    EquipmentType = data[off + 6],
                    OptionNo = data[off + 7],
                });
                off += 8;
            }
            return chronicle;
        }

        public static byte[] EncodeChronicle(TitleBookChronicleData chronicle)
        {
            var data = new byte[17];
            if (chronicle == null)
                return data;

            var count = Math.Min(chronicle.Options.Count, 2);
            data[0] = (byte)Math.Min(chronicle.Count > 0 ? chronicle.Count : count, 2);
            var off = 1;
            for (var i = 0; i < count; i++)
            {
                var option = chronicle.Options[i];
                BitConverter.GetBytes(option.OptionId).CopyTo(data, off);
                data[off + 4] = option.CharacJob;
                data[off + 5] = option.FirstGrowType;
                data[off + 6] = option.EquipmentType;
                data[off + 7] = option.OptionNo;
                off += 8;
            }
            return data;
        }

        private static byte[] Slice(byte[] data, int offset, int length)
        {
            var result = new byte[length];
            if (data == null || offset >= data.Length)
                return result;

            Buffer.BlockCopy(data, offset, result, 0, Math.Min(length, data.Length - offset));
            return result;
        }

        private static byte[] Normalize(byte[] data, int length)
        {
            var result = new byte[length];
            if (data == null)
                return result;

            Buffer.BlockCopy(data, 0, result, 0, Math.Min(length, data.Length));
            return result;
        }

        private static void WriteInt16(byte[] buf, int offset, short value)
        {
            BitConverter.GetBytes(value).CopyTo(buf, offset);
        }

        private static void WriteInt32(byte[] buf, int offset, int value)
        {
            BitConverter.GetBytes(value).CopyTo(buf, offset);
        }

        private static void WriteUInt16(byte[] buf, int offset, ushort value)
        {
            BitConverter.GetBytes(value).CopyTo(buf, offset);
        }
    }
}
