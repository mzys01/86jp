using DfoServer.Game.Inventory;
using DfoServer.Network.Handlers.Dungeon;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class DungeonHandler
    {
        public string ProtocolName => "GameProtocol";

        private readonly DungeonSharedServices _services;
        private readonly DungeonEntryHandler _entry;
        private readonly DungeonMapHandler _map;
        private readonly DungeonCombatHandler _combat;
        private readonly DungeonSettlementHandler _settlement;
        private readonly DungeonTutorialHandler _tutorial;

        public DungeonHandler(IAssetService assetService)
        {
            _services = new DungeonSharedServices(assetService);
            _settlement = new DungeonSettlementHandler(_services);
            _map = new DungeonMapHandler(_services);
            _entry = new DungeonEntryHandler(_services, _map);
            _combat = new DungeonCombatHandler(_services, _settlement);
            _tutorial = new DungeonTutorialHandler(_services, _settlement);
        }

        public static void ResetDungeonState(EnhancedClientSession session)
            => DungeonSharedServices.ResetDungeonState(session);

        public Task Handle_ENUM_CMDPACKET_ENTER_SELECT_DUNGEON(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _entry.HandleEnterSelectDungeon(session, header, body);

        public Task Handle_ENUM_CMDPACKET_SELECT_DUNGEON(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _entry.HandleSelectDungeon(session, header, body);

        public Task Handle_ENUM_CMDPACKET_GORGEOUS_CHALLENGE_TOGGLE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _entry.HandleGorgeousChallengeToggle(session, header, body);

        public Task Handle_ENUM_CMDPACKET_MOVE_MAP(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _map.HandleMoveMap(session, header, body);

        public Task Handle_ENUM_CMDPACKET_HELLPARTY_START(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _map.HandleHellPartyStart(session, header, body);

        public Task Handle_ENUM_CMDPACKET_DIE_MONSTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _combat.HandleDieMonster(session, header, body);

        public Task Handle_ENUM_CMDPACKET_DIE_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _combat.HandleDieCharacter(session, header, body);

        public Task Handle_ENUM_CMDPACKET_USE_COIN(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _combat.HandleUseCoin(session, header, body);

        public Task Handle_ENUM_CMDPACKET_GET_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _combat.HandleGetItem(session, header, body);

        public Task Handle_ENUM_CMDPACKET_SELECT_CARD(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _settlement.HandleSelectCard(session, header, body);

        public Task Handle_ENUM_CMDPACKET_EPLP_COMMAND(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _settlement.HandleEplpCommand(session, header, body);

        public Task Handle_CARD_START_REQUEST(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _settlement.HandleCardStartRequest(session, header, body);

        public Task Handle_SET_PLAY_RESULT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _settlement.HandleSetPlayResult(session, header, body);

        public Task Handle_ENUM_CMDPACKET_DUNGEON_EVENT_STORY_PAUSE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _tutorial.HandleStoryPause(session, header, body);

        public Task Handle_ENUM_CMDPACKET_CHANGE_TUTORIAL_FLAG(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _tutorial.HandleChangeTutorialFlag(session, header, body);

        public Task Handle_ENUM_CMDPACKET_TUTORIAL_LEVEL_UP(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _tutorial.HandleTutorialLevelUp(session, header, body);

        public Task Handle_BACK_2_VILLAGE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _tutorial.HandleBack2Village(session, header, body);
    }
}
