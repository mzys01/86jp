using System;
using System.Collections.Generic;

namespace DfoServer.Game.SelectCharacter
{
    public sealed class SkillPointSlotEntrySnapshot
    {
        public byte SkillType { get; set; }
        public ushort Points { get; set; }
    }

    public sealed class CollectionBoxItemSnapshot
    {
        public uint ItemId { get; set; }
        public uint Count { get; set; }
    }

    public sealed class CollectionBoxSnapshot
    {
        public byte BoxType { get; set; }
        public byte DisplayMode { get; set; }
        public uint CollectionId { get; set; }
        public byte StatusFlags { get; set; }
        public List<CollectionBoxItemSnapshot> Items { get; } = new List<CollectionBoxItemSnapshot>();
    }

    public sealed class RentalItemSnapshot
    {
        public uint ItemId { get; set; }
        public uint ExpireTime { get; set; }
    }

    public sealed class RentalInfoSnapshot
    {
        public const uint DefaultRentalId = 891;

        public uint RentalId { get; set; } = DefaultRentalId;
        public List<RentalItemSnapshot> Items { get; } = new List<RentalItemSnapshot>();

        public static void ParseStorageBody(byte[] body, RentalInfoSnapshot rental)
        {
            if (rental == null)
                return;

            rental.Items.Clear();
            if (body == null || body.Length < 8)
                return;

            uint count;
            int off;
            if (body.Length >= 12 && BitConverter.ToUInt32(body, 4) == DefaultRentalId)
            {
                rental.RentalId = DefaultRentalId;
                count = BitConverter.ToUInt32(body, 8);
                off = 12;
            }
            else
            {
                rental.RentalId = BitConverter.ToUInt32(body, 0);
                count = BitConverter.ToUInt32(body, 4);
                off = 8;
            }

            for (uint i = 0; i < count && off + 8 <= body.Length; i++)
            {
                rental.Items.Add(new RentalItemSnapshot
                {
                    ItemId = BitConverter.ToUInt32(body, off),
                    ExpireTime = BitConverter.ToUInt32(body, off + 4),
                });
                off += 8;
            }
        }

        public static byte[] BuildStorageBody(RentalInfoSnapshot rental)
        {
            var info = rental ?? new RentalInfoSnapshot();
            var itemCount = info.Items.Count;
            var storage = new byte[8 + itemCount * 8];
            Buffer.BlockCopy(BitConverter.GetBytes(info.RentalId), 0, storage, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)itemCount), 0, storage, 4, 4);
            for (var i = 0; i < itemCount; i++)
            {
                var off = 8 + i * 8;
                Buffer.BlockCopy(BitConverter.GetBytes(info.Items[i].ItemId), 0, storage, off, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(info.Items[i].ExpireTime), 0, storage, off + 4, 4);
            }

            return storage;
        }
    }
}
