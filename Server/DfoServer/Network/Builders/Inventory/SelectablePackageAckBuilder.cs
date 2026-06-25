using DfoServer.Game.Inventory;
using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class SelectablePackageAckBuilder
    {
        public static byte[] BuildSuccess(IReadOnlyList<PackageGrantedItem> grantedItems)
        {
            // A short "result + count" body is accepted as Ok, but older client traces
            // then read item ids from the wrong offsets and loop on createItem failures.
            // Keep the mall-style 24-byte header shape and append rewards at offset 24.
            // Live 0x00A0 traces still need a separate obtained-popup notification.
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);         // result flag
            writer.WriteByte(0x00);         // popup flag
            writer.WriteInt32(-1);          // category: search all / safe sentinel
            writer.WriteInt32(-1);          // no main commodity for package reward popup
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteInt32(0);           // mall-compatible field; commodity stays sentinel, rewards are appended

            var count = grantedItems != null ? grantedItems.Count : 0;
            if (count > ushort.MaxValue)
                count = ushort.MaxValue;
            writer.WriteUInt16((ushort)count);

            for (var i = 0; i < count; i++)
            {
                var item = grantedItems[i];
                writer.WriteInt32(item.ItemTemplateId);
                writer.WriteInt32(item.DisplayCount <= 0 ? 1 : item.DisplayCount);
            }

            return writer.ToArray();
        }

        public static byte[] BuildError()
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteInt32(-1);
            writer.WriteInt32(-1);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            return writer.ToArray();
        }
    }
}
