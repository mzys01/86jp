using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_DISJOINT_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!DisjointItemRequestParser.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x001A,
                    DisjointItemAckBuilder.BuildError(DisjointItemResult.ErrorInvalidRequest)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] DISJOINT_ITEM raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} target=({request.ItemSpace},{request.TargetSlotIndex}) disjointSlot={request.DisjointItemSlotIndex} ctx=0x{request.ContextValue:X8}");

            var (cid, aid) = ResolveOwner(session);
            if (!_inventoryStore.TryDisjointItem(cid, aid, request, out var result))
            {
                var errorCode = result != null ? result.ErrorCode : DisjointItemResult.ErrorInvalidTarget;
                FileLogger.Log($"[{ProtocolName}] DISJOINT_ITEM: FAILED error=0x{errorCode:X2} target=({request.ItemSpace},{request.TargetSlotIndex})");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001A, DisjointItemAckBuilder.BuildError(errorCode)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x001A, DisjointItemAckBuilder.BuildSuccess(result)));
            if (result.RefreshItems.Count > 0)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, ItemListUpdateBuilder.BuildCommonUpdates(result.RefreshItems)));

            var materialText = result.Materials.Count > 0
                ? string.Join(", ", result.Materials.ConvertAll(m => $"0x{m.ItemTemplateId:X8}x{m.Count}@{m.SlotIndex}"))
                : string.Empty;
            FileLogger.Log($"[{ProtocolName}] DISJOINT_ITEM: OK source=0x{result.SourceItemTemplateId:X8} targetSlot={request.TargetSlotIndex} results={result.Materials.Count} materials=[{materialText}]");
        }
    }
}
