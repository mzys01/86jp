using DfoServer.Game.Accounts;
using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Legacy;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network
{
    public class GameProtocolHandler : BaseProtocolHandler
    {
        private readonly LoginHandler _loginHandler;
        private readonly CharacterSelectHandler _characterSelectHandler;
        private readonly InventoryHandler _inventoryHandler;
        private readonly TownHandler _townHandler;
        private readonly DungeonHandler _dungeonHandler;
        private readonly SkillHandler _skillHandler;
        private readonly SettingsHandler _settingsHandler;
        private readonly CeraShopHandler _ceraShopHandler;
        private readonly ICharacterRepository _characterRepository;
        private readonly SqliteSelectCharacterDataSource _selectCharacterDataSource;

        public override string ProtocolName => "GameProtocol";

        public GameProtocolHandler()
        {
            var databasePath = ServerPaths.DatabasePath;
            var schemaFilePath = ServerPaths.SchemaFilePath;

            var characterRepository = new SqliteCharacterRepository(databasePath, schemaFilePath);
            var accountRepository = new SqliteAccountRepository(databasePath, schemaFilePath);

            var sqliteSelectCharacterDataSource = new SqliteSelectCharacterDataSource(
                databasePath,
                schemaFilePath,
                characterRepository);

            var userInfoBlobRepository = new Game.CharacterData.SqliteUserInfoBlobRepository(databasePath, schemaFilePath);
            var getUserInfoTemplate = userInfoBlobRepository.LoadGetUserInfoTemplate();

            _characterRepository = characterRepository;
            _selectCharacterDataSource = sqliteSelectCharacterDataSource;
            _loginHandler = new LoginHandler(accountRepository);
            _characterSelectHandler = new CharacterSelectHandler(sqliteSelectCharacterDataSource, characterRepository, getUserInfoTemplate);
            _inventoryHandler = new InventoryHandler(sqliteSelectCharacterDataSource, characterRepository);
            _townHandler = new TownHandler(characterRepository, sqliteSelectCharacterDataSource);
            _dungeonHandler = new DungeonHandler();
            _skillHandler = new SkillHandler(characterRepository);
            _settingsHandler = new SettingsHandler();
            _ceraShopHandler = new CeraShopHandler(sqliteSelectCharacterDataSource);
        }

        public override async Task OnClientConnected(EnhancedClientSession session)
        {
            FileLogger.Log($"[{ProtocolName}] Admin client connected: {session.SessionId}");
            await _loginHandler.Handle_ClientFirstConnected(session);
        }

        public override Task OnClientDisconnected(EnhancedClientSession session)
        {
            FileLogger.Log($"[{ProtocolName}] Admin client disconnected: {session.SessionId}");
            _townHandler.PersistPosition(session, forceImmediate: true, source: "disconnect");
            return Task.CompletedTask;
        }

        public override async Task OnPacketReceived(EnhancedClientSession session, FlexiblePacket packet)
        {
            var header = packet.GetHeader<GamePacketHeader>();
            var body = packet.BodyData;

            PacketFileLogger.Log("RECV", packet.GetBytes());

            try
            {
                await OnPacketReceived_86JP(session, header, body);
            }
            catch (Exception ex)
            {
                FileLogger.Log(ex.ToString());
                throw;
            }
        }

        public async Task OnPacketReceived_86JP(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (header.cmd == 0)
            {

            }

            if (header.cmd == 1)
            {
                switch (header.type)
                {
                    case 0x0001:
                        await _loginHandler.Handle_ENUM_CMDPACKET_LOGIN(session, header, body);
                        break;
                    case 0x0002:
                        break;
                    case 0x0003:
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0003, CommonPacketBodyBuilder.BuildSuccessAck()));
                        break;
                    case 0x0004:
                        await _characterSelectHandler.Handle_ENUM_CMDPACKET_SELECT_CHARACTER(session, header, body);
                        if (session.Player != null && session.Player.CharacterId > 0)
                        {
                            var gsConnStr = SqliteDatabaseBootstrap.Initialize(
                                ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                            session.GameSession = new Game.Session.GameSession(session, gsConnStr);
                        }
                        break;
                    case 0x0005:
                        await _characterSelectHandler.Handle_ENUM_CMDPACKET_CREATE_CHARACTER(session, header, body);
                        break;
                    case 0x0006:
                        await _characterSelectHandler.Handle_ENUM_CMDPACKET_DELETE_CHARACTER(session, header, body);
                        break;
                    case 0x0007:
                        await _characterSelectHandler.Handle_ENUM_CMDPACKET_RETURN_SELECT_CHARACTER(session, header, body);
                        break;
                    case 0x0008:
                        await _characterSelectHandler.Handle_ENUM_CMDPACKET_GET_USERINFO(session, header, body);
                        break;
                    case 0x000F:
                        await _dungeonHandler.Handle_ENUM_CMDPACKET_ENTER_SELECT_DUNGEON(session, header, body);
                        break;
                    case 0x0010:
                        await _dungeonHandler.Handle_ENUM_CMDPACKET_SELECT_DUNGEON(session, header, body);
                        break;
                    case 0x0012://18
                        await _inventoryHandler.Handle_ENUM_CMDPACKET_DELETE_ITEM(session, header, body);
                        break;
                    case 0x0013://19
                        await _inventoryHandler.Handle_ENUM_CMDPACKET_MOVE_ITEMSPACE(session, header, body);
                        break;
                    case 0x0014://20
                        await _inventoryHandler.Handle_ENUM_CMDPACKET_SORT_ITEM(session, header, body);
                        break;
                    case 0x0015://21
                        await _inventoryHandler.Handle_ENUM_CMDPACKET_BUY_ITEM(session, header, body);
                        break;
                    case 0x0016://22
                        await _inventoryHandler.Handle_ENUM_CMDPACKET_SELL_ITEM(session, header, body);
                        break;
                    case 0x001C://28
                        await _skillHandler.Handle_CHANGE_SKILLSLOT(session, header, body);
                        break;
                    case 0x001D://29
                        await _skillHandler.Handle_BUY_SKILL(session, header, body);
                        break;
                    case 0x014B://331
                        await _skillHandler.Handle_CHANGE_SKILL_COMMAND(session, header, body);
                        break;
                    case 0x014C://332
                        await _skillHandler.Handle_RESET_ALL_SKILL_COMMANDS(session, header, body);
                        break;
                    case 0x0239://569
                        await _inventoryHandler.Handle_SET_CLONE_TITLE(session, header, body);
                        break;
                    case 0x01EC://492
                        await _skillHandler.Handle_SKILL_INIT(session, header, body);
                        break;
                    case 0x001F://31
                        if (session.GameSession != null)
                            await session.GameSession.QuestManager.HandleAcceptQuestAsync(header.type, body);
                        break;
                    case 0x0020://32
                        if (session.GameSession != null)
                            await session.GameSession.QuestManager.HandleGiveupQuestAsync(header.type, body);
                        break;
                    case 0x0021://33
                        if (session.GameSession != null)
                            await session.GameSession.QuestManager.HandleSetTriggerAsync(header.type, body);
                        break;
                    case 0x0022://34
                        if (session.GameSession != null)
                            await session.GameSession.QuestManager.HandleFinishQuestAsync(header.type, body);
                        break;
                    case 0x0023:
                        await _townHandler.Handle_ENUM_CMDPACKET_SET_USER_POSITION(session, header, body);
                        break;
                    case 0x0024:
                        await _townHandler.Handle_ENUM_CMDPACKET_SET_USER_AREA(session, header, body);
                        break;
                    case 0x0025:
                        await _townHandler.Handle_ENUM_CMDPACKET_FINISH_LOADING(session, header, body);
                        break;
                    case 0x0026:
                        break;
                    case 0x0027:
                        await _dungeonHandler.Handle_ENUM_CMDPACKET_DIE_MONSTER(session, header, body);
                        break;
                    case 0x002B:
                        await _dungeonHandler.Handle_ENUM_CMDPACKET_GET_ITEM(session, header, body);
                        break;
                    case 0x002A:
                    case 0x0084:
                        await _townHandler.Handle_ENUM_CMDPACKET_GIVEUP_GAME(session, header, body);
                        break;
                    case 0x0028://40
                        await _dungeonHandler.Handle_ENUM_CMDPACKET_DIE_CHARACTER(session, header, body);
                        break;
                    case 0x0029:
                        await _dungeonHandler.Handle_ENUM_CMDPACKET_USE_COIN(session, header, body);
                        break;
                    case 0x0047:
                    case 0x0048:
                        await _dungeonHandler.Handle_ENUM_CMDPACKET_SELECT_CARD(session, header, body);
                        break;
                    case 0x00A0:
                        await _inventoryHandler.Handle_OPEN_SELECTABLE_PACKAGE(session, header, body);
                        break;
                    case 0x0218:
                        await _inventoryHandler.Handle_USE_BOOSTER_ITEM(session, header, body);
                        break;
                    case 0x002E://46
                        await _dungeonHandler.Handle_SET_PLAY_RESULT(session, header, body);
                        break;
                    case 0x0040://64 商城购买
                        await _ceraShopHandler.HandleCeraShopPurchase(session, header, body);
                        break;
                    case 0x002C:
                        await _inventoryHandler.Handle_ENUM_CMDPACKET_USE_STACKABLE(session, header, body);
                        break;
                    case 0x002D:
                        await _dungeonHandler.Handle_ENUM_CMDPACKET_MOVE_MAP(session, header, body);
                        break;
                    case 0x008F://143
                        await _dungeonHandler.Handle_ENUM_CMDPACKET_CHANGE_TUTORIAL_FLAG(session, header, body);
                        break;
                    case 0x00BF://191
                        await _dungeonHandler.Handle_ENUM_CMDPACKET_DUNGEON_EVENT_STORY_PAUSE(session, header, body);
                        break;
                    case 0x007A://122
                        break;
                    case 0x00AB://171
                        break;
                    case 0x00ED:
                        await _townHandler.Handle_ENUM_CMDPACKET_TELEPORT(session, header, body);
                        break;
                    case 0x0078://120
                        break;
                    case 0x0110://272 附魔宝珠
                        await _inventoryHandler.Handle_ENUM_CMDPACKET_ENCHANT_BY_BEAD(session, header, body);
                        break;
                    case 0x0118://280
                        break;
                    case 0x01A1://417
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01A1, LegacyPacketBodyBuilder.BuildVerifyPvpLagResponse(body)));
                        break;
                    case 0x019C://412
                    case 0x019D://413
                        await _inventoryHandler.Handle_TITLE_BOOK(session, header, body);
                        break;
                    case 0x01E4://484
                        await _dungeonHandler.Handle_ENUM_CMDPACKET_TUTORIAL_LEVEL_UP(session, header, body);
                        break;
                    case 0x01F8://504
                        break;
                    case 0x01DE: // 478
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01DE, CommonPacketBodyBuilder.BuildSuccessAck()));
                        break;
                    case 0x0207:
                        await _inventoryHandler.Handle_OPEN_AVATAR_PACKAGE(session, header, body);
                        break;
                    case 0x0252://594
                        break;
                    case 0x02C1://705
                    case 0x01BA://442
                        break;
                    case 0x02B5:
                        await _characterSelectHandler.Handle_ENUM_CMDPACKET_CHECK_DOUBLE_CHARACTER_NAME(session, header, body);
                        break;
                    case 0x02A8:
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02A8, new byte[] { 0x00, 0x00 }));
                        break;
                    case 0x00C5:
                        _settingsHandler.Handle_SAVE_GAME_OPTION_1(session, header, body);
                        break;
                    case 0x00C6:
                        _settingsHandler.Handle_SAVE_GAME_OPTION_2(session, header, body);
                        break;
                    case 0x0170:
                        _settingsHandler.Handle_SAVE_QUICKCHAT(session, header, body);
                        break;
                    default:
                        break;
                }
            }
        }
    }
}
