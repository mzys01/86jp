namespace DfoServer.Game.Mercenary
{
    // 单个角色的支援兵槽位选择状态。
    public sealed class MercenarySupportState
    {
        public int OwnerCharacterId { get; set; }
        public byte Slot { get; set; }
        public int SupportCharacterId { get; set; }
        public ushort SkillId { get; set; }
        // 所选技能对应的支援兵连招编号。
        public ushort StrikerSkillId { get; set; }
    }
}
