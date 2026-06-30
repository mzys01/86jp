using DfoServer.Game.ItemUpgrade;
using System;
using System.Text;

namespace DfoServer.Network.Parsers.Inventory
{
    public sealed class ItemUpgradeRequest
    {
        public ItemUpgradeMode Mode { get; set; }
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public short MaterialSlotIndex { get; set; }
        public short OptionalTicketSlotIndex { get; set; } = -1;
        public string TargetItemName { get; set; }

        public static bool TryParse(byte[] body, out ItemUpgradeRequest request)
        {
            request = null;
            if (body == null || body.Length < 16)
                return false;

            var rawMode = BitConverter.ToUInt16(body, 0);
            if (rawMode > 1)
                return false;

            var nameLength = BitConverter.ToInt32(body, 12);
            if (nameLength < 0 || 16 + nameLength > body.Length)
                return false;

            request = new ItemUpgradeRequest
            {
                Mode = rawMode == 1 ? ItemUpgradeMode.Amplify : ItemUpgradeMode.Reinforce,
                TargetSlotIndex = BitConverter.ToInt16(body, 2),
                TargetItemTemplateId = BitConverter.ToInt32(body, 4),
                MaterialSlotIndex = BitConverter.ToInt16(body, 8),
                OptionalTicketSlotIndex = BitConverter.ToInt16(body, 10),
                TargetItemName = nameLength > 0 ? Encoding.UTF8.GetString(body, 16, nameLength) : string.Empty,
            };
            return true;
        }

        public ItemUpgradeCommand ToCommand()
        {
            return new ItemUpgradeCommand
            {
                Mode = Mode,
                TargetSlotIndex = TargetSlotIndex,
                TargetItemTemplateId = TargetItemTemplateId,
                MaterialSlotIndex = MaterialSlotIndex,
                OptionalTicketSlotIndex = OptionalTicketSlotIndex,
                TargetItemName = TargetItemName,
            };
        }
    }
}
