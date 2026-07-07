using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.DailyReset;
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
        private readonly ExperienceItemUseService _experienceItemUseService;
        private readonly SqliteSelectCharacterDataSource _sqliteSelectCharacterDataSource;
        private readonly ICharacterRepository _characterRepository;
        private readonly InventoryRefreshSender _refresh;
        private readonly ExperienceItemNotificationService _experienceItemNotifications;
        private readonly DailyResetService _dailyResetService;
        private readonly LotteryOpenPlanner _lotteryOpenPlanner;
        private readonly Func<byte[], Task> _broadcastGamePacket;

        public string ProtocolName => "GameProtocol";

        internal InventoryHandler(
            IInventoryStore inventoryStore,
            ExperienceItemUseService experienceItemUseService,
            SqliteSelectCharacterDataSource sqliteSelectCharacterDataSource,
            ICharacterRepository characterRepository,
            InventoryRefreshSender refreshSender,
            ExperienceItemNotificationService experienceItemNotifications,
            DailyResetService dailyResetService,
            Func<byte[], Task> broadcastGamePacket = null)
        {
            _inventoryStore = inventoryStore ?? throw new ArgumentNullException(nameof(inventoryStore));
            _experienceItemUseService = experienceItemUseService
                ?? throw new ArgumentNullException(nameof(experienceItemUseService));
            _sqliteSelectCharacterDataSource = sqliteSelectCharacterDataSource ?? throw new ArgumentNullException(nameof(sqliteSelectCharacterDataSource));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _refresh = refreshSender ?? throw new ArgumentNullException(nameof(refreshSender));
            _experienceItemNotifications = experienceItemNotifications
                ?? throw new ArgumentNullException(nameof(experienceItemNotifications));
            _dailyResetService = dailyResetService ?? throw new ArgumentNullException(nameof(dailyResetService));
            _lotteryOpenPlanner = new LotteryOpenPlanner(_dailyResetService);
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

        internal static List<PackageGrantedItem> ToBoosterPopupGrantedItemsForSelfTest(BoosterUseResult result)
        {
            return ToBoosterPopupGrantedItems(result);
        }

        internal static List<PackageGrantedItem> ToAggregatedBoosterGrantedItemsForSelfTest(BoosterUseResult result)
        {
            return ToPackageGrantedItems(result);
        }

        private static List<PackageGrantedItem> ToBoosterPopupGrantedItems(BoosterUseResult result)
        {
            var items = new List<PackageGrantedItem>();
            if (result == null)
                return items;

            AddPopupItems(items, result.DisplayRewards);
            AddPopupItems(items, result.DoubleRewards);
            return items.Count > 0 ? items : ToPackageGrantedItems(result);
        }

        private static void AddPopupItems(List<PackageGrantedItem> target, IEnumerable<PackageGrantedItem> source)
        {
            if (target == null || source == null)
                return;

            foreach (var reward in source)
            {
                if (reward == null || reward.ItemTemplateId <= 0 || reward.DisplayCount <= 0)
                    continue;

                target.Add(new PackageGrantedItem
                {
                    ListType = reward.ListType,
                    SlotIndex = reward.SlotIndex,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = reward.DisplayCount <= 0 ? 1 : reward.DisplayCount,
                    Durability = reward.Durability,
                });
            }
        }

        private static List<PackageGrantedItem> ToPackageGrantedItems(BoosterUseResult result)
        {
            var items = new List<PackageGrantedItem>();
            if (result == null)
                return items;

            foreach (var reward in result.Rewards
                .GroupBy(reward => new { reward.ListType, reward.ItemTemplateId })
                .Select(group => new
                {
                    group.Key.ListType,
                    group.Key.ItemTemplateId,
                    SlotIndex = group.First().SlotIndex,
                    DisplayCount = group.Sum(reward => reward.GrantedCount <= 0 ? 1 : reward.GrantedCount),
                }))
            {
                items.Add(new PackageGrantedItem
                {
                    ListType = reward.ListType,
                    SlotIndex = reward.SlotIndex,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = reward.DisplayCount,
                    Durability = 0,
                });
            }

            return items;
        }
    }
}
