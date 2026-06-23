using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    public class SkillHandler
    {
        private readonly ICharacterRepository _characterRepository;

        public SkillHandler(ICharacterRepository characterRepository)
        {
            _characterRepository = characterRepository;
        }

        public async Task Handle_CHANGE_SKILLSLOT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 3) return;
            var ack = new byte[] { 0x01, body[0], body[1], body[2] };
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001C, ack));

            int cid = session.Player != null ? session.Player.CharacterId : 0;
            if (cid > 0)
            {
                try
                {
                    int page = body[0] == 1 ? 1 : 0;
                    new Game.CharacterData.SqliteCharacterProgressRepository(
                        Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath)
                        .SwapSkillSlot(cid, page, body[1], body[2]);
                }
                catch (Exception ex) { FileLogger.Log($"[SkillHandler] CHANGE_SKILLSLOT persist failed: {ex.Message}"); }
            }
        }

        public async Task Handle_BUY_SKILL(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 6) return;
            byte skillTree = body[0];
            int count = body[1];
            var entries = new List<Game.Skills.BuySkillEntry>();
            for (int i = 0; i < count; i++)
            {
                int off = 2 + 4 * i;
                if (off + 3 >= body.Length) break;
                entries.Add(new Game.Skills.BuySkillEntry
                {
                    SkillIndex = body[off],
                    IsRefund = body[off + 2],
                    Level = body[off + 3],
                });
            }

            int cid = session.Player != null ? session.Player.CharacterId : 0;
            int job = session.Player != null ? session.Player.Job : 0;
            if (cid > 0 && entries.Count > 0)
            {
                try
                {
                    var repo = new Game.CharacterData.SqliteCharacterProgressRepository(
                        Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);
                    var charRepo = new Game.Characters.SqliteCharacterRepository(
                        Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);
                    var rec = charRepo.GetById(cid);
                    var result = Game.Skills.BuySkillService.Execute(repo, cid, job, skillTree, entries,
                        rec?.BonusSp ?? 0, rec?.Level ?? (byte)1);
                    var ack = BuySkillAckBuilder.Build(result);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001D, ack));
                }
                catch (Exception ex) { FileLogger.Log($"[SkillHandler] BUY_SKILL failed: {ex}"); }
            }
        }

        public async Task Handle_SKILL_INIT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            int cid = session.Player != null ? session.Player.CharacterId : 0;
            byte level = session.Player != null ? session.Player.Level : (byte)1;
            if (cid <= 0) return;

            try
            {
                var repo = new Game.CharacterData.SqliteCharacterProgressRepository(
                    Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);
                var charRepo = new Game.Characters.SqliteCharacterRepository(
                    Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath);
                var rec = charRepo.GetById(cid);
                int totalSp = Game.Skills.SpTableProvider.GetTotalSp(level) + (rec?.BonusSp ?? 0);
                repo.ResetSkills(cid, (ushort)totalSp);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x01, 0x00 }));

                var dataSource = new SqliteSelectCharacterDataSource(
                    Infrastructure.ServerPaths.DatabasePath, Infrastructure.ServerPaths.SchemaFilePath,
                    _characterRepository);
                var snapshot = dataSource.Load(cid, session.Account?.AccountId ?? 1);
                var skillBody = new SkillInfoBodyBuilder();
                if (skillBody.TryBuild(snapshot, 0, out var skillBytes))
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0013, skillBytes));
            }
            catch (Exception ex) { FileLogger.Log($"[SkillHandler] SKILL_INIT failed: {ex}"); }
        }
    }
}
