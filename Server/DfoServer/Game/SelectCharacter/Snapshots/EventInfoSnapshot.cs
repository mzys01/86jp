namespace DfoServer.Game.SelectCharacter
{
    public sealed class EventInfoEntrySnapshot
    {
        public ushort RepeatEventIndex { get; set; }

        public byte[] EventData { get; set; } = new byte[12];
    }
}
