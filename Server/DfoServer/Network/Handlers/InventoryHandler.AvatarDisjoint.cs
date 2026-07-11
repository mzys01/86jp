using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_DISJOINT_AVATAR(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!AvatarDisjointRequestParser.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CA,
                    AvatarDisjointAckBuilder.BuildError(AvatarDisjointResult.ErrorInvalidRequest)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] DISJOINT_AVATAR raw({body?.Length ?? 0}B): {(body == null ? "null" : BitConverter.ToString(body))} slot={request.SlotIndex} expected=0x{request.ExpectedItemTemplateId:X8}");
            var (cid, aid) = ResolveOwner(session);
            if (!_inventoryStore.TryDisjointAvatar(cid, aid, request, out var result))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CA,
                    AvatarDisjointAckBuilder.BuildError(result?.ErrorCode ?? AvatarDisjointResult.ErrorInvalidRequest)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CA,
                AvatarDisjointAckBuilder.BuildSuccess(result)));

            await _refresh.SendUpdateItemList(session, InventoryListType.Avatar, request.SlotIndex);
            var mainSlots = new List<short>();
            foreach (var material in result.Materials)
                mainSlots.Add(material.SlotIndex);
            if (mainSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Main, mainSlots);

            FileLogger.Log($"[{ProtocolName}] DISJOINT_AVATAR OK source=0x{result.SourceItemTemplateId:X8} slot={request.SlotIndex} rewards={result.Materials.Count} ack=0x00CA-native");
        }
    }
}
