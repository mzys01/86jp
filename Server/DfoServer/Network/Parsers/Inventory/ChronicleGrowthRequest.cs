using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public static class ChronicleGrowthRequest
    {
        private const int FixedLength = 13;
        private const int MaterialLength = 6;

        public static bool TryParse(byte[] body, out ChronicleGrowthCommand command)
        {
            command = null;
            if (body == null || body.Length < FixedLength)
                return false;

            var materialCount = body[12];
            if (materialCount == 0 || materialCount > 16 || body.Length != FixedLength + materialCount * MaterialLength)
                return false;

            var parsed = new ChronicleGrowthCommand
            {
                TicketSlotIndex = BitConverter.ToInt16(body, 0),
                TicketItemTemplateId = BitConverter.ToInt32(body, 2),
                TargetSlotIndex = BitConverter.ToInt16(body, 6),
                TargetItemTemplateId = BitConverter.ToInt32(body, 8),
            };

            var offset = FixedLength;
            for (var i = 0; i < materialCount; i++, offset += MaterialLength)
            {
                parsed.Materials.Add(new ChronicleGrowthMaterialRequest
                {
                    SlotIndex = BitConverter.ToInt16(body, offset),
                    ItemTemplateId = BitConverter.ToInt32(body, offset + 2),
                });
            }

            command = parsed;
            return true;
        }
    }
}
