using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Mercenary;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class MercenaryHandler
    {
        private const int MinimumSupportLevel = 70;
        private const ushort UserInfoNotiType = 0x0002;
        private const ushort SkillListCommand = 0x01E5;
        private const ushort SelectSkillCommand = 0x01E8;
        private const ushort TagCharacterInfoNotiType = 0x019F;

        private readonly ICharacterRepository _characterRepository;
        public string ProtocolName => "GameProtocol";

        public MercenaryHandler(ICharacterRepository characterRepository)
        {
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
        }

        public async Task HandleUserInfoSubtypeRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var subtype = body != null && body.Length > 0 ? body[0] : (byte)0xFF;

            if (subtype != 0x05)
                return;

            var accountId = session.Account?.AccountId ?? 1;
            var activeCharacterId = session.Player?.CharacterId ?? 0;
            if (activeCharacterId <= 0)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER subtype5 ignored before character select");
                return;
            }

            var roster = ListAccountCharacters(accountId);

            await SendCandidateUserInfoAsync(session, roster, activeCharacterId);
        }

        public async Task HandleMercenaryRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var accountId = session.Account?.AccountId ?? 1;
            var activeCharacterId = session.Player?.CharacterId ?? 0;
            var requestedValue = ReadRequestedValue(body);
            var roster = ListAccountCharacters(accountId);

            if (header.type == SelectSkillCommand)
            {
                await HandleSelectSkill(session, header, body, activeCharacterId, roster);
                return;
            }

            var candidate = FindCandidateByWireIndex(roster, activeCharacterId, (byte)(requestedValue & 0xFF));
            var candidateInfo = BuildCandidateInfo(candidate);
            var listAck = BuildSkillListAck(candidateInfo, (ushort)(requestedValue & 0xFFFF));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, listAck));
        }

        private async Task HandleSelectSkill(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            int activeCharacterId,
            IReadOnlyList<CharacterRecord> roster)
        {
            if (body == null || body.Length < 3)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select rejected: body too short");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            var wireSlot = body[0];
            var slot = (byte)0;
            var requestedSkillOrComboId = ReadUInt16(body, 1);

            if (activeCharacterId <= 0)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select rejected: no active character");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00, wireSlot, body[1], body[2] }));
                return;
            }

            var candidate = FindCandidateByWireIndex(roster, activeCharacterId, wireSlot);
            var candidateInfo = BuildCandidateInfo(candidate);
            var repo = CreateSupportRepository();
            if (requestedSkillOrComboId == 0)
            {
                await HandleZeroSkillSelectionAsync(session, header, repo, activeCharacterId, wireSlot, slot);
                return;
            }

            var match = FindRequestedSkill(candidateInfo, requestedSkillOrComboId);
            if (match == null)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select rejected: wire={wireSlot} requested={requestedSkillOrComboId} is not available from current candidate");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00, wireSlot, body[1], body[2] }));
                return;
            }

            var state = new MercenarySupportState
            {
                OwnerCharacterId = activeCharacterId,
                Slot = slot,
                SupportCharacterId = match.Candidate.Character.CharacterId,
                SkillId = (ushort)match.Skill.Skill.SkillIndex,
                StrikerSkillId = (ushort)match.Skill.Skill.ComboIndex,
            };

            try
            {
                repo.Save(state);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER select persist failed: {ex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00, wireSlot, body[1], body[2] }));
                return;
            }

            var ack = BuildSelectAck(state, wireSlot);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, ack));
            await SendOwnerMappedTagCharacterInfoAsync(session, activeCharacterId, new[] { state }, roster);
        }

        private async Task HandleZeroSkillSelectionAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            SqliteMercenarySupportRepository repo,
            int activeCharacterId,
            byte wireSlot,
            byte slot)
        {
            MercenarySupportState current = null;
            try
            {
                current = repo.LoadSlot(activeCharacterId, slot);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER zero-skill load failed: {ex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00, wireSlot, 0x00, 0x00 }));
                return;
            }

            if (current == null || current.SupportCharacterId <= 0 || current.SkillId == 0)
            {
                var clearAck = BuildClearAck(wireSlot);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, clearAck));
                return;
            }
            // 客户端关闭支援兵界面时会发送零技能选择；这里保留当前绑定，避免清掉刚保存的支援兵。
            var ack = BuildSelectAck(current, wireSlot);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, ack));
        }

        private IReadOnlyList<CharacterRecord> ListAccountCharacters(int accountId)
        {
            return _characterRepository.ListByAccount(accountId)
                .ToList();
        }

        internal static CharacterRecord FindCandidateByWireIndexForTest(IReadOnlyList<CharacterRecord> roster, int activeCharacterId, byte wireIndex)
        {
            return FindCandidateByWireIndex(roster, activeCharacterId, wireIndex);
        }

        private static CharacterRecord FindCandidateByWireIndex(IReadOnlyList<CharacterRecord> roster, int activeCharacterId, byte wireIndex)
        {
            if (roster == null || roster.Count == 0)
                return null;

            if (wireIndex >= roster.Count)
                return null;

            var candidate = roster[wireIndex];
            if (candidate == null || candidate.CharacterId == activeCharacterId || candidate.Level < MinimumSupportLevel)
                return null;

            return candidate;
        }

        private StrikerCandidateInfo BuildCandidateInfo(CharacterRecord ch)
        {
            if (ch == null)
                return null;

            var learnedLevels = StrikerSupportSkillLevelSource.LoadLearnedLevels(ch.CharacterId);
            var skills = new List<StrikerCandidateSkillInfo>();

            foreach (var skill in StrikerSkillDataProvider.GetAvailableSkills(ch.Job, ch.GrowType, ch.Level))
            {
                var level = StrikerSupportSkillLevelSource.ResolveBaseLevel(learnedLevels, skill);
                skills.Add(new StrikerCandidateSkillInfo
                {
                    Skill = skill,
                    Level = level,
                });
            }

            return new StrikerCandidateInfo
            {
                Character = ch,
                Skills = skills,
            };
        }

        private static StrikerSkillMatch FindRequestedSkill(StrikerCandidateInfo candidateInfo, ushort requestedSkillOrComboId)
        {
            if (candidateInfo == null)
                return null;

            return candidateInfo.Skills
                .Select(s => new StrikerSkillMatch { Candidate = candidateInfo, Skill = s })
                .FirstOrDefault(x => x.Skill.Skill.SkillIndex == requestedSkillOrComboId)
                ?? candidateInfo.Skills
                    .Select(s => new StrikerSkillMatch { Candidate = candidateInfo, Skill = s })
                    .FirstOrDefault(x => x.Skill.Skill.ComboIndex == requestedSkillOrComboId);
        }

        private static byte[] BuildSkillListAck(StrikerCandidateInfo candidateInfo, ushort requestValue)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteUInt16(requestValue);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);

            var skills = candidateInfo?.Skills
                .Where(s => s.Level > 0)
                .ToList() ?? new List<StrikerCandidateSkillInfo>();

            writer.WriteByte((byte)Math.Min(byte.MaxValue, skills.Count));
            foreach (var skill in skills.Take(byte.MaxValue))
            {
                writer.WriteByte((byte)Math.Max(0, Math.Min(byte.MaxValue, skill.Skill.ComboIndex)));
                writer.WriteUInt16((ushort)skill.Skill.SkillIndex);
                writer.WriteByte(skill.Level);
            }

            return writer.ToArray();
        }

        private static byte[] BuildSelectAck(MercenarySupportState state, byte wireSlot)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte(wireSlot);
            writer.WriteUInt16((ushort)state.SupportCharacterId);
            writer.WriteUInt16(state.SkillId);
            var level = StrikerSupportSkillLevelSource.ResolveBaseLevel(
                state.SupportCharacterId,
                state.SkillId);
            writer.WriteUInt16(level);
            writer.WriteUInt16(state.StrikerSkillId);
            writer.WriteByte(0x01);
            return writer.ToArray();
        }

        private static byte[] BuildClearAck(byte wireSlot)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte(wireSlot);
            writer.WriteUInt16(0x0000);
            return writer.ToArray();
        }

        private async Task SendCandidateUserInfoAsync(
            EnhancedClientSession session,
            IReadOnlyList<CharacterRecord> roster,
            int activeCharacterId)
        {
            if (roster == null || roster.Count == 0)
                return;

            var subtype0Repository = new SqliteSubtype0FieldsRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            foreach (var character in roster)
            {
                if (character == null || character.CharacterId == activeCharacterId || character.Level < MinimumSupportLevel)
                    continue;

                try
                {
                    if (character.Subtype0Tail == null)
                        character.Subtype0Tail = subtype0Repository.Load(character.CharacterId);

                    var writer = new GamePacketWriter();
                    writer.WriteByte(0);
                    writer.WriteUInt16(1);
                    writer.WriteUInt16((ushort)character.CharacterId);
                    writer.WriteDstr(character.Name);
                    writer.WriteBytes(UserInfoSubtype0Builder.BuildRemainingBytes(character));
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, UserInfoNotiType, writer.ToArray()));
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{ProtocolName}] MERCENARY/STRIKER candidate subtype0 failed cid={character.CharacterId}: {ex.Message}");
                }
            }
        }

        private async Task SendOwnerMappedTagCharacterInfoAsync(
            EnhancedClientSession session,
            int activeCharacterId,
            IReadOnlyList<MercenarySupportState> states,
            IReadOnlyList<CharacterRecord> candidates)
        {
            var selectedStates = GetSelectedSupportStates(states);
            if (selectedStates.Count == 0)
                return;

            var body = StrikerSupportTagCharacterPacketBuilder.BuildOwnerMappedBody(activeCharacterId, selectedStates, candidates);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, TagCharacterInfoNotiType, body));
        }

        private static List<MercenarySupportState> GetSelectedSupportStates(IReadOnlyList<MercenarySupportState> states)
        {
            return states?
                .Where(s => s != null && s.SupportCharacterId > 0 && s.SkillId != 0)
                .OrderBy(s => s.Slot)
                .ToList() ?? new List<MercenarySupportState>();
        }

        private static SqliteMercenarySupportRepository CreateSupportRepository()
        {
            return new SqliteMercenarySupportRepository(ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
        }

        private static int ReadRequestedValue(byte[] body)
        {
            if (body != null && body.Length >= 4)
                return BitConverter.ToInt32(body, 0);
            if (body != null && body.Length >= 2)
                return BitConverter.ToUInt16(body, 0);
            return 0;
        }

        private static ushort ReadUInt16(byte[] body, int offset)
        {
            return (ushort)(body[offset] | (body[offset + 1] << 8));
        }

        private sealed class StrikerCandidateInfo
        {
            public CharacterRecord Character { get; set; }
            public List<StrikerCandidateSkillInfo> Skills { get; set; } = new List<StrikerCandidateSkillInfo>();
        }

        private sealed class StrikerCandidateSkillInfo
        {
            public StrikerSkillEntry Skill { get; set; }
            public byte Level { get; set; }
        }

        private sealed class StrikerSkillMatch
        {
            public StrikerCandidateInfo Candidate { get; set; }
            public StrikerCandidateSkillInfo Skill { get; set; }
        }
    }
}
