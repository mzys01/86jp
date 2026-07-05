using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Settings;
using DfoServer.Game.TitleBook;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.SelectCharacter
{
    public sealed class SqliteSelectCharacterDataSource : ISelectCharacterDataSource
    {
        private readonly IInventoryStore _inventoryStore;
        private readonly IAssetService _assetService;
        private readonly SqliteCharacterProgressRepository _initDataRepository;
        private readonly SqliteUserInfoBlobRepository _userInfoBlobRepository;
        private readonly ICharacterStateRepository _initFlagsRepository;
        private readonly PacketSequenceRepository _packetSequenceRepository;
        private readonly ICharacterRepository _characterRepository;
        private readonly AccountSettingsRepository _accountSettingsRepository;
        private readonly CharacterTitleBookRepository _titleBookRepository;
        private readonly DailyReset.DailyResetService _dailyResetService;
        private readonly TitleBookMutationService _titleBookMutationService;
        private readonly CharacterAchievementProgressRepository _achievementProgressRepository;
        private readonly string _connectionString;
        private readonly string _databasePath;
        private readonly string _schemaFilePath;

        public SqliteSelectCharacterDataSource(string databasePath, string schemaFilePath, ICharacterRepository characterRepository, IAssetService assetService = null, IInventoryStore inventoryStore = null)
        {
            _databasePath = databasePath;
            _schemaFilePath = schemaFilePath;
            _connectionString = Infrastructure.SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
            _inventoryStore = inventoryStore ?? new SqliteInventoryStore(databasePath, schemaFilePath);
            _assetService = assetService;
            _initDataRepository = new SqliteCharacterProgressRepository(databasePath, schemaFilePath);
            _userInfoBlobRepository = new SqliteUserInfoBlobRepository(databasePath, schemaFilePath);
            _initFlagsRepository = new SqliteCharacterStateRepository(databasePath, schemaFilePath);
            _packetSequenceRepository = new PacketSequenceRepository(databasePath, schemaFilePath);
            _characterRepository = characterRepository;
            _accountSettingsRepository = new AccountSettingsRepository(databasePath, schemaFilePath);
            _titleBookRepository = new CharacterTitleBookRepository(_connectionString);
            _dailyResetService = new DailyReset.DailyResetService(databasePath, schemaFilePath);
            _titleBookMutationService = new TitleBookMutationService(_connectionString);
            _achievementProgressRepository = new CharacterAchievementProgressRepository(_connectionString);
        }

        public int GetSeedCharacterId()
        {
            int dbSeedId = _userInfoBlobRepository.LoadSeedCharacterId();
            return dbSeedId > 0 ? dbSeedId : 1000;
        }

        public CreatureItemListSnapshot LoadCreatureItemListSnapshot(int characterId)
        {
            return _initDataRepository.LoadCreatures(characterId);
        }

        public List<TitleBookCategorySnapshot> LoadTitleBookSnapshots(int characterId)
        {
            return _titleBookRepository.LoadSnapshots(characterId);
        }

        public TitleBookCategorySnapshot LoadTitleBookSnapshot(int characterId, int category)
        {
            return _titleBookRepository.LoadSnapshot(characterId, category);
        }

        public bool TryPutTitleBook(int characterId, int accountId, InventoryListType sourceList, short sourceSlot, int itemId, int category, int bookIndex, out TitleBookMutationResult result)
        {
            result = _titleBookMutationService.PutTitle(characterId, accountId, sourceList, sourceSlot, itemId, category, bookIndex);
            return result.Success;
        }

        public bool TryGetTitleBook(int characterId, int accountId, InventoryListType targetList, short targetSlot, int itemId, int category, int bookIndex, out TitleBookMutationResult result)
        {
            result = _titleBookMutationService.GetTitle(characterId, accountId, targetList, targetSlot, itemId, category, bookIndex);
            return result.Success;
        }

        public bool TryTriggerAchievement(int characterId, int questId, ushort delta1, ushort delta2, ushort delta3, out AchievementTriggerResult result)
        {
            result = _titleBookMutationService.TriggerAchievement(characterId, questId, delta1, delta2, delta3);
            return result.Success;
        }

        public SelectCharacterDataSnapshot Load(int characterId, int accountId)
        {
            CharacterItemListSnapshot itemList;
            _inventoryStore.DeleteExpiredRentalEquipment(characterId, accountId);
            itemList = _inventoryStore.LoadCharacterItemListSnapshot(characterId, accountId);

            var initSnapshot = new SelectCharacterInitializationSnapshot();

            if (_initDataRepository.HasSkills(characterId))
                initSnapshot.SkillInfo = _initDataRepository.LoadSkills(characterId);
            if (_initDataRepository.HasCreatures(characterId))
                initSnapshot.CreatureItemList = _initDataRepository.LoadCreatures(characterId);

            _initFlagsRepository.LoadAll(characterId, initSnapshot);
            initSnapshot.TitleBookCategories.Clear();
            for (var category = 0; category < TitleBookStaticDataProvider.CategoryCapacities.Count; category++)
                initSnapshot.TitleBookCategories.Add(
                    _titleBookRepository.LoadSnapshot(characterId, category));
            MergeAchievementProgress(initSnapshot, _achievementProgressRepository.LoadSnapshot(characterId));

            
            {
                var rec = _characterRepository?.GetById(characterId);
                if (rec != null && initSnapshot.SkillInfo != null && initSnapshot.SkillInfo.Pages.Count > 0)
                {
                    var synced = Skills.SkillStateService.LoadAndSync(
                        _initDataRepository,
                        characterId,
                        rec.Job,
                        rec.Level,
                        rec.BonusSp,
                        rec.BonusTp,
                        persist: false);
                    initSnapshot.SkillInfo = synced.Skills;
                }
            }

            
            LoadInitFieldsFromPacketTemplates(characterId, initSnapshot);

            if (_assetService != null)
            {
                using (var assetScope = _assetService.OpenScope(characterId, accountId))
                {
                    var wallet = _assetService.LoadWallet(assetScope);
                    ApplyWallet(initSnapshot, wallet);
                }
            }
            else
            {
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    var wallet = CurrencyService.LoadWallet(conn, null, characterId);
                    ApplyWallet(initSnapshot, wallet);
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


            initSnapshot.ServerEventPhaseBitmap = _initFlagsRepository.LoadServerEventPhaseBitmap();
            initSnapshot.ShopCoinEventFlag = _dailyResetService.IsClaimed(characterId, ReviveCoin.ReviveCoinService.DailyClaimKey) ? (byte)1 : (byte)0;

            initSnapshot.PremiumServiceType = 1;
            initSnapshot.PremiumServiceData = Premium.PremiumService.BuildPremiumServiceData(
                _connectionString, accountId);
            LoadAccountPremiums(accountId, initSnapshot);

            
            
            
            CharacterRecord characterRecord = _characterRepository?.GetById(characterId);
            if (characterRecord != null)
            {
                // 选角初始化 USERINFO 同样必须使用当前穿戴栏重建外观，避免 characters.appearance_blob在新建角色或换装后滞留为空/旧值，导致城镇模型和选人/副本显示不一致。
                characterRecord.Appearance = Game.Appearance.AppearanceService.LoadAppearanceFromEquipEntries(characterId);
            }

            
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
            EnsurePremiumServicePacket(packetTemplates, initSnapshot);

            return new SelectCharacterDataSnapshot
            {
                PacketTemplates = packetTemplates,
                ItemListSnapshot = itemList,
                InitializationSnapshot = initSnapshot,
                CharacterRecord = characterRecord,
            };
        }

        private static void EnsurePremiumServicePacket(
            List<SelectCharacterPacketTemplate> packetTemplates,
            SelectCharacterInitializationSnapshot initSnapshot)
        {
            if (packetTemplates == null || packetTemplates.Count == 0 || initSnapshot?.PremiumServiceData == null)
                return;

            for (var i = 0; i < packetTemplates.Count; i++)
            {
                var template = packetTemplates[i];
                if (template.Command == 0x01 && template.Type == 0x0312)
                    return;
            }

            var insertIndex = packetTemplates.Count;
            for (var i = 0; i < packetTemplates.Count; i++)
            {
                var template = packetTemplates[i];
                if (template.Command == 0x00 && template.Type == 0x03D8)
                {
                    insertIndex = i;
                    break;
                }

                if (template.Command == 0x00 && template.Type == 0x0300)
                    insertIndex = i + 1;
            }

            packetTemplates.Insert(insertIndex, new SelectCharacterPacketTemplate
            {
                Kind = SelectCharacterPacketTemplateKind.Raw,
                Command = 0x01,
                Type = 0x0312,
                OccurrenceIndex = 0,
            });
        }

        private static void MergeAchievementProgress(
            SelectCharacterInitializationSnapshot initSnapshot,
            AchievementCompleteSnapshot progress)
        {
            if (progress == null || progress.Entries.Count == 0)
                return;

            var merged = new Dictionary<int, AchievementCompleteEntrySnapshot>();
            foreach (var entry in initSnapshot.AchievementComplete.Entries)
                merged[entry.AchievementId] = entry;
            foreach (var entry in progress.Entries)
                merged[entry.AchievementId] = entry;

            initSnapshot.AchievementComplete = new AchievementCompleteSnapshot();
            initSnapshot.AchievementComplete.Entries.AddRange(merged.Values);
        }

        private static void ApplyWallet(SelectCharacterInitializationSnapshot initSnapshot, WalletSnapshot wallet)
        {
            if (initSnapshot == null || wallet == null)
                return;

            initSnapshot.AckCera = wallet.Cera;
            initSnapshot.AckTokenCera = wallet.TokenCera;
            initSnapshot.AckHappyTokenCera = wallet.HappyTokenCera;
            initSnapshot.LuckyStar = wallet.LuckyStar;
        }

        private void LoadAccountPremiums(int accountId, SelectCharacterInitializationSnapshot initSnapshot)
        {
            initSnapshot.AckPremiums.Clear();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long devilContractMaxExpire = 0;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT premium_type, end_time FROM account_premiums WHERE account_id=@aid AND end_time>@now ORDER BY premium_type;";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.Parameters.AddWithValue("@now", now);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var pt = reader.GetInt32(0);
                            var endTime = reader.GetInt64(1);

                            if (Premium.DevilContractCatalog.IsDevilContractSlotType(pt))
                            {
                                if (endTime > devilContractMaxExpire)
                                    devilContractMaxExpire = endTime;
                                continue;
                            }

                            var remaining = Math.Max(0, endTime - now);
                            initSnapshot.AckPremiums.Add(new AckPremiumEntrySnapshot
                            {
                                PremiumType = (byte)pt,
                                EndTime = BitConverter.GetBytes(remaining),
                            });
                        }
                    }
                }
            }

            if (devilContractMaxExpire > now)
            {
                initSnapshot.AckPremiums.Add(new AckPremiumEntrySnapshot
                {
                    PremiumType = (byte)Premium.DevilContractCatalog.ActivationPremiumType,
                    EndTime = BitConverter.GetBytes(devilContractMaxExpire - now),
                });
            }
        }
        public byte[] LoadCharacterInitBody(int characterId, ushort notiType, int occurrenceIndex = 0)
            => LoadInitBody(characterId, notiType, occurrenceIndex);

        public bool TrySaveCrystalContractSelection(int characterId, byte[] body)
        {
            if (characterId <= 0 || body == null || body.Length < 2)
                return false;

            var storage = new byte[] { body[0], body[1] };
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    @"INSERT INTO character_init_bodies (character_id, noti_type, occurrence_index, body)
                      VALUES (@cid, @nt, 0, @body)
                      ON CONFLICT(character_id, noti_type, occurrence_index)
                      DO UPDATE SET body=@body", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@nt", 0x0300);
                    cmd.Parameters.AddWithValue("@body", storage);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public byte[] LoadAccountMainOption(int accountId)
            => _accountSettingsRepository.Load(accountId)?.MainGameOption;


        public void SaveRentalInfo(SqliteConnection connection, SqliteTransaction transaction, int characterId, RentalInfoSnapshot rental)
        {
            if (characterId <= 0 || connection == null || transaction == null || rental == null)
                return;

            var storage = RentalInfoSnapshot.BuildStorageBody(rental);
            using (var cmd = new SqliteCommand(
                @"INSERT INTO character_init_bodies (character_id, noti_type, occurrence_index, body)
                  VALUES (@cid, @nt, 0, @body)
                  ON CONFLICT(character_id, noti_type, occurrence_index)
                  DO UPDATE SET body=@body", connection, transaction))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@nt", 0x0357);
                cmd.Parameters.AddWithValue("@body", storage);
                cmd.ExecuteNonQuery();
            }
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
                    _databasePath, _schemaFilePath)))
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
            _inventoryStore.EnsureContainerState(characterId, accountId);

            var emptySnapshot = new SelectCharacterInitializationSnapshot();
            _initFlagsRepository.SeedFromSnapshot(characterId, emptySnapshot);

            var initialSkills = InitialCharacterSkills.Build(job);
            if (initialSkills != null)
            {
                var points = Skills.SkillStateService.ResolvePointState(
                    initialSkills, null, job, 1, 0, 0);
                points.RemainingSp = points.TotalSp;
                points.RemainingTp = points.TotalTp;
                Skills.SkillStateService.Persist(_initDataRepository, characterId, initialSkills, points);
            }

            var initialEquip = InitialCharacterEquipment.Get(job);
            if (initialEquip != null)
            {
                _inventoryStore.SeedNewCharacterEquipment(characterId, accountId, initialEquip);
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
                    (0x0300, new byte[] { 0x00, 0x00 }),
                    (0x0357, new byte[] { 0x7B, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }),
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
                RentalInfoSnapshot.ParseStorageBody(body, snap.RentalInfo);
            });

            LoadFieldFromInitBody(characterId, 0x0300, body => {
                if (body == null || body.Length < 2) return;
                snap.CubeType = body[0];
                snap.CubeGrade = body[1];
            });

            
            {
                var lbBody = LoadInitBody(characterId, 0x03D8, 0);
                if (lbBody != null) snap.LotteryBufferBlob = lbBody;
            }
        }
    }
}
