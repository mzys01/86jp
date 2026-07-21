using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class StrikerSupportTagCharacterBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x019F;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            if (occurrenceIndex == 0)
            {
                var characterId = snapshot.CharacterRecord?.CharacterId ?? 0;
                if (StrikerSupportTagCharacterPacketBuilder.TryBuildOwnerSupportBody(characterId, out body))
                    return true;

                body = BuildEmptyBody();
                return true;
            }

            body = null;
            return false;
        }

        internal static byte[] BuildEmptyBody()
        {
            return new byte[] { 0x00, 0x00 };
        }
    }
}
