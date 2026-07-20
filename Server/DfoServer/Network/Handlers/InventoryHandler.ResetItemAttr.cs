using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_RESET_ITEM_ATTR(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!ResetItemAttrRequestParser.TryParseCompatible(body, out var request, out var decodedLegacyCipher))
            {
                FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: invalid body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketType.RESET_ITEM_ATTR,
                    ResetItemAttrAckBuilder.BuildError(ResetItemAttrResult.ErrorInvalidRequest)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR raw({body.Length}B): {BitConverter.ToString(body)} target=({request.TargetSlotIndex},0x{request.TargetItemTemplateId:X8}) materialSlot={request.MaterialSlotIndex} legacyCipherDecoded={decodedLegacyCipher}");

            var (characterId, accountId) = ResolveOwner(session);
            ResetItemAttrResult result;
            bool success;
            try
            {
                success = _inventoryStore.TryResetItemAttr(characterId, accountId, request, out result);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: exception targetSlot={request.TargetSlotIndex} materialSlot={request.MaterialSlotIndex}: {ex.Message}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketType.RESET_ITEM_ATTR,
                    ResetItemAttrAckBuilder.BuildError(ResetItemAttrResult.ErrorUnsupported)));
                return;
            }

            if (!success)
            {
                var errorCode = result != null ? result.ErrorCode : ResetItemAttrResult.ErrorInvalidRequest;
                FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: FAILED error=0x{errorCode:X2} targetSlot={request.TargetSlotIndex} materialSlot={request.MaterialSlotIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketType.RESET_ITEM_ATTR,
                    ResetItemAttrAckBuilder.BuildError(errorCode)));
                return;
            }

            // The command ACK is deliberately separate from COMPLETE_DISPLAY:
            // both use type 0x0051, but the former is cmd=1. The generic CMD
            // status byte precedes the target item id, list type and slot.
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.RESET_ITEM_ATTR,
                ResetItemAttrAckBuilder.BuildSuccess(result)));

            var refreshFailed = false;
            try
            {
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    new[] { result.TargetSlotIndex, result.MaterialSlotIndex });
            }
            catch (Exception ex)
            {
                refreshFailed = true;
                FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: incremental refresh failed, falling back to full Main refresh: {ex.Message}");
            }

            if (refreshFailed)
            {
                try
                {
                    await _refresh.SendItemListRefresh(session, InventoryListType.Main);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: full Main refresh failed: {ex.Message}");
                }
            }

            if (result.MaterialRemainingCount == 0)
            {
                try
                {
                    await _refresh.SendSortItemLockRefresh(session, InventoryListType.Main);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: sort-lock refresh failed: {ex.Message}");
                }
            }

            // Do not emit NOTI 0x0051 here.  The client-side COMPLETE_DISPLAY
            // handler uses that packet as a one-byte display-tracker update;
            // its byte is an internal tracker index (0..7), not the reset
            // mode or inventory slot.  RESET_ITEM_ATTR has no tracker index
            // to send, and the business result is fully represented by the
            // cmd=1 ACK plus the item refresh above.
            FileLogger.Log($"[{ProtocolName}] RESET_ITEM_ATTR: OK mode={result.Mode} targetSlot={result.TargetSlotIndex} material=0x{result.MaterialItemTemplateId:X8}@{result.MaterialSlotIndex} remaining={result.MaterialRemainingCount} quality={result.OldQualitySeed}->{result.NewQualitySeed} ack={ResetItemAttrAckBuilder.SuccessLength}B completeDisplay=not-sent(tracker-only)");
        }
    }
}
