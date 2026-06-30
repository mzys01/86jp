using DfoServer.Game.ItemUpgrade;

namespace DfoServer.Network.Builders
{
    public static class ItemUpgradeNoticeBuilder
    {
        public static byte[] Build(ItemUpgradeResult result, ushort userUniqueId)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(0x01);
            writer.WriteByte(result.UpgradeSucceeded ? (byte)1 : (byte)0);
            writer.WriteUInt16(userUniqueId);
            writer.WriteInt32(result.TargetItemTemplateId);
            writer.WriteByte(result.UpgradeSucceeded ? result.NewLevel : result.OldLevel);
            // 86 客户端这里调用 RandomOption::put_packet_random_option，当前模拟器暂未持久化魔法封印明细，先发送空列表。
            writer.WriteByte(0x00);

            return writer.ToArray();
        }
    }
}
