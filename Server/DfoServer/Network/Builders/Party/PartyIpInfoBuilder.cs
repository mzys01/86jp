using DfoServer.Network;

namespace DfoServer.Network.Builders.Party
{
    // PARTY IP INFO (Noti SC cmd 0x000B) — P2P 端点交换, 让队友互相 UDP 直连(清"连接中")。
    // ⚠️ 逐字节照【86jp 客户端 sub_D31160 读序】(= df CParty::send_party_ipinfo @0x0859CEA2 镜像;
    //    工作流 IDA 实证: 每成员 22 字节, 尾部只有 1 个 char_attr, 不是 df 老版 23 字节)。
    // body = [u8 memberCount] + 每成员 22B:
    //   u16 uid(LE) + u32 inner_ip(4B octets a.b.c.d) + u32 outer_ip(4B octets)
    //   + u16 port(大端/网络序, 客户端过 htons 转换器) + u32 acc_id(LE) + u8 nat_type + u32 mtu(LE) + u8 char_attr
    // 存进客户端队伍成员对象 m+0x3F0(inner)/+0x3F4(outer)/+0x3F8(port)/+0x3FA(nat)。
    // df 次序: RES_PEER 接受成功 → 0x99 → 0x08(给A) → 【0x0B】 → 0x09。一个包广播给全队。
    public static class PartyIpInfoBuilder
    {
        public static byte[] Build(Game.Party.Party party)
        {
            var members = party.MembersBySlot();
            var w = new GamePacketWriter();
            w.WriteByte((byte)members.Count);                 // memberCount (u8)
            foreach (var m in members)
            {
                w.WriteUInt16(m.UserId);                      // uid (LE)
                var ip = (m.IpBytes != null && m.IpBytes.Length == 4) ? m.IpBytes : new byte[] { 127, 0, 0, 1 };
                w.WriteBytes(ip);                             // inner_ip 4B(octets)
                w.WriteBytes(ip);                             // outer_ip 4B(LAN=同 inner)
                w.WriteByte((byte)(m.P2pPort >> 8));          // port 高字节(大端/网络序)
                w.WriteByte((byte)(m.P2pPort & 0xFF));        // port 低字节
                w.WriteUInt32(m.AccId);                       // acc_id (LE)
                w.WriteByte(0);                               // nat_type = 0(open/LAN)
                w.WriteUInt32(1500);                          // mtu
                w.WriteByte(0);                               // char_attr
            }
            return w.ToArray();
        }
    }
}
