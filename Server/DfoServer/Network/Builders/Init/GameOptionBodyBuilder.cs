using DfoServer.Game.SelectCharacter;
using System;

namespace DfoServer.Network.Builders
{
    
    
    public sealed class GameOptionBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x00AD;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            var main = init.MainGameOptionBlob ?? Array.Empty<byte>();
            var bank0 = init.QuickchatBank0 ?? Array.Empty<byte>();
            var bank1 = init.QuickchatBank1 ?? Array.Empty<byte>();

            var writer = new Network.GamePacketWriter();

            writer.WriteInt32(main.Length);
            for (int i = 0; i < main.Length; i++)
                writer.WriteByte(main[i]);

            writer.WriteInt32(bank0.Length);
            for (int i = 0; i < bank0.Length; i++)
                writer.WriteByte(bank0[i]);

            writer.WriteInt32(bank1.Length);
            for (int i = 0; i < bank1.Length; i++)
                writer.WriteByte(bank1[i]);

            body = writer.ToArray();
            return true;
        }
    }
}
