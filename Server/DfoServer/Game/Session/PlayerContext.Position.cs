using System;

namespace DfoServer.Game.Session
{
    public partial class PlayerContext
    {
        public byte CurTownId { get; set; }
        public byte CurAreaId { get; set; }
        public short CurPosX { get; set; }
        public short CurPosY { get; set; }
        public byte CurDirection { get; set; } = 0x05;
        public byte CurAreaState { get; set; } = 0x03;

        /// <summary>
        /// </summary>
        public DateTime LastPositionPersistAt { get; set; } = DateTime.MinValue;
    }
}
