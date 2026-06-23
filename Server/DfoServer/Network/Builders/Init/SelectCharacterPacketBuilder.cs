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

            foreach (var template in templates)
            {
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
                    continue;
                }

                FileLogger.Log($"[SelectCharacterPacketBuilder] ERROR: no builder for cmd={template.Command} type=0x{template.Type:X4} occ={template.OccurrenceIndex}");
            }
        }
    }
}
