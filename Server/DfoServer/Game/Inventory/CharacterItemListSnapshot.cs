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
    }

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
    }

    public sealed class AvatarInventoryItem
    {
        public short SlotIndex { get; set; }

        public int AvatarItemId { get; set; }

        public byte[] Reserved0 { get; set; } = new byte[5];

        public byte OptionValue { get; set; }

        public byte[] Reserved1 { get; set; } = new byte[71];

        public int UnknownFixed30 { get; set; }

        public byte[] Reserved2 { get; set; } = new byte[30];

        public ushort UnknownFixed4 { get; set; }

        public byte[] TailData { get; set; } = new byte[7];
    }

    public sealed class PetInventoryItem
    {
        public short SlotIndex { get; set; }

        public int CreatureItemId { get; set; }

        public int CreatureSerialOrHandle { get; set; }

        public byte[] TailData0A { get; set; } = new byte[74];
    }

    public sealed class AccountCargoStateSnapshot
    {
        public ushort SelectionKey { get; set; }

        public ushort ItemCount { get; set; }

        public int Value32 { get; set; }
    }

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
}