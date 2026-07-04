using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class BoosterGageBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x019D;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            // 86JP's 0x019D client reader is not the Seria-luck progress value.
            // Frida evidence shows a 5-byte active/time state, so skip this
            // packet until the real layout and trigger are proven.
            body = System.Array.Empty<byte>();
            return false;
        }
    }
}
