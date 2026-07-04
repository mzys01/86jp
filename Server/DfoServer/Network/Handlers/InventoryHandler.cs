using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        // 背包操作直连共享 store; 门面只保留选角初始化本职(称号簿/成就/水晶契约/全量快照/宠物快照)
        private readonly IInventoryStore _inventoryStore;
        private readonly SqliteSelectCharacterDataSource _sqliteSelectCharacterDataSource;
        private readonly ICharacterRepository _characterRepository;
        private readonly InventoryRefreshSender _refresh;
        private readonly Func<byte[], Task> _broadcastGamePacket;

        public string ProtocolName => "GameProtocol";

        public InventoryHandler(
            IInventoryStore inventoryStore,
            SqliteSelectCharacterDataSource sqliteSelectCharacterDataSource,
            ICharacterRepository characterRepository,
            InventoryRefreshSender refreshSender,
            Func<byte[], Task> broadcastGamePacket = null)
        {
            _inventoryStore = inventoryStore ?? throw new ArgumentNullException(nameof(inventoryStore));
            _sqliteSelectCharacterDataSource = sqliteSelectCharacterDataSource ?? throw new ArgumentNullException(nameof(sqliteSelectCharacterDataSource));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _refresh = refreshSender ?? throw new ArgumentNullException(nameof(refreshSender));
            _broadcastGamePacket = broadcastGamePacket;
        }

        public static (int characterId, int accountId) ResolveOwner(EnhancedClientSession session)
            => SessionOwnerResolver.Resolve(session);

        public static bool TryParseDeleteOrSellRequest(byte[] body, out InventoryListType listType, out short slotIndex, out short itemCount)
        {
            listType = InventoryListType.Main;
            slotIndex = 0;
            itemCount = 0;

            if (body == null || body.Length < 4)
                return false;

            if (body.Length >= 5 && Enum.IsDefined(typeof(InventoryListType), (byte)body[0]))
            {
                listType = (InventoryListType)body[0];
                slotIndex = BitConverter.ToInt16(body, 1);
                itemCount = BitConverter.ToInt16(body, 3);
                return true;
            }

            slotIndex = BitConverter.ToInt16(body, 0);
            itemCount = BitConverter.ToInt16(body, 2);
            return true;
        }

        private static List<PackageGrantedItem> ToPackageGrantedItems(BoosterUseResult result)
        {
            var items = new List<PackageGrantedItem>();
            if (result == null)
                return items;

            foreach (var reward in result.Rewards)
            {
                items.Add(new PackageGrantedItem
                {
                    ListType = reward.ListType,
                    SlotIndex = reward.SlotIndex,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = reward.GrantedCount <= 0 ? 1 : reward.GrantedCount,
                    Durability = 0,
                });
            }

            return items;
        }
    }
}
