using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace DfoServer.Game.Inventory
{
    // 业务侧物品语义入口：直接把 ItemRecord 解释成完整 84 字节视图。
    internal sealed class InventoryItemView
    {
        private readonly InventoryItemExtraPayload _extra;

        private InventoryItemView(InventoryItemViewKind kind, SqliteInventoryStore.ItemRecord record)
        {
            Kind = kind;
            Record = record ?? throw new ArgumentNullException(nameof(record));
            _extra = InventoryItemExtraPayload.Parse(record.ExtraJson);
            Entry84 = BuildEntry84(kind, record, _extra, SyncEntry84Change);
            Common84 = Entry84;
            AvatarDetail = kind == InventoryItemViewKind.Avatar
                ? BuildAvatarDetail(_extra)
                : InventoryAvatarDetailView.Empty;
        }

        public InventoryItemViewKind Kind { get; }

        public SqliteInventoryStore.ItemRecord Record { get; }

        public InventoryItemEntry84View Entry84 { get; }

        public InventoryItemEntry84View Common84 { get; }

        public InventoryAvatarDetailView AvatarDetail { get; }

        public byte[] PetTailData0A
        {
            get => InventoryItemViewBytes.Copy(_extra.PetTailData0A);
            set
            {
                _extra.SetPetTailData0A(value);
                Record.ExtraJson = _extra.Serialize();
            }
        }

        public int Value
        {
            get => Entry84.Value;
            set => Entry84.Value = value;
        }

        public byte Attr
        {
            get => Entry84.Attr;
            set => Entry84.Attr = value;
        }

        public byte Upgrade
        {
            get => Entry84.Upgrade;
            set => Entry84.Upgrade = value;
        }

        public byte ReSealCount
        {
            get => Entry84.ReSealCount;
            set => Entry84.ReSealCount = value;
        }

        public ushort Durability
        {
            get => Entry84.Durability;
            set => Entry84.Durability = value;
        }

        public ushort AbilityNo
        {
            get => Entry84.AbilityNo;
            set => Entry84.AbilityNo = value;
        }

        public byte SealFlag
        {
            get => Entry84.SealFlag;
            set => Entry84.SealFlag = value;
        }

        public int EnchantCardId
        {
            get => Entry84.EnchantCardId;
            set => Entry84.EnchantCardId = value;
        }

        public byte EnchantUpgradeCount
        {
            get => Entry84.EnchantUpgradeCount;
            set => Entry84.EnchantUpgradeCount = value;
        }

        public byte AmplifyType
        {
            get => Entry84.AmplifyType;
            set => Entry84.AmplifyType = value;
        }

        public ushort AmplifyValue
        {
            get => Entry84.AmplifyValue;
            set => Entry84.AmplifyValue = value;
        }

        public byte Forging
        {
            get => Entry84.Forging;
            set => Entry84.Forging = value;
        }

        public byte SortLockFlag
        {
            get => Entry84.SortLockFlag;
            set => Entry84.SortLockFlag = value;
        }

        public static InventoryItemView ForCommon(SqliteInventoryStore.ItemRecord record)
        {
            return new InventoryItemView(InventoryItemViewKind.Common, record);
        }

        public static InventoryItemView ForAvatar(SqliteInventoryStore.ItemRecord record)
        {
            return new InventoryItemView(InventoryItemViewKind.Avatar, record);
        }

        public static InventoryItemView ForPet(SqliteInventoryStore.ItemRecord record)
        {
            return new InventoryItemView(InventoryItemViewKind.Pet, record);
        }

        private static InventoryItemEntry84View BuildEntry84(
            InventoryItemViewKind kind,
            SqliteInventoryStore.ItemRecord record,
            InventoryItemExtraPayload extra,
            Action<InventoryItemEntry84View, InventoryItemEntry84Field> onChanged)
        {
            if (kind == InventoryItemViewKind.Avatar)
                return BuildAvatarEntry84(record, extra, onChanged);

            if (kind == InventoryItemViewKind.Pet)
                return BuildPetEntry84(record, extra, onChanged);

            return BuildCommonEntry84(record, extra, onChanged);
        }

        private static InventoryItemEntry84View BuildCommonEntry84(
            SqliteInventoryStore.ItemRecord record,
            InventoryItemExtraPayload extra,
            Action<InventoryItemEntry84View, InventoryItemEntry84Field> onChanged)
        {
            return new InventoryItemEntry84View(
                record.SlotIndex,
                record.ItemTemplateId,
                record.StackCount,
                extra.ExtData0,
                record.Durability,
                record.SealFlag,
                extra.PrefixData0E,
                record.Marker16,
                extra.MiddleData1A,
                record.ExpireTime,
                extra.TailData2F,
                extra.JewelSocket,
                onChanged);
        }

        private static InventoryItemEntry84View BuildAvatarEntry84(
            SqliteInventoryStore.ItemRecord record,
            InventoryItemExtraPayload extra,
            Action<InventoryItemEntry84View, InventoryItemEntry84Field> onChanged)
        {
            var tail = InventoryItemViewBytes.CopyRange(extra.AvatarReserved1, 35, 37);
            tail[36] = (byte)(record.Marker16 & 0xFF);

            return new InventoryItemEntry84View(
                record.SlotIndex,
                record.ItemTemplateId,
                InventoryItemViewBytes.ReadInt32(extra.AvatarReserved0, 0),
                InventoryItemViewBytes.ReadByte(extra.AvatarReserved0, 4),
                (ushort)(record.OptionValue | (InventoryItemViewBytes.ReadByte(extra.AvatarReserved1, 0) << 8)),
                InventoryItemViewBytes.ReadByte(extra.AvatarReserved1, 1),
                InventoryItemViewBytes.CopyRange(extra.AvatarReserved1, 2, 8),
                InventoryItemViewBytes.ReadInt32(extra.AvatarReserved1, 10),
                InventoryItemViewBytes.CopyRange(extra.AvatarReserved1, 14, 17),
                InventoryItemViewBytes.ReadInt32(extra.AvatarReserved1, 31),
                tail,
                Array.Empty<byte>(),
                onChanged);
        }

        private static InventoryItemEntry84View BuildPetEntry84(
            SqliteInventoryStore.ItemRecord record,
            InventoryItemExtraPayload extra,
            Action<InventoryItemEntry84View, InventoryItemEntry84Field> onChanged)
        {
            return new InventoryItemEntry84View(
                record.SlotIndex,
                record.ItemTemplateId,
                record.PetSerialOrHandle,
                InventoryItemViewBytes.ReadByte(extra.PetTailData0A, 0),
                InventoryItemViewBytes.ReadUInt16(extra.PetTailData0A, 1),
                InventoryItemViewBytes.ReadByte(extra.PetTailData0A, 3),
                InventoryItemViewBytes.CopyRange(extra.PetTailData0A, 4, 8),
                InventoryItemViewBytes.ReadInt32(extra.PetTailData0A, 12),
                InventoryItemViewBytes.CopyRange(extra.PetTailData0A, 16, 17),
                InventoryItemViewBytes.ReadInt32(extra.PetTailData0A, 33),
                InventoryItemViewBytes.CopyRange(extra.PetTailData0A, 37, 37),
                Array.Empty<byte>(),
                onChanged);
        }

        private InventoryAvatarDetailView BuildAvatarDetail(InventoryItemExtraPayload extra)
        {
            return new InventoryAvatarDetailView(
                extra.AvatarSocketData,
                NormalizeColorDataLength(extra.AvatarColorDataLength),
                extra.AvatarTailData,
                SyncAvatarDetailChange);
        }

        private void SyncAvatarDetailChange(InventoryAvatarDetailView detail, InventoryAvatarDetailField field)
        {
            switch (field)
            {
                case InventoryAvatarDetailField.SocketData:
                    _extra.SetAvatarSocketData(detail.AvatarSocketData);
                    break;
                case InventoryAvatarDetailField.ColorDataLength:
                    _extra.SetAvatarColorDataLength(detail.ColorDataLen);
                    break;
                case InventoryAvatarDetailField.ColorData:
                    _extra.SetAvatarTailData(detail.ColorData);
                    break;
            }

            Record.ExtraJson = _extra.Serialize();
        }

        private void SyncEntry84Change(InventoryItemEntry84View entry, InventoryItemEntry84Field field)
        {
            if (Kind == InventoryItemViewKind.Avatar)
                SyncAvatarEntry84Change(entry, field);
            else if (Kind == InventoryItemViewKind.Pet)
                SyncPetEntry84Change(entry, field);
            else
                SyncCommonEntry84Change(entry, field);
        }

        private void SyncCommonEntry84Change(InventoryItemEntry84View entry, InventoryItemEntry84Field field)
        {
            switch (field)
            {
                case InventoryItemEntry84Field.Value:
                    Record.StackCount = entry.Value;
                    return;
                case InventoryItemEntry84Field.Attr:
                    _extra.SetExtData0(entry.Attr);
                    break;
                case InventoryItemEntry84Field.Durability:
                    Record.Durability = entry.Durability;
                    return;
                case InventoryItemEntry84Field.SealFlag:
                    Record.SealFlag = entry.SealFlag;
                    return;
                case InventoryItemEntry84Field.PrefixData0E:
                    _extra.SetPrefixData0E(entry.PrefixData0E);
                    break;
                case InventoryItemEntry84Field.Marker16:
                    Record.Marker16 = entry.Marker16;
                    return;
                case InventoryItemEntry84Field.MiddleData1A:
                    _extra.SetMiddleData1A(entry.MiddleData1A);
                    break;
                case InventoryItemEntry84Field.ExpireTime:
                    Record.ExpireTime = entry.ExpireTime;
                    return;
                case InventoryItemEntry84Field.TailData2F:
                    _extra.SetTailData2F(entry.TailData2F);
                    break;
                case InventoryItemEntry84Field.JewelSocket:
                    _extra.SetJewelSocket(entry.JewelSocket);
                    break;
            }

            Record.ExtraJson = _extra.Serialize();
        }

        private void SyncAvatarEntry84Change(InventoryItemEntry84View entry, InventoryItemEntry84Field field)
        {
            switch (field)
            {
                case InventoryItemEntry84Field.Value:
                    InventoryItemViewBytes.WriteInt32(_extra.AvatarReserved0, 0, entry.Value);
                    _extra.SetAvatarReserved0(_extra.AvatarReserved0);
                    break;
                case InventoryItemEntry84Field.Attr:
                    _extra.AvatarReserved0[4] = entry.Attr;
                    _extra.SetAvatarReserved0(_extra.AvatarReserved0);
                    break;
                case InventoryItemEntry84Field.Durability:
                    Record.OptionValue = (byte)(entry.AbilityNo & 0xFF);
                    _extra.AvatarReserved1[0] = (byte)((entry.AbilityNo >> 8) & 0xFF);
                    _extra.SetAvatarReserved1(_extra.AvatarReserved1);
                    break;
                case InventoryItemEntry84Field.SealFlag:
                    _extra.AvatarReserved1[1] = entry.SealFlag;
                    _extra.SetAvatarReserved1(_extra.AvatarReserved1);
                    break;
                case InventoryItemEntry84Field.PrefixData0E:
                    InventoryItemViewBytes.CopyInto(entry.PrefixData0E, _extra.AvatarReserved1, 2, 8);
                    _extra.SetAvatarReserved1(_extra.AvatarReserved1);
                    break;
                case InventoryItemEntry84Field.Marker16:
                    InventoryItemViewBytes.WriteInt32(_extra.AvatarReserved1, 10, entry.Marker16);
                    _extra.SetAvatarReserved1(_extra.AvatarReserved1);
                    break;
                case InventoryItemEntry84Field.MiddleData1A:
                    InventoryItemViewBytes.CopyInto(entry.MiddleData1A, _extra.AvatarReserved1, 14, 17);
                    _extra.SetAvatarReserved1(_extra.AvatarReserved1);
                    break;
                case InventoryItemEntry84Field.ExpireTime:
                    InventoryItemViewBytes.WriteInt32(_extra.AvatarReserved1, 31, entry.ExpireTime);
                    _extra.SetAvatarReserved1(_extra.AvatarReserved1);
                    break;
                case InventoryItemEntry84Field.TailData2F:
                    var tail = entry.TailData2F;
                    InventoryItemViewBytes.CopyInto(tail, _extra.AvatarReserved1, 35, 36);
                    Record.Marker16 = (Record.Marker16 & ~0xFF) | tail[36];
                    _extra.SetAvatarReserved1(_extra.AvatarReserved1);
                    break;
            }

            Record.ExtraJson = _extra.Serialize();
        }

        private void SyncPetEntry84Change(InventoryItemEntry84View entry, InventoryItemEntry84Field field)
        {
            switch (field)
            {
                case InventoryItemEntry84Field.Value:
                    Record.PetSerialOrHandle = entry.Value;
                    return;
                case InventoryItemEntry84Field.Attr:
                    _extra.PetTailData0A[0] = entry.Attr;
                    break;
                case InventoryItemEntry84Field.Durability:
                    InventoryItemViewBytes.WriteUInt16(_extra.PetTailData0A, 1, entry.Durability);
                    break;
                case InventoryItemEntry84Field.SealFlag:
                    _extra.PetTailData0A[3] = entry.SealFlag;
                    break;
                case InventoryItemEntry84Field.PrefixData0E:
                    InventoryItemViewBytes.CopyInto(entry.PrefixData0E, _extra.PetTailData0A, 4, 8);
                    break;
                case InventoryItemEntry84Field.Marker16:
                    InventoryItemViewBytes.WriteInt32(_extra.PetTailData0A, 12, entry.Marker16);
                    break;
                case InventoryItemEntry84Field.MiddleData1A:
                    InventoryItemViewBytes.CopyInto(entry.MiddleData1A, _extra.PetTailData0A, 16, 17);
                    break;
                case InventoryItemEntry84Field.ExpireTime:
                    InventoryItemViewBytes.WriteInt32(_extra.PetTailData0A, 33, entry.ExpireTime);
                    break;
                case InventoryItemEntry84Field.TailData2F:
                    InventoryItemViewBytes.CopyInto(entry.TailData2F, _extra.PetTailData0A, 37, 37);
                    break;
            }

            _extra.SetPetTailData0A(_extra.PetTailData0A);
            Record.ExtraJson = _extra.Serialize();
        }

        private static int NormalizeColorDataLength(ushort colorLength)
        {
            if (colorLength == 0)
                return 4;

            if ((colorLength & 0xFF00) != 0 && (colorLength & 0x00FF) == 0)
                return colorLength >> 8;

            return colorLength;
        }
    }

    internal enum InventoryItemViewKind
    {
        Common,
        Avatar,
        Pet,
    }

    internal enum InventoryItemEntry84Field
    {
        Value,
        Attr,
        Durability,
        SealFlag,
        PrefixData0E,
        Marker16,
        MiddleData1A,
        ExpireTime,
        TailData2F,
        JewelSocket,
    }

    internal sealed class InventoryItemEntry84View
    {
        private readonly Action<InventoryItemEntry84View, InventoryItemEntry84Field> _onChanged;
        private byte[] _prefixData0E;
        private byte[] _middleData1A;
        private byte[] _tailData2F;
        private byte[] _jewelSocket;
        private int _value;
        private byte _attr;
        private ushort _durability;
        private byte _sealFlag;
        private int _marker16;
        private int _expireTime;

        internal InventoryItemEntry84View(
            short slotIndex,
            int itemTemplateId,
            int value,
            byte attr,
            ushort durability,
            byte sealFlag,
            byte[] prefixData0E,
            int marker16,
            byte[] middleData1A,
            int expireTime,
            byte[] tailData2F,
            byte[] jewelSocket,
            Action<InventoryItemEntry84View, InventoryItemEntry84Field> onChanged)
        {
            SlotIndex = slotIndex;
            ItemTemplateId = itemTemplateId;
            _value = value;
            _attr = attr;
            _durability = durability;
            _sealFlag = sealFlag;
            _prefixData0E = InventoryItemViewBytes.CopyFixed(prefixData0E, 8);
            _marker16 = marker16;
            _middleData1A = InventoryItemViewBytes.CopyFixed(middleData1A, 17);
            _expireTime = expireTime;
            _tailData2F = InventoryItemViewBytes.CopyFixed(tailData2F, 37);
            _jewelSocket = InventoryItemViewBytes.CopyFixed(jewelSocket, 30);
            _onChanged = onChanged;
        }

        public short SlotIndex { get; }

        public int ItemTemplateId { get; }

        public int Value
        {
            get => _value;
            set
            {
                _value = value;
                Notify(InventoryItemEntry84Field.Value);
            }
        }

        public byte Attr
        {
            get => _attr;
            set
            {
                _attr = value;
                Notify(InventoryItemEntry84Field.Attr);
            }
        }

        public byte Upgrade
        {
            get => (byte)(Attr & 0x1F);
            set => Attr = (byte)((Attr & 0xE0) | (value & 0x1F));
        }

        public byte ReSealCount
        {
            get => (byte)((Attr >> 5) & 0x07);
            set => Attr = (byte)((Attr & 0x1F) | ((value & 0x07) << 5));
        }

        public ushort Durability
        {
            get => _durability;
            set
            {
                _durability = value;
                Notify(InventoryItemEntry84Field.Durability);
            }
        }

        public ushort AbilityNo
        {
            get => Durability;
            set => Durability = value;
        }

        public byte SealFlag
        {
            get => _sealFlag;
            set
            {
                _sealFlag = value;
                Notify(InventoryItemEntry84Field.SealFlag);
            }
        }

        public byte[] PrefixData0E
        {
            get => InventoryItemViewBytes.Copy(_prefixData0E);
            set
            {
                _prefixData0E = InventoryItemViewBytes.CopyFixed(value, 8);
                Notify(InventoryItemEntry84Field.PrefixData0E);
            }
        }

        public int EnchantCardId
        {
            get => InventoryItemViewBytes.ReadInt32(_prefixData0E, 0);
            set
            {
                InventoryItemViewBytes.WriteInt32(_prefixData0E, 0, value);
                Notify(InventoryItemEntry84Field.PrefixData0E);
            }
        }

        public byte EnchantUpgradeCount
        {
            get => InventoryItemViewBytes.ReadByte(_prefixData0E, 4);
            set
            {
                _prefixData0E[4] = value;
                Notify(InventoryItemEntry84Field.PrefixData0E);
            }
        }

        public byte AmplifyType
        {
            get => InventoryItemViewBytes.ReadByte(_prefixData0E, 5);
            set
            {
                _prefixData0E[5] = value;
                Notify(InventoryItemEntry84Field.PrefixData0E);
            }
        }

        public ushort AmplifyValue
        {
            get => InventoryItemViewBytes.ReadUInt16(_prefixData0E, 6);
            set
            {
                InventoryItemViewBytes.WriteUInt16(_prefixData0E, 6, value);
                Notify(InventoryItemEntry84Field.PrefixData0E);
            }
        }

        public int Marker16
        {
            get => _marker16;
            set
            {
                _marker16 = value;
                Notify(InventoryItemEntry84Field.Marker16);
            }
        }

        public byte[] MiddleData1A
        {
            get => InventoryItemViewBytes.Copy(_middleData1A);
            set
            {
                _middleData1A = InventoryItemViewBytes.CopyFixed(value, 17);
                Notify(InventoryItemEntry84Field.MiddleData1A);
            }
        }

        public byte ChronicleOptionCount => InventoryItemViewBytes.ReadByte(_middleData1A, 0);

        public int ChronicleOption0Id => InventoryItemViewBytes.ReadInt32(_middleData1A, 1);

        public int ChronicleOption1Id => InventoryItemViewBytes.ReadInt32(_middleData1A, 5);

        public byte ChronicleOption0CharacJob => InventoryItemViewBytes.ReadByte(_middleData1A, 9);

        public byte ChronicleOption1CharacJob => InventoryItemViewBytes.ReadByte(_middleData1A, 10);

        public byte ChronicleOption0FirstGrowType => InventoryItemViewBytes.ReadByte(_middleData1A, 11);

        public byte ChronicleOption1FirstGrowType => InventoryItemViewBytes.ReadByte(_middleData1A, 12);

        public byte ChronicleOption0EquipmentType => InventoryItemViewBytes.ReadByte(_middleData1A, 13);

        public byte ChronicleOption1EquipmentType => InventoryItemViewBytes.ReadByte(_middleData1A, 14);

        public byte ChronicleOption0OptionNo => InventoryItemViewBytes.ReadByte(_middleData1A, 15);

        public byte ChronicleOption1OptionNo => InventoryItemViewBytes.ReadByte(_middleData1A, 16);

        public IReadOnlyList<InventoryChronicleOptionEntry> ChronicleOptions => InventoryItemViewBytes.ParseChronicleOptions(_middleData1A);

        public int ExpireTime
        {
            get => _expireTime;
            set
            {
                _expireTime = value;
                Notify(InventoryItemEntry84Field.ExpireTime);
            }
        }

        public byte[] TailData2F
        {
            get => InventoryItemViewBytes.Copy(_tailData2F);
            set
            {
                _tailData2F = InventoryItemViewBytes.CopyFixed(value, 37);
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte EmblemSocketCount
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 0);
            set
            {
                _tailData2F[0] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public int EmblemId1
        {
            get => InventoryItemViewBytes.ReadInt32(_tailData2F, 1);
            set
            {
                InventoryItemViewBytes.WriteInt32(_tailData2F, 1, value);
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public int EmblemId2
        {
            get => InventoryItemViewBytes.ReadInt32(_tailData2F, 5);
            set
            {
                InventoryItemViewBytes.WriteInt32(_tailData2F, 5, value);
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte[] EmblemData
        {
            get
            {
                var count = Math.Min(2, (int)EmblemSocketCount);
                if (count <= 0)
                    return Array.Empty<byte>();

                var data = new byte[1 + count * 4];
                data[0] = (byte)count;
                if (count > 0)
                    InventoryItemViewBytes.WriteInt32(data, 1, EmblemId1);
                if (count > 1)
                    InventoryItemViewBytes.WriteInt32(data, 5, EmblemId2);
                return data;
            }
            set
            {
                Array.Clear(_tailData2F, 0, Math.Min(9, _tailData2F.Length));
                if (value != null && value.Length > 0)
                    Buffer.BlockCopy(value, 0, _tailData2F, 0, Math.Min(value.Length, 9));
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public ushort Rune
        {
            get => InventoryItemViewBytes.ReadUInt16(_tailData2F, 9);
            set
            {
                InventoryItemViewBytes.WriteUInt16(_tailData2F, 9, value);
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte RandomOptionCount
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 11);
            set
            {
                _tailData2F[11] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte RandomOption0Type => InventoryItemViewBytes.ReadByte(_tailData2F, 12);

        public byte RandomOption1Type => InventoryItemViewBytes.ReadByte(_tailData2F, 13);

        public byte RandomOption2Type => InventoryItemViewBytes.ReadByte(_tailData2F, 14);

        public byte RandomOption0Value1 => InventoryItemViewBytes.ReadByte(_tailData2F, 15);

        public byte RandomOption1Value1 => InventoryItemViewBytes.ReadByte(_tailData2F, 16);

        public byte RandomOption2Value1 => InventoryItemViewBytes.ReadByte(_tailData2F, 17);

        public byte RandomOption0Value2 => InventoryItemViewBytes.ReadByte(_tailData2F, 18);

        public byte RandomOption1Value2 => InventoryItemViewBytes.ReadByte(_tailData2F, 19);

        public byte RandomOption2Value2 => InventoryItemViewBytes.ReadByte(_tailData2F, 20);

        public byte RandomOptionState
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 21);
            set
            {
                _tailData2F[21] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte RandomOptionChangedIndex
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 22);
            set
            {
                _tailData2F[22] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte RandomOptionChangeState
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 23);
            set
            {
                _tailData2F[23] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte RandomOptionChangeType
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 24);
            set
            {
                _tailData2F[24] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte RandomOptionChangeValue1
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 25);
            set
            {
                _tailData2F[25] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte RandomOptionChangeValue2
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 26);
            set
            {
                _tailData2F[26] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public IReadOnlyList<RandomOptionEntry> RandomOptions => InventoryItemViewBytes.ParseRandomOptions(_tailData2F);

        public void SetRandomOptions(IReadOnlyList<RandomOptionEntry> entries)
        {
            InventoryItemViewBytes.WriteRandomOptions(_tailData2F, entries);
            Notify(InventoryItemEntry84Field.TailData2F);
        }

        public byte GenuineUpgrade
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 27);
            set
            {
                _tailData2F[27] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte Forging
        {
            get => GenuineUpgrade;
            set => GenuineUpgrade = value;
        }

        public byte EmancipateEquipmentLevel
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 28);
            set
            {
                _tailData2F[28] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte TradeRestriction
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 29);
            set
            {
                _tailData2F[29] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public ushort TailUnknown0
        {
            get => InventoryItemViewBytes.ReadUInt16(_tailData2F, 30);
            set
            {
                InventoryItemViewBytes.WriteUInt16(_tailData2F, 30, value);
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte TailUnknown1
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 32);
            set
            {
                _tailData2F[32] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte TailUnknown2
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 33);
            set
            {
                _tailData2F[33] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte TailUnknown3
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 34);
            set
            {
                _tailData2F[34] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte RemainUseCount
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 35);
            set
            {
                _tailData2F[35] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte RemainingUseCount
        {
            get => RemainUseCount;
            set => RemainUseCount = value;
        }

        public byte SortLockFlag
        {
            get => InventoryItemViewBytes.ReadByte(_tailData2F, 36);
            set
            {
                _tailData2F[36] = value;
                Notify(InventoryItemEntry84Field.TailData2F);
            }
        }

        public byte SortLock
        {
            get => SortLockFlag;
            set => SortLockFlag = value;
        }

        public byte[] JewelSocket
        {
            get => InventoryItemViewBytes.Copy(_jewelSocket);
            set
            {
                _jewelSocket = InventoryItemViewBytes.CopyFixed(value, 30);
                Notify(InventoryItemEntry84Field.JewelSocket);
            }
        }

        public IReadOnlyList<InventoryJewelSocketEntry> JewelSockets => InventoryItemViewBytes.ParseJewelSockets(_jewelSocket);

        private void Notify(InventoryItemEntry84Field field)
        {
            _onChanged?.Invoke(this, field);
        }
    }

    internal sealed class InventoryAvatarDetailView
    {
        private readonly Action<InventoryAvatarDetailView, InventoryAvatarDetailField> _onChanged;
        private byte[] _avatarSocketData;
        private byte[] _colorData;
        private int _colorDataLen;

        internal InventoryAvatarDetailView(
            byte[] avatarSocketData,
            int colorDataLen,
            byte[] colorData,
            Action<InventoryAvatarDetailView, InventoryAvatarDetailField> onChanged = null)
        {
            _avatarSocketData = AvatarSocketDataCodec.Normalize(avatarSocketData);
            _colorDataLen = colorDataLen <= 0 ? 4 : colorDataLen;
            _colorData = InventoryItemViewBytes.CopyFixed(colorData, 7);
            _onChanged = onChanged;
            Socket0 = new InventoryAvatarSocketSlotView(this, 0);
            Socket1 = new InventoryAvatarSocketSlotView(this, 1);
            Socket2 = new InventoryAvatarSocketSlotView(this, 2);
            Socket3 = new InventoryAvatarSocketSlotView(this, 3);
            Socket4 = new InventoryAvatarSocketSlotView(this, 4);
            Sockets = new[]
            {
                Socket0,
                Socket1,
                Socket2,
                Socket3,
                Socket4,
            };
        }

        public static InventoryAvatarDetailView Empty { get; } = new InventoryAvatarDetailView(Array.Empty<byte>(), 4, Array.Empty<byte>());

        public int AvatarSocketLen => _avatarSocketData.Length;

        public int SocketLength => AvatarSocketLen;

        public byte[] AvatarSocketData
        {
            get => InventoryItemViewBytes.Copy(_avatarSocketData);
            set
            {
                _avatarSocketData = AvatarSocketDataCodec.Normalize(value);
                Notify(InventoryAvatarDetailField.SocketData);
            }
        }

        public byte[] SocketData
        {
            get => AvatarSocketData;
            set => AvatarSocketData = value;
        }

        public IReadOnlyList<InventoryAvatarSocketSlotView> Sockets { get; }

        public InventoryAvatarSocketSlotView Socket0 { get; }

        public InventoryAvatarSocketSlotView Socket1 { get; }

        public InventoryAvatarSocketSlotView Socket2 { get; }

        public InventoryAvatarSocketSlotView Socket3 { get; }

        public InventoryAvatarSocketSlotView Socket4 { get; }

        public int ColorDataLen
        {
            get => _colorDataLen;
            set
            {
                _colorDataLen = value <= 0 ? 4 : value;
                Notify(InventoryAvatarDetailField.ColorDataLength);
            }
        }

        public int ColorLength => ColorDataLen;

        public byte[] ColorData => InventoryItemViewBytes.Copy(_colorData);

        public ushort Color1
        {
            get => InventoryItemViewBytes.ReadUInt16(_colorData, 0);
            set
            {
                InventoryItemViewBytes.WriteUInt16(_colorData, 0, value);
                Notify(InventoryAvatarDetailField.ColorData);
            }
        }

        public ushort Color2
        {
            get => InventoryItemViewBytes.ReadUInt16(_colorData, 2);
            set
            {
                InventoryItemViewBytes.WriteUInt16(_colorData, 2, value);
                Notify(InventoryAvatarDetailField.ColorData);
            }
        }

        public InventoryAvatarSocketEntry GetSocket(int index)
        {
            EnsureSocketIndex(index);
            return new InventoryAvatarSocketEntry(
                GetSocketType(index),
                GetSocketEmblemItemId(index));
        }

        public void SetSocket(int index, ushort socketType, int emblemItemId)
        {
            EnsureSocketIndex(index);
            var offset = index * 6;
            InventoryItemViewBytes.WriteUInt16(_avatarSocketData, offset, socketType);
            InventoryItemViewBytes.WriteInt32(_avatarSocketData, offset + 2, emblemItemId);
            Notify(InventoryAvatarDetailField.SocketData);
        }

        internal ushort GetSocketType(int index)
        {
            EnsureSocketIndex(index);
            return InventoryItemViewBytes.ReadUInt16(_avatarSocketData, index * 6);
        }

        internal void SetSocketType(int index, ushort value)
        {
            EnsureSocketIndex(index);
            InventoryItemViewBytes.WriteUInt16(_avatarSocketData, index * 6, value);
            Notify(InventoryAvatarDetailField.SocketData);
        }

        internal int GetSocketEmblemItemId(int index)
        {
            EnsureSocketIndex(index);
            return InventoryItemViewBytes.ReadInt32(_avatarSocketData, index * 6 + 2);
        }

        internal void SetSocketEmblemItemId(int index, int value)
        {
            EnsureSocketIndex(index);
            InventoryItemViewBytes.WriteInt32(_avatarSocketData, index * 6 + 2, value);
            Notify(InventoryAvatarDetailField.SocketData);
        }

        private static void EnsureSocketIndex(int index)
        {
            if (index < 0 || index >= 5)
                throw new ArgumentOutOfRangeException(nameof(index));
        }

        private void Notify(InventoryAvatarDetailField field)
        {
            _onChanged?.Invoke(this, field);
        }
    }

    internal enum InventoryAvatarDetailField
    {
        SocketData,
        ColorDataLength,
        ColorData,
    }

    internal sealed class InventoryAvatarSocketSlotView
    {
        private readonly InventoryAvatarDetailView _owner;
        private readonly int _index;

        internal InventoryAvatarSocketSlotView(InventoryAvatarDetailView owner, int index)
        {
            _owner = owner;
            _index = index;
        }

        public ushort SocketType
        {
            get => _owner.GetSocketType(_index);
            set => _owner.SetSocketType(_index, value);
        }

        public ushort Type
        {
            get => SocketType;
            set => SocketType = value;
        }

        public int EmblemItemId
        {
            get => _owner.GetSocketEmblemItemId(_index);
            set => _owner.SetSocketEmblemItemId(_index, value);
        }

        public void Set(ushort socketType, int emblemItemId)
        {
            _owner.SetSocket(_index, socketType, emblemItemId);
        }
    }

    internal readonly struct InventoryAvatarSocketEntry
    {
        public InventoryAvatarSocketEntry(ushort socketType, int emblemItemId)
        {
            SocketType = socketType;
            EmblemItemId = emblemItemId;
        }

        public ushort SocketType { get; }

        public ushort Type => SocketType;

        public int EmblemItemId { get; }
    }

    internal readonly struct InventoryChronicleOptionEntry
    {
        public InventoryChronicleOptionEntry(int optionId, byte characJob, byte firstGrowType, byte equipmentType, byte optionNo)
        {
            OptionId = optionId;
            CharacJob = characJob;
            FirstGrowType = firstGrowType;
            EquipmentType = equipmentType;
            OptionNo = optionNo;
        }

        public int OptionId { get; }

        public byte CharacJob { get; }

        public byte FirstGrowType { get; }

        public byte EquipmentType { get; }

        public byte OptionNo { get; }
    }

    internal readonly struct InventoryJewelSocketEntry
    {
        public InventoryJewelSocketEntry(ushort socketType, uint emblemItemId)
        {
            SocketType = socketType;
            EmblemItemId = emblemItemId;
        }

        public ushort SocketType { get; }

        public uint EmblemItemId { get; }
    }

    internal sealed class InventoryItemExtraPayload
    {
        private readonly JsonObject _json;

        private InventoryItemExtraPayload(JsonObject json)
        {
            _json = json;
            ExtData0 = ReadByte(json, "extData0");
            PrefixData0E = ReadHexFixed(json, "prefixData0E", 8);
            MiddleData1A = ReadHexFixed(json, "middleData1A", 17);
            TailData2F = ReadHexFixed(json, "tailData2F", 37);
            JewelSocket = ReadHexFixed(json, "jewelSocket", 30);
            AvatarReserved0 = ReadHexFixed(json, "reserved0", 5);
            AvatarReserved1 = ReadHexFixed(json, "reserved1", 71);
            AvatarSocketData = AvatarSocketDataCodec.Normalize(ReadHexFixed(json, "reserved2", 30));
            AvatarColorDataLength = Convert.ToUInt16(ReadInt(json, "unknownFixed4"), CultureInfo.InvariantCulture);
            AvatarTailData = ReadHexFixed(json, "tailData", 7);
            PetTailData0A = ReadHexFixed(json, "tailData0A", 74);
        }

        public byte ExtData0 { get; private set; }

        public byte[] PrefixData0E { get; private set; }

        public byte[] MiddleData1A { get; private set; }

        public byte[] TailData2F { get; private set; }

        public byte[] JewelSocket { get; private set; }

        public byte[] AvatarReserved0 { get; private set; }

        public byte[] AvatarReserved1 { get; private set; }

        public byte[] AvatarSocketData { get; private set; }

        public ushort AvatarColorDataLength { get; private set; }

        public byte[] AvatarTailData { get; private set; }

        public byte[] PetTailData0A { get; private set; }

        public string Serialize()
        {
            return _json.ToJsonString();
        }

        public void SetExtData0(byte value)
        {
            ExtData0 = value;
            _json["extData0"] = value;
        }

        public void SetPrefixData0E(byte[] value)
        {
            PrefixData0E = SetHex("prefixData0E", value, 8);
        }

        public void SetMiddleData1A(byte[] value)
        {
            MiddleData1A = SetHex("middleData1A", value, 17);
        }

        public void SetTailData2F(byte[] value)
        {
            TailData2F = SetHex("tailData2F", value, 37);
        }

        public void SetJewelSocket(byte[] value)
        {
            JewelSocket = SetHex("jewelSocket", value, 30);
        }

        public void SetAvatarReserved0(byte[] value)
        {
            AvatarReserved0 = SetHex("reserved0", value, 5);
        }

        public void SetAvatarReserved1(byte[] value)
        {
            AvatarReserved1 = SetHex("reserved1", value, 71);
        }

        public void SetAvatarSocketData(byte[] value)
        {
            AvatarSocketData = SetHex("reserved2", AvatarSocketDataCodec.Normalize(value), 30);
        }

        public void SetAvatarColorDataLength(int value)
        {
            AvatarColorDataLength = Convert.ToUInt16(value <= 0 ? 4 : value, CultureInfo.InvariantCulture);
            _json["unknownFixed4"] = AvatarColorDataLength;
        }

        public void SetAvatarTailData(byte[] value)
        {
            AvatarTailData = SetHex("tailData", value, 7);
        }

        public void SetPetTailData0A(byte[] value)
        {
            PetTailData0A = SetHex("tailData0A", value, 74);
        }

        public static InventoryItemExtraPayload Parse(string extraJson)
        {
            JsonObject json = null;
            if (!string.IsNullOrWhiteSpace(extraJson))
            {
                try
                {
                    json = JsonNode.Parse(extraJson) as JsonObject;
                }
                catch
                {
                    json = null;
                }
            }

            return new InventoryItemExtraPayload(json ?? new JsonObject());
        }

        private byte[] SetHex(string propertyName, byte[] value, int expectedLength)
        {
            var data = InventoryItemViewBytes.CopyFixed(value, expectedLength);
            _json[propertyName] = InventoryItemViewBytes.ToHex(data);
            return data;
        }

        private static byte ReadByte(JsonObject json, string propertyName)
        {
            return Convert.ToByte(ReadInt(json, propertyName) & 0xFF, CultureInfo.InvariantCulture);
        }

        private static int ReadInt(JsonObject json, string propertyName)
        {
            if (!json.TryGetPropertyValue(propertyName, out var node) || node == null)
                return 0;

            return int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }

        private static byte[] ReadHexFixed(JsonObject json, string propertyName, int expectedLength)
        {
            return InventoryItemViewBytes.CopyFixed(ReadHexActual(json, propertyName), expectedLength);
        }

        private static byte[] ReadHexActual(JsonObject json, string propertyName)
        {
            if (!json.TryGetPropertyValue(propertyName, out var node) || node == null)
                return Array.Empty<byte>();

            var hex = node.ToString();
            if (string.IsNullOrWhiteSpace(hex))
                return Array.Empty<byte>();

            var data = new byte[hex.Length / 2];
            for (var index = 0; index < data.Length; index++)
            {
                if (!byte.TryParse(hex.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out data[index]))
                    return Array.Empty<byte>();
            }

            return data;
        }
    }

    internal static class InventoryItemViewBytes
    {
        public static byte[] Copy(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            return copy;
        }

        public static byte[] CopyFixed(byte[] source, int length)
        {
            var data = new byte[length];
            if (source != null && source.Length > 0)
                Buffer.BlockCopy(source, 0, data, 0, Math.Min(source.Length, length));
            return data;
        }

        public static void CopyInto(byte[] source, byte[] target, int targetOffset, int length)
        {
            if (target == null || targetOffset < 0 || targetOffset >= target.Length)
                return;

            Array.Clear(target, targetOffset, Math.Min(length, target.Length - targetOffset));
            if (source != null && source.Length > 0)
                Buffer.BlockCopy(source, 0, target, targetOffset, Math.Min(Math.Min(source.Length, length), target.Length - targetOffset));
        }

        public static byte[] CopyRange(byte[] source, int offset, int length)
        {
            var data = new byte[length];
            if (source != null && offset >= 0 && source.Length > offset)
                Buffer.BlockCopy(source, offset, data, 0, Math.Min(length, source.Length - offset));
            return data;
        }

        public static string ToHex(byte[] data)
        {
            return BitConverter.ToString(data ?? Array.Empty<byte>()).Replace("-", string.Empty);
        }

        public static byte ReadByte(byte[] data, int offset)
        {
            return data != null && offset >= 0 && offset < data.Length ? data[offset] : (byte)0;
        }

        public static ushort ReadUInt16(byte[] data, int offset)
        {
            return data != null && offset >= 0 && offset + 2 <= data.Length
                ? BitConverter.ToUInt16(data, offset)
                : (ushort)0;
        }

        public static int ReadInt32(byte[] data, int offset)
        {
            return data != null && offset >= 0 && offset + 4 <= data.Length
                ? BitConverter.ToInt32(data, offset)
                : 0;
        }

        public static void WriteUInt16(byte[] data, int offset, ushort value)
        {
            if (data != null && offset >= 0 && offset + 2 <= data.Length)
                BitConverter.GetBytes(value).CopyTo(data, offset);
        }

        public static void WriteInt32(byte[] data, int offset, int value)
        {
            if (data != null && offset >= 0 && offset + 4 <= data.Length)
                BitConverter.GetBytes(value).CopyTo(data, offset);
        }

        public static IReadOnlyList<InventoryChronicleOptionEntry> ParseChronicleOptions(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<InventoryChronicleOptionEntry>();

            var count = Math.Min(data[0], (byte)2);
            var options = new List<InventoryChronicleOptionEntry>(count);
            for (var index = 0; index < count; index++)
            {
                var optionIdOffset = 1 + index * 4;
                if (optionIdOffset + 4 > data.Length
                    || 9 + index >= data.Length
                    || 11 + index >= data.Length
                    || 13 + index >= data.Length
                    || 15 + index >= data.Length)
                    break;

                options.Add(new InventoryChronicleOptionEntry(
                    BitConverter.ToInt32(data, optionIdOffset),
                    data[9 + index],
                    data[11 + index],
                    data[13 + index],
                    data[15 + index]));
            }

            return options;
        }

        public static IReadOnlyList<InventoryJewelSocketEntry> ParseJewelSockets(byte[] data)
        {
            var sockets = new List<InventoryJewelSocketEntry>(5);
            for (var index = 0; index < 5; index++)
            {
                var offset = index * 6;
                if (data == null || offset + 6 > data.Length)
                {
                    sockets.Add(default);
                    continue;
                }

                sockets.Add(new InventoryJewelSocketEntry(
                    BitConverter.ToUInt16(data, offset),
                    BitConverter.ToUInt32(data, offset + 2)));
            }

            return sockets;
        }

        public static IReadOnlyList<RandomOptionEntry> ParseRandomOptions(byte[] tailData2F)
        {
            var result = new List<RandomOptionEntry>();
            if (tailData2F == null || tailData2F.Length < 21)
                return result;

            var count = Math.Max(0, Math.Min(3, (int)tailData2F[11]));
            for (var index = 0; index < count; index++)
            {
                result.Add(new RandomOptionEntry
                {
                    Type = tailData2F[12 + index],
                    Value1 = tailData2F[15 + index],
                    Value2 = tailData2F[18 + index],
                });
            }

            return result;
        }

        public static void WriteRandomOptions(byte[] tailData2F, IReadOnlyList<RandomOptionEntry> entries)
        {
            if (tailData2F == null || tailData2F.Length < 37 || entries == null || entries.Count == 0)
                return;

            var count = Math.Min(3, entries.Count);
            tailData2F[11] = (byte)count;
            for (var index = 0; index < 3; index++)
            {
                tailData2F[12 + index] = 0;
                tailData2F[15 + index] = 0;
                tailData2F[18 + index] = 0;
            }

            for (var index = 0; index < count; index++)
            {
                tailData2F[12 + index] = entries[index].Type;
                tailData2F[15 + index] = entries[index].Value1;
                tailData2F[18 + index] = entries[index].Value2;
            }

            tailData2F[21] = 0x00;
            tailData2F[22] = 0xFF;
            for (var index = 23; index <= 26 && index < tailData2F.Length; index++)
                tailData2F[index] = 0;
        }
    }
}
