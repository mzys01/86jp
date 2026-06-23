using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class HotkeyConfigBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x01C7;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var slots = snapshot.InitializationSnapshot.HotkeyConfigSlots;
            var slotCount = slots?.Count ?? 0;
            var blobLen = slotCount * 2;
            body = new byte[1 + 4 + blobLen];
            body[0] = snapshot.InitializationSnapshot.HotkeyKeyType;
            Buffer.BlockCopy(BitConverter.GetBytes(blobLen), 0, body, 1, 4);
            for (var i = 0; i < slotCount; i++)
                Buffer.BlockCopy(BitConverter.GetBytes(slots[i]), 0, body, 5 + i * 2, 2);
            return true;
        }
    }
}
