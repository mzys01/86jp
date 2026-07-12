using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class SelectCharacterPacketBuilder
    {
        private static readonly InitPacketBuilderRegistry _registry = new InitPacketBuilderRegistry();

        public static IEnumerable<byte[]> BuildPacketStream(ISelectCharacterDataSource dataSource, int characterId, int accountId)
        {
            var snapshot = dataSource.Load(characterId, accountId);
            
            
            if (snapshot.CharacterRecord != null)
                snapshot.InitializationSnapshot.AckCharSlotIndex = snapshot.CharacterRecord.TownId;

            
            var templates = (snapshot.PacketTemplates != null && snapshot.PacketTemplates.Count > 0)
                ? snapshot.PacketTemplates
                : NewCharacterInitSequence.Build();
            var darkKnightComboSent = false;
            var darkKnightComboTemplateExists = HasTemplate(templates, 0x00, 0x01C0);
            var strikerSupportSent = false;

            foreach (var template in templates)
            {
                if (template.Kind == SelectCharacterPacketTemplateKind.Raw
                    && template.Command == 0x00
                    && template.Type == 0x019F)
                {
                    if (strikerSupportSent)
                    {
                        FileLogger.Log("[SelectCharacterPacketBuilder] SKIP duplicate cmd=0 type=0x019F");
                        continue;
                    }

                    // 支援兵动态状态始终替换 occurrence 0，避免保留重复或过期项。
                    var strikerSupportBody = BuildStrikerSupportBody(snapshot);
                    strikerSupportSent = true;
                    FileLogger.Log($"[SelectCharacterPacketBuilder] OK cmd=0 type=0x019F(415) occ=0 bodyLen={strikerSupportBody.Length}");
                    yield return GamePacketEnvelopeBuilder.Build(0x00, 0x019F, strikerSupportBody);
                    continue;
                }

                if (template.Kind == SelectCharacterPacketTemplateKind.ItemList)
                {
                    var body = ItemListPacketBuilder.BuildBody(snapshot.ItemListSnapshot, template.ItemListType);
                    yield return GamePacketEnvelopeBuilder.Build(template.Command, template.Type, body);
                    continue;
                }

                bool built;
                byte[] structuredBody;
                if (template.Command == 0x01)
                    built = _registry.TryBuildCmd(template.Type, snapshot, out structuredBody);
                else if (template.Command == 0x00)
                    built = _registry.TryBuild(template.Type, snapshot, template.OccurrenceIndex, out structuredBody);
                else
                {
                    built = false;
                    structuredBody = null;
                }

                if (built)
                {
                    FileLogger.Log($"[SelectCharacterPacketBuilder] OK cmd={template.Command} type=0x{template.Type:X4}({template.Type}) occ={template.OccurrenceIndex} bodyLen={structuredBody?.Length ?? 0}");
                    yield return GamePacketEnvelopeBuilder.Build(template.Command, template.Type, structuredBody);
                    if (template.Command == 0x00 && template.Type == 0x01C0)
                        darkKnightComboSent = true;
                    if (!darkKnightComboTemplateExists
                        && template.Command == 0x00
                        && template.Type == 0x0013
                        && TryBuildDarkKnightComboSkillInfo(snapshot, out var comboBody))
                    {
                        darkKnightComboSent = true;
                        FileLogger.Log($"[SelectCharacterPacketBuilder] OK cmd=0 type=0x01C0(448) occ=0 bodyLen={comboBody.Length}");
                        yield return GamePacketEnvelopeBuilder.Build(0x00, 0x01C0, comboBody);
                    }
                    continue;
                }

                FileLogger.Log($"[SelectCharacterPacketBuilder] ERROR: no builder for cmd={template.Command} type=0x{template.Type:X4} occ={template.OccurrenceIndex}");
            }

            if (!strikerSupportSent)
            {
                var injectedStrikerSupportBody = BuildStrikerSupportBody(snapshot);
                FileLogger.Log($"[SelectCharacterPacketBuilder] INJECT cmd=0 type=0x019F(415) occ=0 bodyLen={injectedStrikerSupportBody.Length}");
                yield return GamePacketEnvelopeBuilder.Build(0x00, 0x019F, injectedStrikerSupportBody);
            }

            if (!darkKnightComboSent && TryBuildDarkKnightComboSkillInfo(snapshot, out var trailingComboBody))
            {
                FileLogger.Log($"[SelectCharacterPacketBuilder] OK cmd=0 type=0x01C0(448) occ=0 bodyLen={trailingComboBody.Length}");
                yield return GamePacketEnvelopeBuilder.Build(0x00, 0x01C0, trailingComboBody);
            }
        }

        private static bool TryBuildDarkKnightComboSkillInfo(SelectCharacterDataSnapshot snapshot, out byte[] body)
        {
            body = null;
            if (snapshot?.CharacterRecord?.Job != 9)
                return false;

            return _registry.TryBuild(0x01C0, snapshot, 0, out body);
        }

        private static byte[] BuildStrikerSupportBody(SelectCharacterDataSnapshot snapshot)
        {
            return _registry.TryBuild(0x019F, snapshot, 0, out var body)
                ? body
                : new byte[] { 0x00, 0x00 };
        }

        private static bool HasTemplate(List<SelectCharacterPacketTemplate> templates, byte command, ushort type)
        {
            if (templates == null)
                return false;

            foreach (var template in templates)
            {
                if (template.Command == command && template.Type == type)
                    return true;
            }

            return false;
        }
    }
}
