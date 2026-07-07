using DfoServer.Game.Accounts;
using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Pets;
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
        private readonly StaminaHandler _staminaHandler;
        private readonly SkillHandler _skillHandler;
        private readonly SettingsHandler _settingsHandler;
        private readonly CeraShopHandler _ceraShopHandler;
        private readonly LuckyStarHandler _luckyStarHandler;
        private readonly RentalHandler _rentalHandler;
        private readonly MailboxHandler _mailboxHandler;
        private readonly CollectionBoxHandler _collectionBoxHandler;
        private readonly ShopCoinEventHandler _shopCoinEventHandler;
        private readonly InventoryRefreshSender _inventoryRefreshSender;
        private readonly PetCreatureHandler _petCreatureHandler;
        private readonly MercenaryHandler _mercenaryHandler;
        private readonly ICharacterRepository _characterRepository;
        private readonly SqliteSelectCharacterDataSource _selectCharacterDataSource;
        private readonly SqliteAssetService _assetService;
        private readonly Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> _cmdDispatch;

        public override string ProtocolName => "GameProtocol";

        public GameProtocolHandler(Func<byte[], Task> broadcastGamePacket = null)
        {
            var databasePath = ServerPaths.DatabasePath;
            var schemaFilePath = ServerPaths.SchemaFilePath;

            var characterRepository = new SqliteCharacterRepository(databasePath, schemaFilePath);
            var accountRepository = new SqliteAccountRepository(databasePath, schemaFilePath);
            // 租赁全链路共用同一时间源，保持绝对 Unix 到期时间模型。
            var rentalTimeProvider = SystemRentalTimeProvider.Instance;

            // 全程序共享一个 SqliteInventoryStore(无状态, 只持连接串): 旧版门面与 AssetService 各自 new 一个
            var inventoryStore = new Game.Inventory.SqliteInventoryStore(databasePath, schemaFilePath, rentalTimeProvider);
            _assetService = new SqliteAssetService(databasePath, schemaFilePath, inventoryStore);

            var sqliteSelectCharacterDataSource = new SqliteSelectCharacterDataSource(
                databasePath,
                schemaFilePath,
                characterRepository,
                _assetService,
                inventoryStore,
                rentalTimeProvider);

            var userInfoBlobRepository = new Game.CharacterData.SqliteUserInfoBlobRepository(databasePath, schemaFilePath);
            var getUserInfoTemplate = userInfoBlobRepository.LoadGetUserInfoTemplate();

            _characterRepository = characterRepository;
            _selectCharacterDataSource = sqliteSelectCharacterDataSource;
            _loginHandler = new LoginHandler(accountRepository);
            _characterSelectHandler = new CharacterSelectHandler(sqliteSelectCharacterDataSource, characterRepository, getUserInfoTemplate);
            _inventoryRefreshSender = new InventoryRefreshSender(inventoryStore, sqliteSelectCharacterDataSource, characterRepository);
            _inventoryHandler = new InventoryHandler(inventoryStore, sqliteSelectCharacterDataSource, characterRepository, _inventoryRefreshSender, broadcastGamePacket);
            _petCreatureHandler = new PetCreatureHandler(inventoryStore, sqliteSelectCharacterDataSource, _inventoryRefreshSender);
            _townHandler = new TownHandler(characterRepository, inventoryStore);
            var dailyResetService = new Game.DailyReset.DailyResetService(databasePath, schemaFilePath);
            var reviveCoinService = new Game.ReviveCoin.ReviveCoinService(inventoryStore, _assetService, dailyResetService);
            _dungeonHandler = new DungeonHandler(
                _assetService,
                reviveCoinService,
                characterRepository,
                sqliteSelectCharacterDataSource,
                rentalTimeProvider);
            _staminaHandler = new StaminaHandler(_assetService);
            _settingsHandler = new SettingsHandler();
            _ceraShopHandler = new CeraShopHandler(inventoryStore, sqliteSelectCharacterDataSource, _inventoryRefreshSender);
            _skillHandler = new SkillHandler(characterRepository, inventoryStore, _inventoryRefreshSender);
            _luckyStarHandler = new LuckyStarHandler(_assetService, sqliteSelectCharacterDataSource, rentalTimeProvider);
            _rentalHandler = new RentalHandler(_assetService, inventoryStore, sqliteSelectCharacterDataSource, rentalTimeProvider);
            _mailboxHandler = new MailboxHandler();
            var collectBoxProgressRepository = new Game.Inventory.CollectBoxProgressRepository(databasePath, schemaFilePath);
            _collectionBoxHandler = new CollectionBoxHandler(inventoryStore, collectBoxProgressRepository);
            _shopCoinEventHandler = new ShopCoinEventHandler(reviveCoinService, _inventoryRefreshSender);
            _mercenaryHandler = new MercenaryHandler(characterRepository, getUserInfoTemplate);

            _cmdDispatch = new Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>>();
            RegisterLoginHandlers(_cmdDispatch);
            RegisterCharacterHandlers(_cmdDispatch);
            RegisterInventoryHandlers(_cmdDispatch);
            RegisterPetHandlers(_cmdDispatch);
            RegisterSortItemLockHandlers(_cmdDispatch);
            RegisterEquipmentItemLockHandlers(_cmdDispatch);
            RegisterEquipmentSocketHandlers(_cmdDispatch);
            RegisterEquipmentEmblemHandlers(_cmdDispatch);
            RegisterAvatarSocketHandlers(_cmdDispatch);
            RegisterAvatarEmblemHandlers(_cmdDispatch);
            RegisterDungeonHandlers(_cmdDispatch);
            RegisterSkillHandlers(_cmdDispatch);
            RegisterTownHandlers(_cmdDispatch);
            RegisterSettingsHandlers(_cmdDispatch);
            RegisterQuestHandlers(_cmdDispatch);
            RegisterMailboxHandlers(_cmdDispatch);
            RegisterCollectionBoxHandlers(_cmdDispatch);
            RegisterMercenaryHandlers(_cmdDispatch);
            RegisterMiscHandlers(_cmdDispatch);
            _cmdDispatch[0x00CF] = _shopCoinEventHandler.HandleShopCoinEvent;   // 207 SHOP_COIN_EVENT 每日免费复活币
        }

        public override async Task OnClientConnected(EnhancedClientSession session)
        {
            FileLogger.Log($"[{ProtocolName}] Admin client connected: {session.SessionId}");
            await _loginHandler.Handle_ClientFirstConnected(session);
        }

        public override Task OnClientDisconnected(EnhancedClientSession session)
        {
            FileLogger.Log($"[{ProtocolName}] Admin client disconnected: {session.SessionId}");
            Handlers.Dungeon.DungeonRunLifecycle.EndRunOnTeardown(session, "disconnect");
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
                    await _inventoryRefreshSender.SendAllSortItemLockRefresh(s);
                    await _inventoryRefreshSender.SendAllEquipmentItemLockListRefresh(s);
                }
            };
            d[0x0005] = _characterSelectHandler.Handle_ENUM_CMDPACKET_CREATE_CHARACTER;
            d[0x0006] = _characterSelectHandler.Handle_ENUM_CMDPACKET_DELETE_CHARACTER;
            d[0x0007] = _characterSelectHandler.Handle_ENUM_CMDPACKET_RETURN_SELECT_CHARACTER;
            d[0x0008] = _characterSelectHandler.Handle_ENUM_CMDPACKET_GET_USERINFO;
            d[0x0009] = _staminaHandler.Handle_ENUM_CMDPACKET_RECOVER_STAMINA;
            d[0x02B5] = _characterSelectHandler.Handle_ENUM_CMDPACKET_CHECK_DOUBLE_CHARACTER_NAME;
        }

        private void RegisterInventoryHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0012] = _inventoryHandler.Handle_ENUM_CMDPACKET_DELETE_ITEM;       //18
            d[0x0013] = _inventoryHandler.Handle_ENUM_CMDPACKET_MOVE_ITEMSPACE;    //19
            d[0x0014] = _inventoryHandler.Handle_ENUM_CMDPACKET_SORT_ITEM;         //20
            d[0x0015] = _inventoryHandler.Handle_ENUM_CMDPACKET_BUY_ITEM;          //21
            d[0x0016] = _inventoryHandler.Handle_ENUM_CMDPACKET_SELL_ITEM;         //22
            d[0x0017] = _inventoryHandler.Handle_ENUM_CMDPACKET_REPAIR_EQUIPMENT;  //23 装备修理
            d[0x001A] = _inventoryHandler.Handle_ENUM_CMDPACKET_DISJOINT_ITEM;     //26 系统分解
            d[0x002C] = _inventoryHandler.Handle_ENUM_CMDPACKET_USE_STACKABLE;
            d[0x00D0] = _inventoryHandler.Handle_OPEN_MAGIC_BOX_SINGLE;
            d[0x0050] = _inventoryHandler.Handle_ENUM_CMDPACKET_UPGRADE_ITEM;      //80
            d[0x00A0] = _inventoryHandler.Handle_OPEN_SELECTABLE_PACKAGE;
            d[0x0110] = _inventoryHandler.Handle_ENUM_CMDPACKET_ENCHANT_BY_BEAD;   //272
            d[0x0191] = _inventoryHandler.Handle_UNSEAL_RANDOM_OPTION;             //401
            d[0x019C] = _inventoryHandler.Handle_TITLE_BOOK;                       //412
            d[0x01B6] = _inventoryHandler.Handle_CHANGE_RANDOM_OPTION;             //438
            d[0x019D] = _inventoryHandler.Handle_TITLE_BOOK;                       //413
            d[0x0207] = _inventoryHandler.Handle_OPEN_AVATAR_PACKAGE;
            d[0x0218] = _inventoryHandler.Handle_USE_BOOSTER_ITEM;
            d[0x0239] = _inventoryHandler.Handle_SET_CLONE_TITLE;                  //569
            d[0x03F3] = _inventoryHandler.Handle_OPEN_MAGIC_BOX;
            d[0x0063] = _inventoryHandler.Handle_COMPOUND_AVATAR;                  //99 合并装扮(时装合成)
            d[0x03EA] = _inventoryHandler.Handle_COMPOUND_AVATAR_SET;              //1002 8件高级装扮100%合成稀有装扮(克隆装扮合成器)
            d[0x0342] = _inventoryHandler.Handle_ADD_EQUIPMENT_EFFECT;             //834 武器特效符文添加
            d[0x0131] = _inventoryHandler.Handle_CREATE_ACCOUNT_CARGO;               //305 开通金库
            d[0x0132] = _inventoryHandler.Handle_UPGRADE_ACCOUNT_CARGO;             //306 扩容金库
            d[0x0133] = _inventoryHandler.Handle_DEPOSIT_MONEY;                    //307 金库存金币
            d[0x0134] = _inventoryHandler.Handle_WITHDRAW_MONEY;                   //308 金库取金币
            d[0x0198] = _inventoryHandler.Handle_UPGRADE_CARGO;                    //408 扩容个人仓库
        }

        private void RegisterPetHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x002C] = async (s, h, b) =>
            {
                if (await _petCreatureHandler.TryHandleUseStackable(s, h, b))
                    return;

                await _inventoryHandler.Handle_ENUM_CMDPACKET_USE_STACKABLE(s, h, b);
            };
            d[0x0064] = _petCreatureHandler.HandleRenameCreature;
            d[0x0066] = _petCreatureHandler.HandleHatchCreatureEgg;
            d[0x00AD] = _petCreatureHandler.HandleHatchCreatureEgg;
            d[0x00AE] = _petCreatureHandler.HandleRequestHatchedCreature;
        }

        private void RegisterSortItemLockHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x02CA] = _inventoryHandler.Handle_ENUM_CMDPACKET_TOGGLE_SORT_ITEM_LOCK;
            d[0x02CB] = _inventoryHandler.Handle_ENUM_CMDPACKET_UNLOCK_SORT_ITEM_LOCK;
        }

        private void RegisterEquipmentItemLockHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x010B] = _inventoryHandler.Handle_ENUM_CMDPACKET_REQUEST_ITEM_LOCK;
            d[0x010C] = _inventoryHandler.Handle_ENUM_CMDPACKET_REQUEST_ITEM_UNLOCK;
            d[0x010D] = _inventoryHandler.Handle_ENUM_CMDPACKET_REQUEST_ITEM_UNLOCK_CANCEL;
        }

        private void RegisterEquipmentSocketHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x031D] = _inventoryHandler.Handle_EQUIPMENT_SOCKET_OPEN;
        }

        private void RegisterEquipmentEmblemHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x031C] = _inventoryHandler.Handle_EQUIPMENT_EMBLEM_ATTACH;
        }

        private void RegisterAvatarSocketHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x00CE] = _inventoryHandler.Handle_AVATAR_SOCKET_OPEN;
        }

        private void RegisterAvatarEmblemHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x00C9] = _inventoryHandler.Handle_AVATAR_EMBLEM_ATTACH;
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
            d[0x0312] = PremiumQueryHandler.Handle_PREMIUM_SERVICE;                //786
            d[0x03B6] = _dungeonHandler.Handle_ENUM_CMDPACKET_GORGEOUS_CHALLENGE_TOGGLE;
            d[0x009F] = _dungeonHandler.Handle_ENUM_CMDPACKET_DEATH_TOWER_STAGE_CMD; // 159
        }

        private void RegisterSkillHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x001C] = _skillHandler.Handle_CHANGE_SKILLSLOT;                     //28
            d[0x001D] = _skillHandler.Handle_BUY_SKILL;                            //29
            d[0x0104] = _skillHandler.Handle_CHANGE_ANOTHER_SKILL_TREE;            //260
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
            d[0x01C0] = (s, h, b) => { _settingsHandler.Handle_SAVE_CHARACTER_OPTION(s, h, b); return Task.CompletedTask; };
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

        private void RegisterCollectionBoxHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0388] = _collectionBoxHandler.HandleQueryCollectionBox;
            d[0x0389] = _collectionBoxHandler.HandleInsertCollectBoxItem;
            d[0x038A] = _collectionBoxHandler.HandleRemoveCollectBoxItem;
        }

        private void RegisterMercenaryHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            // 只处理支援兵弹窗使用的角色信息子类型 5；普通角色信息仍走原有 0x0008。
            d[0x0002] = _mercenaryHandler.HandleUserInfoSubtypeRequest;
            d[0x01E5] = _mercenaryHandler.HandleMercenaryRequest;                  //485 支援兵技能列表
            d[0x01E8] = _mercenaryHandler.HandleMercenaryRequest;                  //488 支援兵选择
        }

        private void RegisterMiscHandlers(Dictionary<ushort, Func<EnhancedClientSession, GamePacketHeader, byte[], Task>> d)
        {
            d[0x0003] = (s, h, b) =>
                s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0003, CommonPacketBodyBuilder.BuildSuccessAck()));
            d[0x0040] = _ceraShopHandler.HandleCeraShopPurchase;                   //64
            d[0x01A1] = _inventoryHandler.Handle_ACHIEVEMENT_TRIGGER;              //417
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
