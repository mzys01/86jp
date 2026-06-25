using DfoServer.Game.Inventory;

namespace DfoServer.Game.ExpertJob
{
    public sealed class EnchantByBeadResult
    {
        public const byte ErrorInvalidBead = 0x11;
        public const byte ErrorInvalidTarget = 0x13;
        public const byte ErrorUnsupported = 0x17;

        public bool Success { get; set; }

        public byte ErrorCode { get; set; }

        public EnchantByBeadCommand Command { get; set; }

        public CommonInventoryItem TargetItem { get; set; }

        public CommonInventoryItem BeadItem { get; set; }

        public int EnchantCardItemId { get; set; }

        public static EnchantByBeadResult Error(EnchantByBeadCommand command, byte errorCode)
        {
            return new EnchantByBeadResult
            {
                Command = command,
                ErrorCode = errorCode,
            };
        }

        public static EnchantByBeadResult Ok(EnchantByBeadCommand command, CommonInventoryItem targetItem, CommonInventoryItem beadItem, int enchantCardItemId)
        {
            return new EnchantByBeadResult
            {
                Success = true,
                Command = command,
                TargetItem = targetItem,
                BeadItem = beadItem,
                EnchantCardItemId = enchantCardItemId,
            };
        }
    }
}
