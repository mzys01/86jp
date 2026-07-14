using DfoServer.Game.SelectCharacter;

namespace DfoServer.Game.CharacterData
{
    public interface ICharacterStateRepository
    {
        void LoadFlags(int characterId, SelectCharacterInitializationSnapshot snapshot);

        void SaveFlags(int characterId, SelectCharacterInitializationSnapshot snapshot);

        void SaveCharacterOption(int characterId, byte[] body);

        void SaveEmotionIndex(int characterId, ushort emotionIndex);

        void SaveHotkeyConfig(int characterId, byte[] hotkeys);

        bool HasFlags(int characterId);

        void SeedFromSnapshot(int characterId, SelectCharacterInitializationSnapshot snapshot);

        void LoadAll(int characterId, SelectCharacterInitializationSnapshot snapshot);
    }
}
