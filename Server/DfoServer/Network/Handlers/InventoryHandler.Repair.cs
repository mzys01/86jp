using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        // 请求 body(剥头后): [inven_type:1][slot:2 LE][repair_item_slot:2 LE][pad:2][quick:1]
        //   inven_type: 0=背包/快捷栏, 3=穿戴装备, 2=货柜
        //   slot: 0xFFFF(-1)=全部修理, 否则指定槽
        //   末字节(body[7]): 0=普通修理(商店价), 1=快速修理(侧边栏, 费用×[quick repair cost rate]=1.5)
        // 回包 body(9B): [01成功标志][剩余金币:4 LE][inven_type:1][slot:2 LE][00 00]
        public async Task Handle_ENUM_CMDPACKET_REPAIR_EQUIPMENT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 5)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0017, RepairEquipmentAckBuilder.BuildError(0x0A)));
                return;
            }

            var invenType = body[0];
            var slot = BitConverter.ToInt16(body, 1);
            var quickRepair = body.Length >= 8 && body[7] == 1;   // 侧边栏快速修理标志

            var listType = MapInvenTypeToListType(invenType);
            if (listType == null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0017, RepairEquipmentAckBuilder.BuildError(0x11)));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryRepairEquipment(cid, aid, listType.Value, slot, quickRepair, out var result))
            {
                FileLogger.Log($"[{ProtocolName}] REPAIR_EQUIPMENT: FAILED inven_type={invenType} slot={slot}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0017, RepairEquipmentAckBuilder.BuildError(0x0A)));
                return;
            }

            // 全部修理只回一个 slot=0xFFFF 的 ACK, 客户端据此自己把全身耐久本地拉满(客户端 handler sub_CD7C50 逻辑)。
            short ackSlot = (slot == -1) ? unchecked((short)0xFFFF) : result.SlotIndex;
            var ackBody = RepairEquipmentAckBuilder.Build(invenType, ackSlot, result.UpdatedGold);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0017, ackBody));

            FileLogger.Log($"[{ProtocolName}] REPAIR_EQUIPMENT: OK inven_type={invenType} ackSlot=0x{(ushort)ackSlot:X4} cost={result.Cost} remainGold={result.UpdatedGold}");
        }

        private static InventoryListType? MapInvenTypeToListType(byte invenType)
        {
            switch (invenType)
            {
                case 0: return InventoryListType.Main;          // 背包/快捷栏 → character_items
                case 3: return InventoryListType.Equipment;     // 穿戴装备 → character_equipped_entries
                case 2: return InventoryListType.PersonalCargo; // 货柜
                default: return null;
            }
        }
    }
}
