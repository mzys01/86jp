using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Settings;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.SelectCharacter
{
    public sealed class SqliteSelectCharacterDataSource : ISelectCharacterDataSource
    {
        private readonly SqliteInventoryStore _inventoryStore;
        private readonly SqliteCharacterProgressRepository _initDataRepository;
        private readonly SqliteUserInfoBlobRepository _userInfoBlobRepository;
        private readonly SqliteCharacterStateRepository _initFlagsRepository;
        private readonly PacketSequenceRepository _packetSequenceRepository;
        private readonly ICharacterRepository _characterRepository;
        private readonly AccountSettingsRepository _accountSettingsRepository;

        public SqliteSelectCharacterDataSource(string databasePath, string schemaFilePath, ICharacterRepository characterRepository)
        {
            _inventoryStore = new SqliteInventoryStore(databasePath, schemaFilePath);
            _initDataRepository = new SqliteCharacterProgressRepository(databasePath, schemaFilePath);
            _userInfoBlobRepository = new SqliteUserInfoBlobRepository(databasePath, schemaFilePath);
            _initFlagsRepository = new SqliteCharacterStateRepository(databasePath, schemaFilePath);
            _packetSequenceRepository = new PacketSequenceRepository(databasePath, schemaFilePath);
            _characterRepository = characterRepository;
            _accountSettingsRepository = new AccountSettingsRepository(databasePath, schemaFilePath);
        }

        public int GetSeedCharacterId()
        {
            int dbSeedId = _userInfoBlobRepository.LoadSeedCharacterId();
            return dbSeedId > 0 ? dbSeedId : 1000;
        }

        public SelectCharacterDataSnapshot Load(int characterId, int accountId)
        {
            CharacterItemListSnapshot itemList;
            using (_inventoryStore.BeginScope(characterId, accountId))
                itemList = _inventoryStore.LoadCharacterItemListSnapshot();

            var initSnapshot = new SelectCharacterInitializationSnapshot();

            if (_initDataRepository.HasSkills(characterId))
                initSnapshot.SkillInfo = _initDataRepository.LoadSkills(characterId);
            if (_initDataRepository.HasCreatures(characterId))
                initSnapshot.CreatureItemList = _initDataRepository.LoadCreatures(characterId);

            _initFlagsRepository.LoadAll(characterId, initSnapshot);

            
            {
                var rec = _characterRepository?.GetById(characterId);
                if (rec != null && initSnapshot.SkillInfo != null && initSnapshot.SkillInfo.Pages.Count > 0)
                {
                    var sp = Skills.SkillPointCalculator.Calculate(
                        rec.Job, rec.Level, rec.BonusSp, rec.BonusTp, initSnapshot.SkillInfo);
                    initSnapshot.SkillInfo.Pages[0].HeaderValue = (ushort)sp.RemainingSp;
                    if (initSnapshot.SkillInfo.Pages.Count > 1)
                        initSnapshot.SkillInfo.Pages[1].HeaderValue = (ushort)sp.RemainingSp;
                    initSnapshot.SkillInfo.Tail1 = (ushort)sp.TotalSp;
                }
            }

            
            var acctSettings = _accountSettingsRepository.Load(accountId);
            initSnapshot.MainGameOptionBlob = acctSettings?.MainGameOption ?? Settings.AccountSettings.DefaultMainGameOption;
            initSnapshot.QuickchatBank0 = acctSettings?.QuickchatBank0;
            initSnapshot.QuickchatBank1 = acctSettings?.QuickchatBank1;
            initSnapshot.HotkeyConfigSlots.Clear();
            var hkSlots = acctSettings?.HotkeySlots ?? Settings.AccountSettings.DefaultHotkeySlots;
            if (hkSlots != null && hkSlots.Length >= 2)
            {
                initSnapshot.HotkeyKeyType = acctSettings?.HotkeyKeyType ?? 0;
                for (int i = 0; i + 1 < hkSlots.Length; i += 2)
                    initSnapshot.HotkeyConfigSlots.Add(BitConverter.ToUInt16(hkSlots, i));
            }


            LoadInitFieldsFromPacketTemplates(characterId, initSnapshot);

            initSnapshot.ServerEventPhaseBitmap = _initFlagsRepository.LoadServerEventPhaseBitmap();

            var premiumBody = _initFlagsRepository.LoadGlobalRawPacket(0x10312);
            if (premiumBody != null && premiumBody.Length >= 3)
            {
                initSnapshot.PremiumServiceType = BitConverter.ToUInt16(premiumBody, 1);
                var dataLen = premiumBody.Length - 3;
                if (dataLen > 0)
                {
                    initSnapshot.PremiumServiceData = new byte[dataLen];
                    Buffer.BlockCopy(premiumBody, 3, initSnapshot.PremiumServiceData, 0, dataLen);
                }
            }

            
            
            
            CharacterRecord characterRecord = _characterRepository?.GetById(characterId);

            
            var subtype1Repo = new CharacterData.SqliteSubtype1Repository(
                Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);
            if (subtype1Repo.HasData(characterId))
                initSnapshot.UserInfoAddition = subtype1Repo.Load(characterId);

            
            if (characterRecord != null)
            {
                var tailSnap = new CharacterData.SqliteSubtype0FieldsRepository(
                    Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath).Load(characterId);
                if (tailSnap != null)
                    characterRecord.Subtype0Tail = tailSnap;

                
                if (characterRecord.Subtype0Tail != null && initSnapshot.UserInfoAddition != null)
                {
                    characterRecord.Subtype0Tail.ProgressA = initSnapshot.UserInfoAddition.Progress1;
                    characterRecord.Subtype0Tail.ProgressB = initSnapshot.UserInfoAddition.Progress2;
                    characterRecord.Subtype0Tail.SkillTreeIndex = initSnapshot.UserInfoAddition.SkillTreeIndex;
                }
            }

            var packetTemplates = _packetSequenceRepository.Load(characterId);

            return new SelectCharacterDataSnapshot
            {
                PacketTemplates = packetTemplates,
                ItemListSnapshot = itemList,
                InitializationSnapshot = initSnapshot,
                CharacterRecord = characterRecord,
            };
        }

        public CharacterItemListSnapshot LoadItemListSnapshot(int characterId, int accountId)
        {
            using (_inventoryStore.BeginScope(characterId, accountId))
                return _inventoryStore.LoadCharacterItemListSnapshot();
        }

        public bool TryMoveItem(int characterId, int accountId, InventoryMoveRequest request, out InventoryMoveResult result)
        {
            using (_inventoryStore.BeginScope(characterId, accountId))
                return _inventoryStore.TryMoveItem(request, out result);
        }

        public bool TryDeleteItem(int characterId, int accountId, InventoryListType listType, short slotIndex, short deleteCount, out InventoryMutationResult result)
        {
            using (_inventoryStore.BeginScope(characterId, accountId))
                return _inventoryStore.TryDeleteItem(listType, slotIndex, deleteCount, out result);
        }

        public bool TryBuyItem(int characterId, int accountId, int itemTemplateId, int buyCount, out InventoryMutationResult result)
        {
            using (_inventoryStore.BeginScope(characterId, accountId))
                return _inventoryStore.TryBuyItem(itemTemplateId, buyCount, out result);
        }

        public bool TrySellItem(int characterId, int accountId, InventoryListType listType, short slotIndex, short sellCount, out InventoryMutationResult result)
        {
            using (_inventoryStore.BeginScope(characterId, accountId))
                return _inventoryStore.TrySellItem(listType, slotIndex, sellCount, out result);
        }

        public bool TrySortItems(int characterId, int accountId, InventoryListType listType, byte category)
        {
            using (_inventoryStore.BeginScope(characterId, accountId))
                return _inventoryStore.TrySortItems(characterId, listType, category);
        }

        private void LoadFieldFromInitBody(int characterId, int notiType, Action<byte[]> parse)
        {
            var body = LoadInitBody(characterId, notiType, 0);
            if (body != null) parse(body);
        }

        private byte[] LoadInitBody(int characterId, int notiType, int occurrenceIndex)
        {
            using (var conn = new SqliteConnection(
                Infrastructure.SqliteDatabaseBootstrap.Initialize(
                    Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath)))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT body FROM character_init_bodies WHERE character_id=@cid AND noti_type=@nt AND occurrence_index=@oi", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@nt", notiType);
                    cmd.Parameters.AddWithValue("@oi", occurrenceIndex);
                    return cmd.ExecuteScalar() as byte[];
                }
            }
        }

        public void InitializeNewCharacter(int characterId, int accountId, byte job)
        {
            using (_inventoryStore.BeginScope(characterId, accountId))
                _inventoryStore.EnsureContainerState(characterId);

            var emptySnapshot = new SelectCharacterInitializationSnapshot();
            _initFlagsRepository.SeedFromSnapshot(characterId, emptySnapshot);

            var initialSkills = InitialCharacterSkills.Build(job);
            if (initialSkills != null)
                _initDataRepository.SaveSkills(characterId, initialSkills);

            var initialEquip = InitialCharacterEquipment.Get(job);
            if (initialEquip != null)
            {
                using (_inventoryStore.BeginScope(characterId, accountId))
                    _inventoryStore.SeedNewCharacterEquipment(initialEquip);
            }

            
            SeedNewCharacterStructuredData(characterId, job);
        }

        private void SeedNewCharacterStructuredData(int characterId, byte job)
        {
            var connStr = Infrastructure.SqliteDatabaseBootstrap.Initialize(
                Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                
                
                
                var stat = Game.Characters.CharacterStatComputer.BuildAdditionalInfo(job, 1);
                using (var cmd = new SqliteCommand(@"INSERT OR IGNORE INTO character_subtype1_fields(
                    character_id, stat_hp_max, stat_mp_max, stat_physical_attack, stat_physical_defense,
                    stat_magical_attack, stat_magical_defense, stat_fire_resistance, stat_water_resistance,
                    stat_dark_resistance, stat_light_resistance, stat_inventory_limit,
                    stat_hp_regen_speed, stat_mp_regen_speed, stat_move_speed, stat_attack_speed,
                    stat_cast_speed, stat_hit_recovery, stat_jump_power, stat_weight, stat_level,
                    name_tag_item_id, name_tag_expire_time, skill_tree_index, equipped_creature_level, equip_list_trailing,
                    manage_level, flag_byte, guild_power_war, server_timestamp, quest_shop_count,
                    progress1, progress2
                ) VALUES(
                    @cid, @hp, @mp, @pa, @pd, @ma, @md, @fr, @wr, @dr, @lr, @il,
                    @hr, @mr, @ms, @as2, @cs, @hrc, @jp, @wt, 100,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                )", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    
                    int o = 0;
                    cmd.Parameters.AddWithValue("@hp", (long)System.BitConverter.ToUInt32(stat, o)); o += 4;
                    cmd.Parameters.AddWithValue("@mp", (long)System.BitConverter.ToUInt32(stat, o)); o += 4;
                    cmd.Parameters.AddWithValue("@pa", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@pd", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@ma", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@md", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@fr", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@wr", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@dr", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@lr", (int)System.BitConverter.ToInt16(stat, o)); o += 2;
                    o += 34; 
                    cmd.Parameters.AddWithValue("@il", (long)System.BitConverter.ToUInt32(stat, o)); o += 4;
                    cmd.Parameters.AddWithValue("@hr", (int)System.BitConverter.ToUInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@mr", (int)System.BitConverter.ToUInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@ms", (long)System.BitConverter.ToUInt32(stat, o)); o += 4;
                    cmd.Parameters.AddWithValue("@as2", (int)System.BitConverter.ToUInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@cs", (int)System.BitConverter.ToUInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@hrc", (int)System.BitConverter.ToUInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@jp", (int)System.BitConverter.ToUInt16(stat, o)); o += 2;
                    cmd.Parameters.AddWithValue("@wt", (long)System.BitConverter.ToUInt32(stat, o));
                    cmd.ExecuteNonQuery();
                }

                
                var defaults = new (int noti, byte[] body)[]
                {
                    (0x0035, new byte[13]),                     
                    (0x0077, new byte[] { 0x00 }),              
                    (0x0111, new byte[8]),                      
                    (0x019F, new byte[] { 0x00, 0x00 }),        
                    (0x03D8, new byte[204]),                    
                };
                foreach (var d in defaults)
                {
                    using (var cmd = new SqliteCommand(
                        "INSERT OR IGNORE INTO character_init_bodies(character_id, noti_type, occurrence_index, body) VALUES(@cid, @nt, 0, @b)", conn))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@nt", d.noti);
                        cmd.Parameters.AddWithValue("@b", d.body);
                        cmd.ExecuteNonQuery();
                    }
                }
                
                using (var cmd = new SqliteCommand(
                    "INSERT OR IGNORE INTO character_init_bodies(character_id, noti_type, occurrence_index, body) VALUES(@cid, @nt, 1, @b)", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@nt", 0x0077);
                    cmd.Parameters.AddWithValue("@b", new byte[] { 0x00 });
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private void LoadInitFieldsFromPacketTemplates(int characterId, SelectCharacterInitializationSnapshot snap)
        {
            var repo = _packetSequenceRepository;

            LoadFieldFromInitBody(characterId, 0x015F, body => {
                snap.SkillPointSlots.Clear();
                if (body == null || body.Length < 1) return;
                int count = body[0]; int off = 1;
                for (int i = 0; i < count && off + 3 <= body.Length; i++)
                {
                    snap.SkillPointSlots.Add(new SkillPointSlotEntrySnapshot
                    { SkillType = body[off], Points = BitConverter.ToUInt16(body, off + 1) });
                    off += 3;
                }
            });

            LoadFieldFromInitBody(characterId, 0x0381, body => {
                if (body == null || body.Length < 8) return;
                snap.CollectionBox.BoxType = body[0];
                snap.CollectionBox.DisplayMode = body[1];
                snap.CollectionBox.CollectionId = BitConverter.ToUInt32(body, 2);
                snap.CollectionBox.StatusFlags = body[6];
                int count = body[7]; int off = 8;
                snap.CollectionBox.Items.Clear();
                for (int i = 0; i < count && off + 8 <= body.Length; i++)
                {
                    snap.CollectionBox.Items.Add(new CollectionBoxItemSnapshot
                    { ItemId = BitConverter.ToUInt32(body, off), Count = BitConverter.ToUInt32(body, off + 4) });
                    off += 8;
                }
            });

            LoadFieldFromInitBody(characterId, 0x0357, body => {
                if (body == null || body.Length < 8) return;
                snap.RentalInfo.RentalId = BitConverter.ToUInt32(body, 0);
                var count = BitConverter.ToUInt32(body, 4);
                int off = 8;
                snap.RentalInfo.Items.Clear();
                for (uint i = 0; i < count && off + 8 <= body.Length; i++)
                {
                    snap.RentalInfo.Items.Add(new RentalItemSnapshot
                    { ItemId = BitConverter.ToUInt32(body, off), ExpireTime = BitConverter.ToUInt32(body, off + 4) });
                    off += 8;
                }
            });

            
            {
                var lbBody = LoadInitBody(characterId, 0x03D8, 0);
                if (lbBody != null) snap.LotteryBufferBlob = lbBody;
            }

            LoadFieldFromInitBody(characterId, 0x019D, body => {
                if (body != null && body.Length >= 2) { snap.GageType = body[0]; snap.GageValue = body[1]; }
            });

            
        }
    }
}
