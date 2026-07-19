using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public enum InventoryListType : byte
    {
        Main = 0,
        Avatar = 1,
        PersonalCargo = 2,
        Equipment = 3,
        Pet = 7,
        AccountCargo = 12,
        QuickSlot = 29,
        KnightShieldEquipped = 33,
        KnightShieldCatalog = 34,
    }

    // ITEM_LIST/UPDATE_ITEM_LIST 的普通 0x54 entry 协议 DTO。
    // 业务逻辑不要直接读写 PrefixData0E/MiddleData1A/TailData2F 等协议字段；
    // 新业务应通过 InventoryItemView 表达语义，发包前再映射到此 DTO。
    public sealed class CommonInventoryItem
    {
        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int CountOrInstanceValue { get; set; }

        public byte ExtData0 { get; set; }

        public ushort Durability { get; set; }

        public byte SealFlag { get; set; }

        public byte[] PrefixData0E { get; set; } = new byte[8];

        public int Marker16 { get; set; }

        public byte[] MiddleData1A { get; set; } = new byte[17];

        public int ExpireTime { get; set; }

        public byte[] TailData2F { get; set; } = new byte[37];

        public byte[] JewelSocket { get; set; } = new byte[30];

        public byte EquipmentLockId { get; set; }
    }

    // ITEM_LIST/UPDATE_ITEM_LIST 的时装协议 DTO，只用于初始化和刷新包构造边界。
    // 业务逻辑不要把 Reserved0/Reserved1/Reserved2 当作业务模型直接操作。
    public sealed class AvatarInventoryItem
    {
        private byte[] _prefixData0E = new byte[8];
        private byte[] _middleData1A = new byte[17];
        private byte[] _tailData2F = new byte[37];
        private byte[] _avatarSocketData = new byte[30];
        private int _colorDataLen = 4;

        public short SlotIndex { get; set; }

        public int AvatarItemId { get; set; }

        public int RemainingSeconds { get; set; }

        public byte Attr { get; set; }

        public ushort AbilityNo { get; set; }

        public byte SealFlag { get; set; }

        public byte[] PrefixData0E
        {
            get => CharacterItemListSnapshot.Slice(_prefixData0E, 0, _prefixData0E.Length);
            set => _prefixData0E = Normalize(value, 8);
        }

        public int Marker16 { get; set; }

        public byte[] MiddleData1A
        {
            get => CharacterItemListSnapshot.Slice(_middleData1A, 0, _middleData1A.Length);
            set => _middleData1A = Normalize(value, 17);
        }

        public int ExpireTime { get; set; }

        public byte[] TailData2F
        {
            get => CharacterItemListSnapshot.Slice(_tailData2F, 0, _tailData2F.Length);
            set => _tailData2F = Normalize(value, 37);
        }

        public byte SortLockFlag
        {
            get => _tailData2F[36];
            set => _tailData2F[36] = value;
        }

        public int AvatarSocketLen => _avatarSocketData.Length;

        public byte[] AvatarSocketData
        {
            get => CharacterItemListSnapshot.Slice(_avatarSocketData, 0, _avatarSocketData.Length);
            set => _avatarSocketData = AvatarSocketDataCodec.Normalize(value);
        }

        public int ColorDataLen
        {
            get => _colorDataLen;
            set => _colorDataLen = value <= 0 ? 4 : value;
        }

        public ushort Color1 { get; set; }

        public ushort Color2 { get; set; }

        public byte[] Reserved0
        {
            get
            {
                var data = new byte[5];
                BitConverter.GetBytes(RemainingSeconds).CopyTo(data, 0);
                data[4] = Attr;
                return data;
            }
            set
            {
                var data = Normalize(value, 5);
                RemainingSeconds = BitConverter.ToInt32(data, 0);
                Attr = data[4];
            }
        }

        public byte OptionValue
        {
            get => (byte)(AbilityNo & 0xFF);
            set => AbilityNo = (ushort)((AbilityNo & 0xFF00) | value);
        }

        public byte[] Reserved1
        {
            get
            {
                var data = new byte[71];
                data[0] = (byte)((AbilityNo >> 8) & 0xFF);
                data[1] = SealFlag;
                Buffer.BlockCopy(_prefixData0E, 0, data, 2, 8);
                BitConverter.GetBytes(Marker16).CopyTo(data, 10);
                Buffer.BlockCopy(_middleData1A, 0, data, 14, 17);
                BitConverter.GetBytes(ExpireTime).CopyTo(data, 31);
                Buffer.BlockCopy(_tailData2F, 0, data, 35, 36);
                return data;
            }
            set
            {
                var data = Normalize(value, 71);
                AbilityNo = (ushort)((AbilityNo & 0x00FF) | (data[0] << 8));
                SealFlag = data[1];
                Buffer.BlockCopy(data, 2, _prefixData0E, 0, 8);
                Marker16 = BitConverter.ToInt32(data, 10);
                Buffer.BlockCopy(data, 14, _middleData1A, 0, 17);
                ExpireTime = BitConverter.ToInt32(data, 31);
                Buffer.BlockCopy(data, 35, _tailData2F, 0, 36);
            }
        }

        public int UnknownFixed30
        {
            get => SortLockFlag | (AvatarSocketLen << 8);
            set => SortLockFlag = (byte)(value & 0xFF);
        }

        public byte[] Reserved2
        {
            get => AvatarSocketData;
            set => AvatarSocketData = value;
        }

        public ushort UnknownFixed4
        {
            get => (ushort)(ColorDataLen << 8);
            set
            {
                if (value == 0)
                    ColorDataLen = 4;
                else if ((value & 0xFF00) != 0 && (value & 0x00FF) == 0)
                    ColorDataLen = value >> 8;
                else
                    ColorDataLen = value;
            }
        }

        public byte[] TailData
        {
            get
            {
                var data = new byte[7];
                BitConverter.GetBytes(Color1).CopyTo(data, 0);
                BitConverter.GetBytes(Color2).CopyTo(data, 2);
                return data;
            }
            set
            {
                var data = Normalize(value, 7);
                Color1 = BitConverter.ToUInt16(data, 0);
                Color2 = BitConverter.ToUInt16(data, 2);
            }
        }

        private static byte[] Normalize(byte[] source, int length)
        {
            var data = new byte[length];
            if (source != null && source.Length > 0)
                Buffer.BlockCopy(source, 0, data, 0, Math.Min(source.Length, length));
            return data;
        }
    }

    // ITEM_LIST/UPDATE_ITEM_LIST 的宠物协议 DTO，只用于初始化和刷新包构造边界。
    // 宠物业务状态应从宠物实例/详情模型读取，再在发包前映射到此 DTO。
    public sealed class PetInventoryItem
    {
        private byte[] _prefixData0E = new byte[8];
        private byte[] _middleData1A = new byte[17];
        private byte[] _tailData2F = new byte[37];

        public short SlotIndex { get; set; }

        public int CreatureItemId { get; set; }

        public int CreatureSerialOrHandle { get; set; }

        public int CreatureUid
        {
            get => CreatureSerialOrHandle;
            set => CreatureSerialOrHandle = value;
        }

        public byte Attr { get; set; }

        public ushort Durability { get; set; }

        public byte SealFlag { get; set; }

        public byte[] PrefixData0E
        {
            get => CharacterItemListSnapshot.Slice(_prefixData0E, 0, _prefixData0E.Length);
            set => _prefixData0E = Normalize(value, 8);
        }

        public int EnchantCardId
        {
            get => BitConverter.ToInt32(_prefixData0E, 0);
            set => BitConverter.GetBytes(value).CopyTo(_prefixData0E, 0);
        }

        public byte EnchantUpgradeCount
        {
            get => _prefixData0E[4];
            set => _prefixData0E[4] = value;
        }

        public byte AmplifyType
        {
            get => _prefixData0E[5];
            set => _prefixData0E[5] = value;
        }

        public ushort AmplifyValue
        {
            get => BitConverter.ToUInt16(_prefixData0E, 6);
            set => BitConverter.GetBytes(value).CopyTo(_prefixData0E, 6);
        }

        public int Marker16 { get; set; }

        public byte[] MiddleData1A
        {
            get => CharacterItemListSnapshot.Slice(_middleData1A, 0, _middleData1A.Length);
            set => _middleData1A = Normalize(value, 17);
        }

        public int ExpireTime { get; set; }

        public byte[] TailData2F
        {
            get => CharacterItemListSnapshot.Slice(_tailData2F, 0, _tailData2F.Length);
            set => _tailData2F = Normalize(value, 37);
        }

        public byte GenuineUpgrade
        {
            get => _tailData2F[27];
            set => _tailData2F[27] = value;
        }

        public byte EmancipateEquipmentLevel
        {
            get => _tailData2F[28];
            set => _tailData2F[28] = value;
        }

        public byte TradeRestriction
        {
            get => _tailData2F[29];
            set => _tailData2F[29] = value;
        }

        public byte RemainUseCount
        {
            get => _tailData2F[35];
            set => _tailData2F[35] = value;
        }

        public byte RemainingUseCount
        {
            get => RemainUseCount;
            set => RemainUseCount = value;
        }

        public byte SortLockFlag
        {
            get => _tailData2F[36];
            set => _tailData2F[36] = value;
        }

        public byte SortLock
        {
            get => SortLockFlag;
            set => SortLockFlag = value;
        }

        public byte[] TailData0A
        {
            get
            {
                var data = new byte[74];
                data[0] = Attr;
                BitConverter.GetBytes(Durability).CopyTo(data, 1);
                data[3] = SealFlag;
                Buffer.BlockCopy(_prefixData0E, 0, data, 4, 8);
                BitConverter.GetBytes(Marker16).CopyTo(data, 12);
                Buffer.BlockCopy(_middleData1A, 0, data, 16, 17);
                BitConverter.GetBytes(ExpireTime).CopyTo(data, 33);
                Buffer.BlockCopy(_tailData2F, 0, data, 37, 37);
                return data;
            }
            set
            {
                var data = Normalize(value, 74);
                Attr = data[0];
                Durability = BitConverter.ToUInt16(data, 1);
                SealFlag = data[3];
                Buffer.BlockCopy(data, 4, _prefixData0E, 0, 8);
                Marker16 = BitConverter.ToInt32(data, 12);
                Buffer.BlockCopy(data, 16, _middleData1A, 0, 17);
                ExpireTime = BitConverter.ToInt32(data, 33);
                Buffer.BlockCopy(data, 37, _tailData2F, 0, 37);
            }
        }

        private static byte[] Normalize(byte[] source, int length)
        {
            var data = new byte[length];
            if (source != null && source.Length > 0)
                Buffer.BlockCopy(source, 0, data, 0, Math.Min(source.Length, length));
            return data;
        }
    }

    internal static class AvatarSocketDataCodec
    {
        public const int Length = 30;

        public static byte[] Normalize(byte[] source)
        {
            var data = new byte[Length];
            if (source != null && source.Length > 0)
                Buffer.BlockCopy(source, 0, data, 0, Math.Min(source.Length, Length));

            return NormalizeCanonicalSocketTypes(LooksLikeLegacyShifted(data) ? ConvertLegacyShiftedToCanonical(data) : data);
        }

        public static ushort NormalizeSocketType(ushort socketType)
        {
            return socketType == 0x00EF ? (ushort)0xFFEF : socketType;
        }

        private static byte[] NormalizeCanonicalSocketTypes(byte[] data)
        {
            for (var i = 0; i < 5; i++)
            {
                var offset = i * 6;
                var socketType = BitConverter.ToUInt16(data, offset);
                var normalized = NormalizeSocketType(socketType);
                if (normalized != socketType)
                    BitConverter.GetBytes(normalized).CopyTo(data, offset);
            }

            return data;
        }

        private static bool LooksLikeLegacyShifted(byte[] data)
        {
            for (var i = 0; i < 5; i++)
            {
                var offset = i * 6;
                if (data[offset] == 0 && IsKnownSocketType(data[offset + 1]))
                    return true;
            }

            return false;
        }

        private static byte[] ConvertLegacyShiftedToCanonical(byte[] legacy)
        {
            var data = new byte[Length];
            for (var i = 0; i < 5; i++)
            {
                var offset = i * 6;
                if (legacy[offset] == 0 && IsKnownSocketType(legacy[offset + 1]))
                {
                    data[offset] = legacy[offset + 1];
                    data[offset + 1] = legacy[offset + 2];
                    Buffer.BlockCopy(legacy, offset + 3, data, offset + 2, 3);
                }
                else
                {
                    Buffer.BlockCopy(legacy, offset, data, offset, 6);
                }
            }

            return data;
        }

        private static bool IsKnownSocketType(byte type)
        {
            return type == 0x01
                || type == 0x02
                || type == 0x04
                || type == 0x08
                || type == 0x10
                || type == 0xEF;
        }
    }

    public sealed class AccountCargoStateSnapshot
    {
        public ushort SelectionKey { get; set; }

        public ushort ItemCount { get; set; }

        public int Value32 { get; set; }
    }

    // 选角/进图 ITEM_LIST 的协议快照，不是运行时物品业务模型。
    // handler 不应长期依赖它反查业务状态；需要刷新时应从业务结果或记录重新映射。
    public sealed class CharacterItemListSnapshot
    {
        public ushort MainListParam16 { get; set; }

        public ushort AvatarListParam16 { get; set; }

        public ushort PersonalCargoListParam16 { get; set; }

        public List<CommonInventoryItem> MainItems { get; } = new List<CommonInventoryItem>();

        public List<AvatarInventoryItem> AvatarItems { get; } = new List<AvatarInventoryItem>();

        public List<AvatarInventoryItem> EquipmentItems { get; } = new List<AvatarInventoryItem>();

        public List<CommonInventoryItem> PersonalCargoItems { get; } = new List<CommonInventoryItem>();

        public List<PetInventoryItem> PetItems { get; } = new List<PetInventoryItem>();

        public List<CommonInventoryItem> AccountCargoItems { get; } = new List<CommonInventoryItem>();

        public AccountCargoStateSnapshot AccountCargoState { get; set; } = new AccountCargoStateSnapshot();

        public static byte[] Slice(byte[] source, int offset, int length)
        {
            var buffer = new byte[length];
            Array.Copy(source, offset, buffer, 0, length);
            return buffer;
        }
    }

    public sealed class SortItemLockEntry
    {
        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public byte State { get; set; } = 1;
    }
}
