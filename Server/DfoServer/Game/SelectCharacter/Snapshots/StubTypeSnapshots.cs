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
    }
}
