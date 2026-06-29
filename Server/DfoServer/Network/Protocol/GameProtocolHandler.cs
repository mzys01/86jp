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
using System.Collections.Generic;
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
        private readonly LuckyStarHandler _luckyStarHandler;
        private readonly RentalHandler _rentalHandler;
        private readonly MailboxHandler _mailboxHandler;
        private readonly ICharacterRepository _characterRepository;
        private readonly SqliteSelectCharacterDataSource _selectCharacterDataSource;
        private readonly SqliteAssetService _assetService;
        private readonly Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> _cmdDispatch;

        public override string ProtocolName => "GameProtocol";

        public GameProtocolHandler()
        {
            var databasePath = ServerPaths.DatabasePath;
            var schemaFilePath = ServerPaths.SchemaFilePath;

            var characterRepository = new SqliteCharacterRepository(databasePath, schemaFilePath);
            var accountRepository = new SqliteAccountRepository(databasePath, schemaFilePath);

            _assetService = new SqliteAssetService(databasePath, schemaFilePath);

            var sqliteSelectCharacterDataSource = new SqliteSelectCharacterDataSource(
                databasePath,
                schemaFilePath,
                characterRepository,
                _assetService);

            var userInfoBlobRepository = new Game.CharacterData.SqliteUserInfoBlobRepository(databasePath, schemaFilePath);
            var getUserInfoTemplate = userInfoBlobRepository.LoadGetUserInfoTemplate();

            _characterRepository = characterRepository;
            _selectCharacterDataSource = sqliteSelectCharacterDataSource;
            _loginHandler = new LoginHandler(accountRepository);
            _characterSelectHandler = new CharacterSelectHandler(sqliteSelectCharacterDataSource, characterRepository, getUserInfoTemplate);
            _inventoryHandler = new InventoryHandler(sqliteSelectCharacterDataSource, characterRepository);
            _townHandler = new TownHandler(characterRepository, sqliteSelectCharacterDataSource);
            _dungeonHandler = new DungeonHandler(_assetService);
            _skillHandler = new SkillHandler(characterRepository);
            _settingsHandler = new SettingsHandler();
            _ceraShopHandler = new CeraShopHandler(sqliteSelectCharacterDataSource);
            _luckyStarHandler = new LuckyStarHandler(_assetService, sqliteSelectCharacterDataSource);
            _rentalHandler = new RentalHandler(_assetService, sqliteSelectCharacterDataSource);
            _mailboxHandler = new MailboxHandler();

            _cmdDispatch = new Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>>();
            RegisterLoginHandlers(_cmdDispatch);
            RegisterCharacterHandlers(_cmdDispatch);
            RegisterInventoryHandlers(_cmdDispatch);
            RegisterSortItemLockHandlers(_cmdDispatch);
            RegisterEquipmentSocketHandlers(_cmdDispatch);
            RegisterAvatarSocketHandlers(_cmdDispatch);
            RegisterDungeonHandlers(_cmdDispatch);
            RegisterSkillHandlers(_cmdDispatch);
            RegisterTownHandlers(_cmdDispatch);
            RegisterSettingsHandlers(_cmdDispatch);
            RegisterQuestHandlers(_cmdDispatch);
            RegisterMailboxHandlers(_cmdDispatch);
            RegisterMiscHandlers(_cmdDispatch);
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
                if (_cmdDispatch.TryGetValue(header.type, out var handler))
                    await handler(session, header, body);
                else
                    FileLogger.Log($"[GameProtocol] Unhandled CMD type=0x{header.type:X4} body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
            }
        }

        #region CMD Dispatch Registration

        private void RegisterLoginHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0001] = _loginHandler.Handle_ENUM_CMDPACKET_LOGIN;
        }

        private void RegisterCharacterHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0004] = async (s, h, b) =>
            {
                await _characterSelectHandler.Handle_ENUM_CMDPACKET_SELECT_CHARACTER(s, h, b);
                if (s.Player != null && s.Player.CharacterId > 0)
                {
                    var gsConnStr = SqliteDatabaseBootstrap.Initialize(
                        ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
                    s.GameSession = new Game.Session.GameSession(s, gsConnStr, _assetService);
                    await _inventoryHandler.SendAllSortItemLockRefresh(s);
                }
            };
            d[0x0005] = _characterSelectHandler.Handle_ENUM_CMDPACKET_CREATE_CHARACTER;
            d[0x0006] = _characterSelectHandler.Handle_ENUM_CMDPACKET_DELETE_CHARACTER;
            d[0x0007] = _characterSelectHandler.Handle_ENUM_CMDPACKET_RETURN_SELECT_CHARACTER;
            d[0x0008] = _characterSelectHandler.Handle_ENUM_CMDPACKET_GET_USERINFO;
            d[0x0009] = _dungeonHandler.Handle_ENUM_CMDPACKET_RECOVER_STAMINA;
            d[0x02B5] = _characterSelectHandler.Handle_ENUM_CMDPACKET_CHECK_DOUBLE_CHARACTER_NAME;
        }

        private void RegisterInventoryHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0012] = _inventoryHandler.Handle_ENUM_CMDPACKET_DELETE_ITEM;       //18
            d[0x0013] = _inventoryHandler.Handle_ENUM_CMDPACKET_MOVE_ITEMSPACE;    //19
            d[0x0014] = _inventoryHandler.Handle_ENUM_CMDPACKET_SORT_ITEM;         //20
            d[0x0015] = _inventoryHandler.Handle_ENUM_CMDPACKET_BUY_ITEM;          //21
            d[0x0016] = _inventoryHandler.Handle_ENUM_CMDPACKET_SELL_ITEM;         //22
            d[0x002C] = _inventoryHandler.Handle_ENUM_CMDPACKET_USE_STACKABLE;
            d[0x00A0] = _inventoryHandler.Handle_OPEN_SELECTABLE_PACKAGE;
            d[0x0110] = _inventoryHandler.Handle_ENUM_CMDPACKET_ENCHANT_BY_BEAD;   //272
            d[0x019C] = _inventoryHandler.Handle_TITLE_BOOK;                       //412
            d[0x019D] = _inventoryHandler.Handle_TITLE_BOOK;                       //413
            d[0x0207] = _inventoryHandler.Handle_OPEN_AVATAR_PACKAGE;
            d[0x0218] = _inventoryHandler.Handle_USE_BOOSTER_ITEM;
            d[0x0239] = _inventoryHandler.Handle_SET_CLONE_TITLE;                  //569
            d[0x0063] = _inventoryHandler.Handle_COMPOUND_AVATAR;                  //99 合并装扮(时装合成)
            d[0x03EA] = _inventoryHandler.Handle_COMPOUND_AVATAR_SET;              //1002 8件高级装扮100%合成稀有装扮(克隆装扮合成器)
        }

        private void RegisterSortItemLockHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x02CA] = _inventoryHandler.Handle_ENUM_CMDPACKET_TOGGLE_SORT_ITEM_LOCK;
            d[0x02CB] = _inventoryHandler.Handle_ENUM_CMDPACKET_UNLOCK_SORT_ITEM_LOCK;
        }

        private void RegisterEquipmentSocketHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x031D] = _inventoryHandler.Handle_EQUIPMENT_SOCKET_OPEN;
        }

        private void RegisterAvatarSocketHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x00CE] = _inventoryHandler.Handle_AVATAR_SOCKET_OPEN;
        }

        private void RegisterDungeonHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x000F] = _dungeonHandler.Handle_ENUM_CMDPACKET_ENTER_SELECT_DUNGEON;
            d[0x0010] = _dungeonHandler.Handle_ENUM_CMDPACKET_SELECT_DUNGEON;
            d[0x0027] = _dungeonHandler.Handle_ENUM_CMDPACKET_DIE_MONSTER;
            d[0x0028] = _dungeonHandler.Handle_ENUM_CMDPACKET_DIE_CHARACTER;       //40
            d[0x0029] = _dungeonHandler.Handle_ENUM_CMDPACKET_USE_COIN;
            d[0x002B] = _dungeonHandler.Handle_ENUM_CMDPACKET_GET_ITEM;
            d[0x002D] = _dungeonHandler.Handle_ENUM_CMDPACKET_MOVE_MAP;
            d[0x002E] = _dungeonHandler.Handle_SET_PLAY_RESULT;                    //46
            d[0x0045] = _dungeonHandler.Handle_CARD_START_REQUEST;
            d[0x0047] = _dungeonHandler.Handle_ENUM_CMDPACKET_SELECT_CARD;
            d[0x0048] = _dungeonHandler.Handle_ENUM_CMDPACKET_EPLP_COMMAND;
            d[0x00EB] = _dungeonHandler.Handle_ENUM_CMDPACKET_HELLPARTY_START;     //235
            d[0x008F] = _dungeonHandler.Handle_ENUM_CMDPACKET_CHANGE_TUTORIAL_FLAG; //143
            d[0x00BF] = _dungeonHandler.Handle_ENUM_CMDPACKET_DUNGEON_EVENT_STORY_PAUSE; //191
            d[0x01E4] = _dungeonHandler.Handle_ENUM_CMDPACKET_TUTORIAL_LEVEL_UP;   //484
            d[0x0312] = _dungeonHandler.Handle_ENUM_CMDPACKET_0312;
            d[0x03B6] = _dungeonHandler.Handle_ENUM_CMDPACKET_GORGEOUS_CHALLENGE_TOGGLE;
        }

        private void RegisterSkillHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x001C] = _skillHandler.Handle_CHANGE_SKILLSLOT;                     //28
            d[0x001D] = _skillHandler.Handle_BUY_SKILL;                            //29
            d[0x014B] = _skillHandler.Handle_CHANGE_SKILL_COMMAND;                 //331
            d[0x014C] = _skillHandler.Handle_RESET_ALL_SKILL_COMMANDS;             //332
            d[0x01EC] = _skillHandler.Handle_SKILL_INIT;                           //492
        }

        private void RegisterTownHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0023] = _townHandler.Handle_ENUM_CMDPACKET_SET_USER_POSITION;
            d[0x0024] = _townHandler.Handle_ENUM_CMDPACKET_SET_USER_AREA;
            d[0x0025] = _townHandler.Handle_ENUM_CMDPACKET_FINISH_LOADING;
            d[0x002A] = _townHandler.Handle_ENUM_CMDPACKET_GIVEUP_GAME;
            d[0x0084] = _townHandler.Handle_ENUM_CMDPACKET_GIVEUP_GAME;
            d[0x00ED] = _townHandler.Handle_ENUM_CMDPACKET_TELEPORT;
        }

        private void RegisterSettingsHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x00C5] = (s, h, b) => { _settingsHandler.Handle_SAVE_GAME_OPTION_1(s, h, b); return Task.CompletedTask; };
            d[0x00C6] = (s, h, b) => { _settingsHandler.Handle_SAVE_GAME_OPTION_2(s, h, b); return Task.CompletedTask; };
            d[0x0170] = (s, h, b) => { _settingsHandler.Handle_SAVE_QUICKCHAT(s, h, b); return Task.CompletedTask; };
        }

        private void RegisterQuestHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x001F] = async (s, h, b) => //31
            {
                if (s.GameSession != null)
                    await s.GameSession.QuestManager.HandleAcceptQuestAsync(h.type, b);
            };
            d[0x0020] = async (s, h, b) => //32
            {
                if (s.GameSession != null)
                    await s.GameSession.QuestManager.HandleGiveupQuestAsync(h.type, b);
            };
            d[0x0021] = async (s, h, b) => //33
            {
                if (s.GameSession != null)
                    await s.GameSession.QuestManager.HandleSetTriggerAsync(h.type, b);
            };
            d[0x0022] = async (s, h, b) => //34
            {
                if (s.GameSession != null)
                    await s.GameSession.QuestManager.HandleFinishQuestAsync(h.type, b);
            };
        }

        private void RegisterMailboxHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0060] = _mailboxHandler.HandleOpenMailbox;
        }

        private void RegisterMiscHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0003] = (s, h, b) =>
                s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0003, CommonPacketBodyBuilder.BuildSuccessAck()));
            d[0x0040] = _ceraShopHandler.HandleCeraShopPurchase;                   //64
            d[0x01A1] = (s, h, b) =>                                               //417
                s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01A1, LegacyPacketBodyBuilder.BuildVerifyPvpLagResponse(b)));
            d[0x01DE] = (s, h, b) =>                                               //478
                s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01DE, CommonPacketBodyBuilder.BuildSuccessAck()));
            d[0x02A8] = (s, h, b) =>
                s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02A8, new byte[] { 0x00, 0x00 }));
            d[0x0372] = _rentalHandler.HandleRentWeapon;
            d[0x0373] = _luckyStarHandler.HandleShopPurchasePacket;
        }

        #endregion
    }
}
