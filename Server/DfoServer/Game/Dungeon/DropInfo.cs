namespace DfoServer.Game.Dungeon
{
    public struct DropInfo
    {
        public ushort SceneSlot;
        public uint TemplateId;
        public uint StackCount;
        public ushort Endurance;
        public byte UpgradeLevel;

        public bool IsGold => TemplateId == 0;
    }
}
