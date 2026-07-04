using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class CollectionBoxHandler
    {
        private readonly Game.Inventory.IInventoryStore _dataSource;
        private readonly CollectBoxProgressRepository _progressRepository;

        public CollectionBoxHandler(Game.Inventory.IInventoryStore dataSource, CollectBoxProgressRepository progressRepository)
        {
            _dataSource = dataSource;
            _progressRepository = progressRepository;
        }

        // CMD 0x0388 (904) 打开收集箱面板
        // 回包 body[0]=非零开关, body[1]=index，客户端凭 index 切换到对应收集箱页签。
        // 实测：body 末尾字节 = PVF [Index] 原值，回包必须原样带回，否则页签会被强制跳回 Index1。
        public async Task HandleQueryCollectionBox(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            int boxIndex = body != null && body.Length > 0 ? body[body.Length - 1] : 0;
            var entry = CollectBoxDataService.GetByIndex(boxIndex);

            session.SelectedCollectionBoxIndex = entry != null ? boxIndex : 0;

            var w = new GamePacketWriter();
            w.WriteByte((byte)session.SelectedCollectionBoxIndex);
            w.WriteByte((byte)session.SelectedCollectionBoxIndex);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, w.ToArray()));
        }

        // CMD 0x0389 (905) 收集箱槛位放入宝珠
        // 请求 body: body[0..1]=boxIndex(u16), body[2..3]=slotIndex(u16), body[4..7]=itemId(u32)
        // 回包 body[0]=0 时客户端弹错误提示，body[1]=错误码；body[0]!=0 时成功。
        // 放入成功后必须推 NOTI 0381，否则客户端收集箱 UI 不会点亮槛位。
        public async Task HandleInsertCollectBoxItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            const byte ErrorCodeItemMismatch = 0x12;

            byte resultCode = 0;
            byte errorCode = ErrorCodeItemMismatch;

            if (body != null && body.Length >= 8)
            {
                int boxIndex = BitConverter.ToUInt16(body, 0);
                int slotIndex = BitConverter.ToUInt16(body, 2);
                int itemId = (int)BitConverter.ToUInt32(body, 4);

                var entry = CollectBoxDataService.GetByIndex(boxIndex);
                var isValidSlotItem = entry != null && entry.Slots.Exists(s => s.ItemId == itemId);

                if (isValidSlotItem)
                {
                    var (characterId, accountId) = SessionOwnerResolver.Resolve(session);

                    // 背包扣减与进度写入同一事务: 中间崩溃整体回滚, 宝珠不会"已扣未点亮"地丢失
                    InventoryMutationResult removeResult = null;
                    bool removed = await Task.Run(() =>
                        _dataSource.TryRemoveItemByTemplateId(characterId, accountId, itemId, out _, out removeResult,
                            (conn, tx) => _progressRepository.PutSlot(conn, tx, characterId, boxIndex, slotIndex, itemId)));

                    if (removed)
                    {
                        resultCode = 1;

                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0012, DeleteItemAckBuilder.Build(removeResult)));

                        if (Builders.CollectionBoxBodyBuilder.TryBuildForBox(_progressRepository, characterId, boxIndex, out var notiBody))
                            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0381, notiBody));
                    }
                }
            }

            var w = new GamePacketWriter();
            w.WriteByte(resultCode);
            if (resultCode == 0)
                w.WriteByte(errorCode);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, w.ToArray()));
        }

        // CMD 0x038A (906) 取出宝珠
        // 请求 body: body[0]=固定标志(含义未知), body[1..4]=itemId(u32)，不含槛位信息，靠存档表反查。
        // 归还背包与删存档记录同一事务: 中间崩溃整体回滚，宝珠既不丢失也不复制。
        public async Task HandleRemoveCollectBoxItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            const byte ErrorCodeItemMismatch = 0x12;

            byte resultCode = 0;
            byte errorCode = ErrorCodeItemMismatch;

            if (body != null && body.Length >= 5)
            {
                int itemId = (int)BitConverter.ToUInt32(body, 1);
                var (characterId, accountId) = SessionOwnerResolver.Resolve(session);

                int boxIndex = 0, slotIndex = 0;
                bool found = await Task.Run(() =>
                    _progressRepository.TryFindSlotByItem(characterId, itemId, out boxIndex, out slotIndex));

                if (found)
                {
                    short assignedSlot = 0;
                    int newStackCount = 0;
                    bool pickedUp = await Task.Run(() =>
                        _dataSource.TryPickupItem(characterId, accountId, itemId, 1, out assignedSlot, out newStackCount,
                            (conn, tx) => _progressRepository.RemoveItem(conn, tx, characterId, boxIndex, itemId)));

                    if (pickedUp)
                    {
                        resultCode = 1;

                        var returnedItem = new CommonInventoryItem
                        {
                            SlotIndex = assignedSlot,
                            ItemTemplateId = itemId,
                            CountOrInstanceValue = newStackCount,
                        };
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                            ItemListUpdateBuilder.BuildCommonUpdates(new[] { returnedItem })));

                        if (Builders.CollectionBoxBodyBuilder.TryBuildForBox(_progressRepository, characterId, boxIndex, out var notiBody))
                            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0381, notiBody));
                    }
                    else
                    {
                        errorCode = ErrorCodeItemMismatch;
                    }
                }
            }

            var w = new GamePacketWriter();
            w.WriteByte(resultCode);
            if (resultCode == 0)
                w.WriteByte(errorCode);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, w.ToArray()));
        }
    }
}
