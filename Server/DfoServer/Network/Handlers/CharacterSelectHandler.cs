using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Names;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers;
using System;
using System.Text;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class CharacterSelectHandler
    {
        private readonly ISelectCharacterDataSource _selectCharacterDataSource;
        private readonly ICharacterRepository _characterRepository;
        private readonly GetUserInfoTemplate _getUserInfoTemplate;

        public string ProtocolName => "GameProtocol";

        public CharacterSelectHandler(
            ISelectCharacterDataSource selectCharacterDataSource,
            ICharacterRepository characterRepository,
            GetUserInfoTemplate getUserInfoTemplate)
        {
            _selectCharacterDataSource = selectCharacterDataSource ?? throw new ArgumentNullException(nameof(selectCharacterDataSource));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _getUserInfoTemplate = getUserInfoTemplate;
        }

        public async Task Handle_ENUM_CMDPACKET_SELECT_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            try
            {
                DungeonSharedServices.PersistPetCreatureSatiety(session, "select_character");
                DungeonSharedServices.PersistPetCreatureTownRecovery(session, "select_character");

                int slot = 0;
                if (body != null && body.Length >= 2)
                {
                    slot = BitConverter.ToUInt16(body, 0);
                }
                else
                {
                    FileLogger.Log($"[{ProtocolName}] Select character body too short ({body?.Length ?? 0}B), defaulting slot=0");
                }

                CharacterRecord record = null;
                if (session.Account != null)
                {
                    var list = _characterRepository.ListByAccount(session.Account.AccountId);
                    if (list.Count == 0)
                    {
                        FileLogger.Log($"[{ProtocolName}] Select character: account_id={session.Account.AccountId} has 0 characters, falling back to seed character_id={_selectCharacterDataSource.GetSeedCharacterId()}");
                    }
                    else
                    {
                        if (slot < 0 || slot >= list.Count)
                        {
                            FileLogger.Log($"[{ProtocolName}] Select character slot={slot} out of range (count={list.Count}), clamping to 0");
                            slot = 0;
                        }
                        record = list[slot];
                    }
                }
                if (record == null)
                {
                    record = _characterRepository.GetById(_selectCharacterDataSource.GetSeedCharacterId());
                }

                if (record != null)
                {
                    try
                    {
                        AppearanceService.RepairLegacyTitleAppearanceBlobIfNeeded(record.CharacterId);
                        record = _characterRepository.GetById(record.CharacterId) ?? record;
                        var tail = new Game.CharacterData.SqliteSubtype0FieldsRepository(
                            Infrastructure.ServerPaths.DatabasePath,
                            Infrastructure.ServerPaths.SchemaFilePath).Load(record.CharacterId);
                        var skillTreeIndex = new Game.CharacterData.SqliteSubtype1Repository(
                            Infrastructure.ServerPaths.DatabasePath,
                            Infrastructure.ServerPaths.SchemaFilePath).LoadSkillTreeIndex(record.CharacterId);
                        if (skillTreeIndex.HasValue)
                        {
                            tail = tail ?? new UserInfoMinimumTailSnapshot();
                            tail.SkillTreeIndex = skillTreeIndex.Value;
                        }
                        if (tail != null)
                            tail.PetCreatureAliveFlag = PetCreatureSatietyService.LoadEquippedCreatureAliveFlag(
                                Infrastructure.ServerPaths.DatabasePath,
                                Infrastructure.ServerPaths.SchemaFilePath,
                                record.CharacterId);
                        if (tail != null)
                            record.Subtype0Tail = tail;

                        // USERINFO subtype0/minimum is sensitive to the byte-stable appearance blob migrated from the
                        // old server. Pet body state lives in the subtype0 tail, so rebuild appearance only for fresh
                        // characters that do not have a stored blob yet.
                        if (record.Appearance == null || record.Appearance.Length == 0)
                            record.Appearance = AppearanceService.LoadAppearanceFromEquipEntries(record.CharacterId);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[{ProtocolName}] Select character subtype0 load failed: {ex.Message}");
                    }

                    session.Player.HydrateFrom(record);
                    _characterRepository.UpdatePosition(
                        session.Player.CharacterId,
                        session.Player.CurTownId,
                        session.Player.CurAreaId,
                        session.Player.CurPosX,
                        session.Player.CurPosY,
                        session.Player.CurDirection,
                        session.Player.CurAreaState);
                    DungeonSharedServices.BeginPetCreatureTownRecovery(session);
                    FileLogger.Log($"[{ProtocolName}] Select character hydrated session {session.SessionId} slot={slot} <- character_id={record.CharacterId} name={record.DisplayName} town={session.Player.CurTownId} area={session.Player.CurAreaId} pos=({session.Player.CurPosX},{session.Player.CurPosY})");
                }
                else
                {
                    FileLogger.Log($"[{ProtocolName}] Select character: no record resolved, keeping in-memory defaults");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] Select character DB load failed: {ex.Message}");
            }

            var ownerCharId = session.Player.CharacterId > 0 ? session.Player.CharacterId : _selectCharacterDataSource.GetSeedCharacterId();
            var ownerAcctId = session.Account?.AccountId ?? 1;

            foreach (var packet in SelectCharacterPacketBuilder.BuildPacketStream(_selectCharacterDataSource, ownerCharId, ownerAcctId))
                await session.SendPacketAsync(packet);

            var cloneTitle = AppearanceService.LoadCloneTitleItemId(ownerCharId);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0239,
                AppearanceService.BuildCloneTitleAckBody(cloneTitle, suppressMessage: 1)));
            FileLogger.Log($"[{ProtocolName}] SELECT_CHARACTER clone title restore: char={ownerCharId} cloneTitle=0x{cloneTitle:X8}");

            await SendEquippedPetCreatureNameNoti(session, ownerCharId);
        }

        private async Task SendEquippedPetCreatureNameNoti(EnhancedClientSession session, int characterId)
        {
            byte[] nameBytes;
            try
            {
                nameBytes = _selectCharacterDataSource.LoadEquippedPetCreatureNameBytes(characterId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] Select character pet rename load failed: {ex.Message}");
                return;
            }

            if (nameBytes == null || nameBytes.Length == 0)
                return;

            // 0x0065 is enough for the client to repaint the equipped creature custom name on login.
            var writer = new GamePacketWriter();
            writer.WriteUInt16(session?.Player?.UserId ?? 0);
            writer.WriteRawDstr(nameBytes);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0065, writer.ToArray()));
            FileLogger.Log($"[{ProtocolName}] Select character pet rename refresh: character_id={characterId} len={nameBytes.Length}");
        }

        public async Task Handle_ENUM_CMDPACKET_GET_USERINFO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            try
            {
                var accountId = session.Account?.AccountId ?? 1;
                var rosterBody = BuildCharacterListBody(accountId);
                byte routingByte = _getUserInfoTemplate != null ? _getUserInfoTemplate.Pkt0RoutingByte7 : (byte)0;
                await session.SendPacketAsync(BuildPacketWithRouting(0x00, 0x0002, rosterBody, routingByte));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0286, new byte[] { 0x00, 0x04 }));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01BA,
                    new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));
                FileLogger.Log($"[{ProtocolName}] GET_USERINFO: 动态 roster+646+442 (account={accountId})");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] GET_USERINFO EXCEPTION: {ex}");
            }
        }

        private static bool NameBytesEqual(byte[] a, byte[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static byte[] BuildPacketWithRouting(byte command, ushort type, byte[] body, byte routingByte7)
        {
            int totalLen = 15 + (body != null ? body.Length : 0);
            var packet = new byte[totalLen];
            packet[0] = command;
            Buffer.BlockCopy(BitConverter.GetBytes(type), 0, packet, 1, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(totalLen), 0, packet, 3, 4);
            packet[7] = routingByte7;
            if (body != null && body.Length > 0)
                Buffer.BlockCopy(body, 0, packet, 15, body.Length);
            return packet;
        }

        private async Task SendGetUserInfoResponse(EnhancedClientSession session, Game.Characters.CharacterRecord record)
        {
            var dbPath = Infrastructure.ServerPaths.DatabasePath;
            var schemaPath = Infrastructure.ServerPaths.SchemaFilePath;

            var entryRepo = new Game.CharacterData.AccountCharacterEntryRepository(dbPath, schemaPath);
            var entries = entryRepo.LoadAll();

            if (entries.Count > 0 && _getUserInfoTemplate != null)
            {
                var writer = new Network.GamePacketWriter();
                writer.WriteByte(0x02); // type
                writer.WriteUInt16(_getUserInfoTemplate.GateOrCount1);
                writer.WriteUInt16(_getUserInfoTemplate.GateOrCount2);
                writer.WriteByte(_getUserInfoTemplate.FlagOrManage);
                writer.WriteInt32(_getUserInfoTemplate.KeyOrPoint);
                writer.WriteUInt16(_getUserInfoTemplate.Unknown16);
                writer.WriteInt32(_getUserInfoTemplate.Unknown32);
                writer.WriteUInt16((ushort)entries.Count);

                foreach (var entry in entries)
                {
                    writer.WriteUInt16(entry.SlotIndex);
                    writer.WriteUtf8Dstr(entry.Name);
                    for (int j = 0; j < entry.BodyAfterName.Length; j++)
                        writer.WriteByte(entry.BodyAfterName[j]);
                }

                var type2Body = writer.ToArray();
                var type2Pkt = BuildPacketWithRouting(0x00, 0x0002, type2Body, _getUserInfoTemplate.Pkt0RoutingByte7);
                await session.SendPacketAsync(type2Pkt);

                var extraRepo = new Game.CharacterData.GetUserInfoExtraPacketRepository(dbPath, schemaPath);
                var extraPackets = extraRepo.LoadAll();
                foreach (var extra in extraPackets)
                {
                    var body = extra.body;
                    var pkt = GamePacketEnvelopeBuilder.Build(extra.command, extra.type, body);
                    await session.SendPacketAsync(pkt);
                }
                return;
            }

            if (record != null && _getUserInfoTemplate != null)
            {
                foreach (var packet in GetUserInfoResponseBuilder.Build(record, _getUserInfoTemplate))
                    await session.SendPacketAsync(packet);
            }
        }

        public async Task Handle_ENUM_CMDPACKET_CHECK_DOUBLE_CHARACTER_NAME(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 5)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02B5, new byte[] { 0x02 }));
                return;
            }

            var nameLen = BitConverter.ToInt32(body, 0);
            if (nameLen <= 0 || nameLen > 30 || 4 + nameLen > body.Length)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02B5, new byte[] { 0x14 }));
                return;
            }

            var nameRaw = new byte[nameLen];
            Buffer.BlockCopy(body, 4, nameRaw, 0, nameLen);
            if (!NameInputValidator.TryValidateRawName(nameRaw, minBytes: 2, maxBytes: 30, out var name, out var failure))
            {
                FileLogger.Log($"[{ProtocolName}] CHECK_NAME: invalid name reason={failure}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x02B5,
                    CommonPacketBodyBuilder.BuildCmdError(NameInputValidator.InvalidNameErrorCode)));
                return;
            }

            var existing = _characterRepository.GetByName(name);
            if (existing != null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02B5, new byte[] { 0x00 }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02B5, CommonPacketBodyBuilder.BuildSuccessAck()));
            FileLogger.Log($"[{ProtocolName}] CHECK_NAME: '{name}' is available");
        }

        public async Task Handle_ENUM_CMDPACKET_CREATE_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 6)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x04 }));
                return;
            }

            var job = body[0];
            if (job > 12)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x04 }));
                return;
            }

            var nameLen = BitConverter.ToInt32(body, 1);
            if (nameLen < 2 || nameLen > 18 || 5 + nameLen + 1 > body.Length)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x12 }));
                return;
            }

            var nameRaw = new byte[nameLen];
            Buffer.BlockCopy(body, 5, nameRaw, 0, nameLen);
            if (!NameInputValidator.TryValidateRawName(nameRaw, minBytes: 2, maxBytes: 18, out var nameStr, out var nameFailure))
            {
                FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER: invalid name reason={nameFailure}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0005,
                    CommonPacketBodyBuilder.BuildCmdError(NameInputValidator.InvalidNameErrorCode)));
                return;
            }

            var accountId = session.Account?.AccountId ?? 1;

            var count = _characterRepository.CountByAccount(accountId);
            var slotLimit = CharacterSlotPolicy.ResolveSlotLimit(_getUserInfoTemplate?.GateOrCount1, _getUserInfoTemplate?.GateOrCount2);
            if (!CharacterSlotPolicy.HasAvailableSlot(count, _getUserInfoTemplate?.GateOrCount1, _getUserInfoTemplate?.GateOrCount2))
            {
                FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER: account_id={accountId} has no free character slot (count={count}, limit={slotLimit})");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x04 }));
                return;
            }

            if (_characterRepository.GetByName(nameStr) != null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x00 }));
                return;
            }

            try
            {
                var record = new CharacterRecord
                {
                    AccountId = accountId,
                    Name = nameRaw,
                    Job = job,
                    GrowType = 0,
                    Level = 1,
                    TownId = 1,
                    AreaId = 0,
                    PosX = 474,
                    PosY = 234,
                    Direction = 5,
                    AreaState = 3,
                };

                var newCharId = _characterRepository.Create(record);
                FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER: created character_id={newCharId} name='{nameStr}' job={job} for account_id={accountId}");

                _selectCharacterDataSource.InitializeNewCharacter(newCharId, accountId, job);

                // 1. CMD ACK success
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, CommonPacketBodyBuilder.BuildSuccessAck()));

                // 2. NOTI 2 subtype 2 — character list refresh
                var charListBody = BuildCharacterListBody(accountId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, charListBody));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER failed: {ex.Message}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x04 }));
            }
        }

        public async Task Handle_ENUM_CMDPACKET_DELETE_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 6)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x02 }));
                return;
            }

            var slotIndex = body[0];
            var nameLen = BitConverter.ToInt32(body, 1);
            if (nameLen <= 0 || nameLen > 30 || 5 + nameLen > body.Length)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x02 }));
                return;
            }

            var name = Encoding.UTF8.GetString(body, 5, nameLen);
            var accountId = session.Account?.AccountId ?? 1;

            var list = _characterRepository.ListByAccount(accountId);
            if (slotIndex >= list.Count)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x02 }));
                return;
            }

            var target = list[slotIndex];
            if (!NameBytesEqual(target.Name, Encoding.UTF8.GetBytes(name)))
            {
                FileLogger.Log($"[{ProtocolName}] DELETE_CHARACTER: name mismatch slot={slotIndex} expected='{target.DisplayName}' got='{name}'");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x15 }));
                return;
            }

            try
            {
                _characterRepository.SoftDelete(target.CharacterId);
                FileLogger.Log($"[{ProtocolName}] DELETE_CHARACTER: soft-deleted character_id={target.CharacterId} name='{name}'");

                var writer = new GamePacketWriter();
                writer.WriteByte(0x00);
                writer.WriteUInt16((ushort)target.CharacterId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, writer.ToArray()));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] DELETE_CHARACTER failed: {ex.Message}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x28 }));
            }
        }

        public async Task Handle_ENUM_CMDPACKET_RETURN_SELECT_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0007, CommonPacketBodyBuilder.BuildSuccessAck()));
            FileLogger.Log($"[{ProtocolName}] RETURN_SELECT_CHARACTER: sent ACK for session {session.SessionId}");
        }

        public async Task SendCharacterListAsync(EnhancedClientSession session)
        {
            var accountId = session.Account?.AccountId ?? 1;
            var body = BuildCharacterListBody(accountId);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, body));
            FileLogger.Log($"[{ProtocolName}] Sent character list for account_id={accountId}");
        }

        private byte[] BuildCharacterListBody(int accountId)
        {
            var characters = _characterRepository.ListByAccount(accountId);
            var writer = new GamePacketWriter();

            var t = _getUserInfoTemplate;
            var slotLimit = CharacterSlotPolicy.ResolveSlotLimit(t?.GateOrCount1, t?.GateOrCount2);
            writer.WriteByte(2);                                                      // userInfoType = 2
            writer.WriteUInt16(slotLimit);                                             // CharacSlotLimit
            writer.WriteUInt16(t != null ? t.GateOrCount2 : slotLimit);               // SlotEffectCount
            writer.WriteByte(t != null ? t.FlagOrManage : (byte)0);                   // ManageLevel
            writer.WriteInt32(t != null ? t.KeyOrPoint : 0);                          // ManagePoint
            writer.WriteUInt16(t != null ? t.Unknown16 : (ushort)0);                  // unknownA
            writer.WriteInt32(t != null ? t.Unknown32 : 0);                           // unknownB
            writer.WriteUInt16((ushort)characters.Count);                              // entryCount

            for (int i = 0; i < characters.Count; i++)
            {
                var ch = characters[i];

                writer.WriteUInt16((ushort)i);
                writer.WriteDstr(ch.Name);
                writer.WriteByte(0x00);                 // reserved3
                writer.WriteByte(0x00);                 // reserved4
                writer.WriteByte(ch.Job);               // job
                writer.WriteByte(ch.GrowType);          // growType
                writer.WriteByte(ch.Level);             // level
                writer.WriteZeroBytes(10);              // reserved5 (10 bytes)

                var appearances = Game.Appearance.AppearanceService.LoadAppearanceFromEquipEntries(ch.CharacterId);
                writer.WriteByte((byte)appearances.Length);
                foreach (var a in appearances)
                    UserInfoSubtype0Builder.WriteAppearanceEntry(writer, a);

                var cloneTitleItemId = AppearanceService.LoadCloneTitleItemId(ch.CharacterId);
                UserInfoType2RosterTailBuilder.Write(writer, cloneTitleItemId > 0 ? (uint)cloneTitleItemId : 0);
            }

            return writer.ToArray();
        }
    }
}
