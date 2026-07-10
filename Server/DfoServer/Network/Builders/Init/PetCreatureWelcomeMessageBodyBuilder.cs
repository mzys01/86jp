using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders
{
    public sealed class PetCreatureWelcomeMessageBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0077;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = null;
            if (occurrenceIndex != 0)
                return false;

            var character = snapshot?.CharacterRecord;
            var characterId = character?.CharacterId ?? 0;
            var itemTemplateId = ResolveEquippedCreatureItemId(snapshot);
            if (characterId <= 0 || itemTemplateId <= 0)
                return false;

            if (PetCreatureScript.TryLoadWelcomeCache(
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath,
                characterId,
                itemTemplateId,
                occurrenceIndex,
                out var cachedBody))
            {
                body = cachedBody;
                return true;
            }

            return false;
        }

        private static int ResolveEquippedCreatureItemId(SelectCharacterDataSnapshot snapshot)
        {
            var tailItemId = snapshot?.CharacterRecord?.Subtype0Tail?.EquippedCreatureItemId ?? 0;
            if (tailItemId > 0)
                return unchecked((int)tailItemId);

            var items = snapshot?.ItemListSnapshot?.PetItems;
            if (items == null)
                return 0;

            foreach (var item in items)
            {
                if (item != null && (item.SlotIndex == 24 || item.SlotIndex == 240))
                    return item.CreatureItemId;
            }

            return 0;
        }
    }
}
