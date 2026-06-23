using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    
    
    public sealed class RentalInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0357;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var info = snapshot.InitializationSnapshot.RentalInfo;
            var itemCount = info.Items.Count;
            body = new byte[8 + itemCount * 8];
            Buffer.BlockCopy(BitConverter.GetBytes(info.RentalId), 0, body, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)itemCount), 0, body, 4, 4);
            for (var i = 0; i < itemCount; i++)
            {
                var off = 8 + i * 8;
                Buffer.BlockCopy(BitConverter.GetBytes(info.Items[i].ItemId), 0, body, off, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(info.Items[i].ExpireTime), 0, body, off + 4, 4);
            }
            return true;
        }
    }
}
