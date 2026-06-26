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
        public uint RentalId { get; set; }
        public List<RentalItemSnapshot> Items { get; } = new List<RentalItemSnapshot>();

        public static void ParseStorageBody(byte[] body, RentalInfoSnapshot rental)
        {
            if (rental == null)
                return;

            rental.Items.Clear();
            if (body == null || body.Length < 8)
                return;

            rental.RentalId = BitConverter.ToUInt32(body, 0);
            var count = BitConverter.ToUInt32(body, 4);
            var off = 8;
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
    }
}
