using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    
    
    public sealed class CollectionBoxBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0381;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var box = snapshot.InitializationSnapshot.CollectionBox;
            var itemCount = box.Items.Count;
            body = new byte[8 + itemCount * 8];
            body[0] = box.BoxType;
            body[1] = box.DisplayMode;
            Buffer.BlockCopy(BitConverter.GetBytes(box.CollectionId), 0, body, 2, 4);
            body[6] = box.StatusFlags;
            body[7] = (byte)itemCount;
            for (var i = 0; i < itemCount; i++)
            {
                var off = 8 + i * 8;
                Buffer.BlockCopy(BitConverter.GetBytes(box.Items[i].ItemId), 0, body, off, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(box.Items[i].Count), 0, body, off + 4, 4);
            }
            return true;
        }
    }
}
