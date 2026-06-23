using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    
    
    public sealed class GageInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x019D;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var info = snapshot.InitializationSnapshot;
            body = new byte[] { info.GageType, info.GageValue };
            return true;
        }
    }
}
