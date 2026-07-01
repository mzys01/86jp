using System;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    // NOTI 0x0381(897): 收集箱初始化推送，按 PVF occurrenceIndex 逐个推送每个收集箱。
    public sealed class CollectionBoxBodyBuilder : IInitPacketBuilder
    {
        private readonly CollectBoxProgressRepository _progressRepository;

        public CollectionBoxBodyBuilder(CollectBoxProgressRepository progressRepository)
        {
            _progressRepository = progressRepository;
        }

        public ushort NotiType => 0x0381;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var indexes = CollectBoxDataService.GetAllIndexes();
            if (occurrenceIndex < 0 || occurrenceIndex >= indexes.Count)
            {
                body = Array.Empty<byte>();
                return false;
            }

            var characterId = snapshot.CharacterRecord?.CharacterId ?? 0;
            return TryBuildForBox(_progressRepository, characterId, indexes[occurrenceIndex], out body);
        }

        // 供选角初始化(TryBuild)和运行时推送(放入/取出宝珠后)共用。
        public static bool TryBuildForBox(CollectBoxProgressRepository progressRepository, int characterId, int boxIndex, out byte[] body)
        {
            var entry = CollectBoxDataService.GetByIndex(boxIndex);
            if (entry == null)
            {
                body = Array.Empty<byte>();
                return false;
            }

            // 协议字段语义(参考工程 df_game_r.c 确认)：
            //   statusFlags=1, remainingSeconds=0          → 无限制
            //   statusFlags=0, remainingSeconds=剩余秒数   → 倒计时
            //   statusFlags=0, remainingSeconds=0xFFFFFFFF → 已过期
            uint remainingSeconds = 0;
            byte statusFlags = 1;
            if (!string.IsNullOrEmpty(entry.MaxExpirationDate) &&
                DateTime.TryParse(entry.MaxExpirationDate, out var maxExpire))
            {
                var remaining = maxExpire - DateTime.Now;
                if (remaining.TotalSeconds > 0)
                {
                    remainingSeconds = (uint)remaining.TotalSeconds;
                    statusFlags = 0;
                }
                else
                {
                    remainingSeconds = 0xFFFFFFFF;
                    statusFlags = 0;
                }
            }

            var savedSlots = characterId > 0
                ? progressRepository.LoadSlots(characterId, entry.Index)
                : Array.Empty<CollectBoxSlotEntry>();
            var itemCount = savedSlots.Count;

            body = new byte[8 + itemCount * 4];
            body[0] = (byte)entry.Index;  // BoxType = PVF [Index]
            body[1] = 1;                  // refresh=1，客户端才会把数据标记为已就绪，否则持续轮询
            Buffer.BlockCopy(BitConverter.GetBytes(remainingSeconds), 0, body, 2, 4);
            body[6] = statusFlags;
            body[7] = (byte)itemCount;
            for (var i = 0; i < itemCount; i++)
                Buffer.BlockCopy(BitConverter.GetBytes((uint)savedSlots[i].ItemId), 0, body, 8 + i * 4, 4);

            return true;
        }
    }
}
