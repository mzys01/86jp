using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    // 穿戴栏物品语义视图：持有 equipped record/raw，并投影为统一 84B entry 语义。
    internal sealed class EquippedItemView
    {
        private MakeEquipListCodec.DisplayFields _fields;

        private EquippedItemView(MakeEquipListCodec.Entry record)
        {
            Record = record ?? throw new ArgumentNullException(nameof(record));
            if (Record.Raw == null || Record.Raw.Length == 0)
                Record.Raw = MakeEquipListCodec.BuildEntryFromDisplayFields(Record.Slot, Record.ItemId, new MakeEquipListCodec.DisplayFields());

            _fields = Record.Raw.Length >= 24
                ? MakeEquipListCodec.ParseDisplayFields(Record.Raw)
                : new MakeEquipListCodec.DisplayFields();

            Entry84 = BuildEntry84(SyncEntry84Change);
        }

        public MakeEquipListCodec.Entry Record { get; }

        public InventoryItemEntry84View Entry84 { get; private set; }

        public InventoryItemEntry84View Common84 => Entry84;

        public byte[] RawEntry
        {
            get
            {
                var raw = Record.Raw ?? Array.Empty<byte>();
                var copy = new byte[raw.Length];
                Buffer.BlockCopy(raw, 0, copy, 0, raw.Length);
                return copy;
            }
        }

        public short Slot
        {
            get => (short)Record.Slot;
            set
            {
                if (Record.Slot == value)
                    return;

                Record.Slot = value;
                RebuildRaw();
                RebuildEntry84();
            }
        }

        public short SlotIndex
        {
            get => Slot;
            set => Slot = value;
        }

        public int ItemId
        {
            get => Record.ItemId;
            set
            {
                if (Record.ItemId == value)
                    return;

                Record.ItemId = value;
                RebuildRaw();
                RebuildEntry84();
            }
        }

        public int ItemTemplateId
        {
            get => ItemId;
            set => ItemId = value;
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

        public int ExpireTime
        {
            get => Entry84.ExpireTime;
            set => Entry84.ExpireTime = value;
        }

        public byte GenuineUpgrade
        {
            get => Entry84.GenuineUpgrade;
            set => Entry84.GenuineUpgrade = value;
        }

        public byte Forging
        {
            get => GenuineUpgrade;
            set => GenuineUpgrade = value;
        }

        public byte EmancipateEquipmentLevel
        {
            get => Entry84.EmancipateEquipmentLevel;
            set => Entry84.EmancipateEquipmentLevel = value;
        }

        public byte TradeRestriction
        {
            get => Entry84.TradeRestriction;
            set => Entry84.TradeRestriction = value;
        }

        public byte SortLockFlag
        {
            get => Entry84.SortLockFlag;
            set => Entry84.SortLockFlag = value == 1 ? (byte)1 : (byte)0;
        }

        public byte SortLock
        {
            get => SortLockFlag;
            set => SortLockFlag = value;
        }

        public byte EquipmentLockId
        {
            get => Record.EquipmentLockId;
            set => Record.EquipmentLockId = value;
        }

        public byte EquipmentLock
        {
            get => EquipmentLockId;
            set => EquipmentLockId = value;
        }

        public byte[] AvatarSocketData
        {
            get => InventoryItemViewBytes.CopyFixed(_fields.JewelSocket, AvatarSocketDataCodec.Length);
            set
            {
                _fields.JewelSocket = InventoryItemViewBytes.CopyFixed(value, AvatarSocketDataCodec.Length);
                RebuildRaw();
                RebuildEntry84();
            }
        }

        public int AvatarSocketCount => CountOpenAvatarSockets();

        public int OpenAvatarSocketCount => AvatarSocketCount;

        public ushort GetAvatarSocketType(int index)
        {
            EnsureAvatarSocketIndex(index);
            return InventoryItemViewBytes.ReadUInt16(AvatarSocketData, index * 6);
        }

        public int GetAvatarSocketEmblemItemId(int index)
        {
            EnsureAvatarSocketIndex(index);
            return InventoryItemViewBytes.ReadInt32(AvatarSocketData, index * 6 + 2);
        }

        public void SetAvatarSocketEmblemItemId(int index, int value)
        {
            EnsureAvatarSocketIndex(index);
            var data = AvatarSocketData;
            InventoryItemViewBytes.WriteInt32(data, index * 6 + 2, value);
            AvatarSocketData = data;
        }

        public static EquippedItemView FromRecord(MakeEquipListCodec.Entry record)
        {
            return new EquippedItemView(record);
        }

        private InventoryItemEntry84View BuildEntry84(Action<InventoryItemEntry84View, InventoryItemEntry84Field> onChanged)
        {
            return new InventoryItemEntry84View(
                (short)Record.Slot,
                Record.ItemId,
                unchecked((int)_fields.InstanceValue),
                _fields.Reinforce,
                _fields.Durability,
                _fields.SealFlag,
                BuildPrefixData0E(_fields),
                -1,
                new byte[17],
                Record.ExpireTime,
                BuildTailData2F(_fields),
                _fields.JewelSocket,
                onChanged);
        }

        private void SyncEntry84Change(InventoryItemEntry84View entry, InventoryItemEntry84Field field)
        {
            switch (field)
            {
                case InventoryItemEntry84Field.Value:
                    _fields.InstanceValue = unchecked((uint)entry.Value);
                    break;
                case InventoryItemEntry84Field.Attr:
                    _fields.Reinforce = entry.Attr;
                    break;
                case InventoryItemEntry84Field.Durability:
                    _fields.Durability = entry.Durability;
                    break;
                case InventoryItemEntry84Field.SealFlag:
                    ApplySealFlag(entry.SealFlag);
                    break;
                case InventoryItemEntry84Field.PrefixData0E:
                    ApplyPrefixData0E(entry.PrefixData0E);
                    break;
                case InventoryItemEntry84Field.ExpireTime:
                    Record.ExpireTime = entry.ExpireTime;
                    break;
                case InventoryItemEntry84Field.TailData2F:
                    ApplyTailData2F(entry.TailData2F);
                    break;
                case InventoryItemEntry84Field.JewelSocket:
                    _fields.JewelSocket = entry.JewelSocket;
                    break;
            }

            RebuildRaw();
        }

        private void RebuildEntry84()
        {
            Entry84 = BuildEntry84(SyncEntry84Change);
        }

        private void RebuildRaw()
        {
            Record.Raw = MakeEquipListCodec.BuildEntryFromDisplayFields(Record.Slot, Record.ItemId, _fields);
        }

        private int CountOpenAvatarSockets()
        {
            var count = 0;
            var data = AvatarSocketData;
            for (var index = 0; index < 5; index++)
            {
                if (InventoryItemViewBytes.ReadUInt16(data, index * 6) != 0)
                    count++;
            }

            return count;
        }

        private static void EnsureAvatarSocketIndex(int index)
        {
            if (index < 0 || index >= 5)
                throw new ArgumentOutOfRangeException(nameof(index));
        }

        private void ApplySealFlag(byte sealFlag)
        {
            _fields.SealFlag = sealFlag;
            _fields.ClearAvatar = (_fields.ClearAvatar & 0xFFFFFF00u) | sealFlag;
        }

        private void ApplyPrefixData0E(byte[] prefixData0E)
        {
            var prefix = InventoryItemViewBytes.CopyFixed(prefixData0E, 8);
            _fields.Enchant = unchecked((uint)InventoryItemViewBytes.ReadInt32(prefix, 0));
            _fields.EnchantUpgradeCount = InventoryItemViewBytes.ReadByte(prefix, 4);
            _fields.AmplifyType = InventoryItemViewBytes.ReadByte(prefix, 5);
            _fields.AmplifyValue = InventoryItemViewBytes.ReadUInt16(prefix, 6);
        }

        private void ApplyTailData2F(byte[] tailData2F)
        {
            var tail = InventoryItemViewBytes.CopyFixed(tailData2F, 37);
            _fields.Emblem = InventoryItemViewBytes.CopyRange(tail, 0, 9);
            _fields.Rune = InventoryItemViewBytes.ReadUInt16(tail, 9);
            _fields.Forging = InventoryItemViewBytes.ReadByte(tail, 27);
            _fields.EmancipateEquipmentLevel = InventoryItemViewBytes.ReadByte(tail, 28);
            _fields.TradeRestriction = InventoryItemViewBytes.ReadByte(tail, 29);
            _fields.TailUnknown0 = InventoryItemViewBytes.ReadUInt16(tail, 30);
            _fields.TailUnknown1 = InventoryItemViewBytes.ReadByte(tail, 32);
            _fields.TailUnknown2 = InventoryItemViewBytes.ReadByte(tail, 33);
            _fields.TailUnknown3 = InventoryItemViewBytes.ReadByte(tail, 34);
            _fields.RemainUseCount = InventoryItemViewBytes.ReadByte(tail, 35);
            _fields.SortLockFlag = InventoryItemViewBytes.ReadByte(tail, 36) == 1 ? (byte)1 : (byte)0;

            var randomOptions = InventoryItemViewBytes.ParseRandomOptions(tail);
            _fields.MagicSealCount = (byte)Math.Min(3, randomOptions.Count);
            _fields.MagicSealTypes = BuildRandomOptionBytes(randomOptions, option => option.Type);
            _fields.MagicSealVal1s = BuildRandomOptionBytes(randomOptions, option => option.Value1);
            _fields.MagicSealVal2s = BuildRandomOptionBytes(randomOptions, option => option.Value2);
            _fields.RandomOptionState = InventoryItemViewBytes.ReadByte(tail, 21);
            _fields.RandomOptionChangedIndex = InventoryItemViewBytes.ReadByte(tail, 22);
            _fields.RandomOptionChangeState = InventoryItemViewBytes.ReadByte(tail, 23);
            _fields.RandomOptionChangeType = InventoryItemViewBytes.ReadByte(tail, 24);
            _fields.RandomOptionChangeValue1 = InventoryItemViewBytes.ReadByte(tail, 25);
            _fields.RandomOptionChangeValue2 = InventoryItemViewBytes.ReadByte(tail, 26);
            _fields.MagicSealTail = ExtractMagicSealTail(tail);
        }

        internal static byte[] BuildPrefixData0E(MakeEquipListCodec.DisplayFields fields)
        {
            var prefix = new byte[8];
            BitConverter.GetBytes(fields.Enchant).CopyTo(prefix, 0);
            prefix[4] = fields.EnchantUpgradeCount;
            prefix[5] = fields.AmplifyType;
            BitConverter.GetBytes(fields.AmplifyValue).CopyTo(prefix, 6);
            return prefix;
        }

        internal static byte[] BuildTailData2F(MakeEquipListCodec.DisplayFields fields)
        {
            var tail = new byte[37];
            if (fields.Emblem != null && fields.Emblem.Length > 0)
                Buffer.BlockCopy(fields.Emblem, 0, tail, 0, Math.Min(fields.Emblem.Length, 9));
            BitConverter.GetBytes(fields.Rune).CopyTo(tail, 9);
            if (fields.MagicSealCount > 0 && fields.MagicSealTypes != null)
            {
                tail[11] = fields.MagicSealCount;
                for (var index = 0; index < fields.MagicSealCount && index < 3; index++)
                {
                    tail[12 + index] = SafeRead(fields.MagicSealTypes, index);
                    tail[15 + index] = SafeRead(fields.MagicSealVal1s, index);
                    tail[18 + index] = SafeRead(fields.MagicSealVal2s, index);
                }

                if (fields.MagicSealTail != null)
                {
                    for (var index = 0; index < fields.MagicSealTail.Length && 21 + index < tail.Length; index++)
                        tail[21 + index] = fields.MagicSealTail[index];
                }
            }
            tail[27] = fields.Forging;
            tail[28] = fields.EmancipateEquipmentLevel;
            tail[29] = fields.TradeRestriction;
            BitConverter.GetBytes(fields.TailUnknown0).CopyTo(tail, 30);
            tail[32] = fields.TailUnknown1;
            tail[33] = fields.TailUnknown2;
            tail[34] = fields.TailUnknown3;
            tail[35] = fields.RemainUseCount;
            tail[36] = fields.SortLockFlag == 1 ? (byte)1 : (byte)0;
            return tail;
        }

        private static byte SafeRead(byte[] source, int index)
        {
            return source != null && index >= 0 && index < source.Length ? source[index] : (byte)0;
        }

        private static byte[] BuildRandomOptionBytes(IReadOnlyList<RandomOptionEntry> entries, Func<RandomOptionEntry, byte> selector)
        {
            var data = new byte[3];
            if (entries == null || selector == null)
                return data;

            for (var index = 0; index < entries.Count && index < data.Length; index++)
                data[index] = selector(entries[index]);
            return data;
        }

        private static byte[] ExtractMagicSealTail(byte[] tail)
        {
            var data = new byte[16];
            if (tail != null && tail.Length > 21)
                Buffer.BlockCopy(tail, 21, data, 0, Math.Min(data.Length, tail.Length - 21));
            return data;
        }
    }
}
