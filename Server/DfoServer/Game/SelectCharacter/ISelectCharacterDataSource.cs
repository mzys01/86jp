using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using System.Collections.Generic;

namespace DfoServer.Game.SelectCharacter
{
    public interface ISelectCharacterDataSource
    {
        
        
        
        
        
        SelectCharacterDataSnapshot Load(int characterId, int accountId);

        int GetSeedCharacterId();

        void InitializeNewCharacter(int characterId, int accountId, byte job);
    }

    public sealed class SelectCharacterDataSnapshot
    {
        public List<SelectCharacterPacketTemplate> PacketTemplates { get; set; } = new List<SelectCharacterPacketTemplate>();

        public List<byte[]> FullPacketStream { get; set; } = new List<byte[]>();

        public byte[] SelectionAckBody { get; set; } = new byte[0];

        public CharacterItemListSnapshot ItemListSnapshot { get; set; } = new CharacterItemListSnapshot();

        public SelectCharacterInitializationSnapshot InitializationSnapshot { get; set; } = new SelectCharacterInitializationSnapshot();

        public List<byte[]> ExactPacketStream { get; set; } = new List<byte[]>();

        
        
        
        public CharacterRecord CharacterRecord { get; set; }
    }
}
