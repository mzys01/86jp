namespace DfoServer.Game.Dungeon
{
    public struct DropInfo
    {
        public ushort SceneSlot;
        public uint TemplateId;
        public uint StackCount;
        public ushort Endurance;
        public byte UpgradeLevel;
        public bool IsPlayerDropped;
        public Inventory.DungeonInventoryDropPayload InventoryPayload;

        public bool IsGold => TemplateId == 0;

        public uint PacketValue => InventoryPayload?.PacketItem != null
            ? unchecked((uint)InventoryPayload.PacketItem.CountOrInstanceValue)
            : StackCount;
    }
}
