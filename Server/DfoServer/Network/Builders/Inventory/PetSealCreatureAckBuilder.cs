using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    public static class PetSealCreatureAckBuilder
    {
        public static byte[] BuildSuccess(PetCreatureSealResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);

            // 86 client 0x037F success path reads an 18-byte result block after the flag.
            writer.WriteInt32(0); // reserved
            writer.WriteInt32(0); // result code 0 = success
            writer.WriteUInt16(0); // reserved
            writer.WriteInt16(result != null ? result.CapsuleSlotIndex : (short)0); // block +0x0A
            writer.WriteInt16(result != null ? result.CreatureSlotIndex : (short)0); // block +0x0C
            writer.WriteInt32(0); // target creature slot is removed by seal

            return writer.ToArray();
        }
    }
}
