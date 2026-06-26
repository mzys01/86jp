using System.Collections.Generic;

namespace DfoServer.Game.Session
{
    public partial class PlayerContext
    {
        public List<Dungeon.ClearRewardGenerator.CardReward> CurCardRewards { get; set; }
        public int CurCardFlipCount { get; set; }
        public int CurDungeonClearState { get; set; }
        public byte[] CurFreeCardSlots { get; set; } = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        public byte[] CurPaidCardSlots { get; set; } = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
    }
}
