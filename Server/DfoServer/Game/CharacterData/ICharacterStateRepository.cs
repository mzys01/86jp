using DfoServer.Game.SelectCharacter;
using System.Collections.Generic;

namespace DfoServer.Game.CharacterData
{
    public interface ICharacterStateRepository
    {
        void LoadFlags(int characterId, SelectCharacterInitializationSnapshot snapshot);

        void SaveFlags(int characterId, SelectCharacterInitializationSnapshot snapshot);

        void SaveCharacterOption(int characterId, byte[] body);

        bool HasFlags(int characterId);

        void SeedFromSnapshot(int characterId, SelectCharacterInitializationSnapshot snapshot);

        void LoadAll(int characterId, SelectCharacterInitializationSnapshot snapshot);

        byte[] LoadServerEventPhaseBitmap();

        void SeedRawPacketsFromTemplates(int characterId, List<SelectCharacterPacketTemplate> templates);
    }
}
