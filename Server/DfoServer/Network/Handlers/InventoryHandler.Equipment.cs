using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_ENCHANT_BY_BEAD(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!EnchantByBeadRequest.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0110, EnchantByBeadAckBuilder.BuildError(EnchantByBeadResult.ErrorInvalidBead)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] ENCHANT_BY_BEAD raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} bead=({request.BeadListType},{request.BeadSlotIndex}) target=({request.TargetListType},{request.TargetSlotIndex})");

            var (cid, aid) = ResolveOwner(session);
            var command = request.ToCommand();
            if (!_sqliteSelectCharacterDataSource.TryEnchantByBead(cid, aid, command, out var result))
            {
                var errorCode = result != null ? result.ErrorCode : EnchantByBeadResult.ErrorInvalidBead;
                FileLogger.Log($"[{ProtocolName}] ENCHANT_BY_BEAD: FAILED error=0x{errorCode:X2}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0110, EnchantByBeadAckBuilder.BuildError(errorCode)));
                return;
            }

            var updateBody = ItemListUpdateBuilder.BuildCommonUpdates(new[] { result.TargetItem, result.BeadItem });
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0110, EnchantByBeadAckBuilder.BuildSuccess(result)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, updateBody));

            FileLogger.Log($"[{ProtocolName}] ENCHANT_BY_BEAD: OK target=({request.TargetListType},{request.TargetSlotIndex}) enchantCard=0x{result.EnchantCardItemId:X8}");
        }

        public async Task Handle_ENUM_CMDPACKET_UPGRADE_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!ItemUpgradeRequest.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0050,
                    ItemUpgradeAckBuilder.BuildError(ItemUpgradeResult.ErrorInvalidTarget)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} mode={request.Mode} target=({request.TargetSlotIndex},0x{request.TargetItemTemplateId:X8}) materialSlot={request.MaterialSlotIndex} optSlot={request.OptionalTicketSlotIndex} name={request.TargetItemName}");

            var (cid, aid) = ResolveOwner(session);
            var command = request.ToCommand();
            if (!_sqliteSelectCharacterDataSource.TryUpgradeItem(cid, aid, command, out var result))
            {
                var errorCode = result != null ? result.ErrorCode : ItemUpgradeResult.ErrorInvalidTarget;
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: FAILED error={errorCode} mode={request.Mode} targetSlot={request.TargetSlotIndex} materialSlot={request.MaterialSlotIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0050, ItemUpgradeAckBuilder.BuildError(errorCode)));
                return;
            }

            var updates = new List<CommonInventoryItem>();
            if (result.TargetItem != null)
                updates.Add(result.TargetItem);
            if (result.ExtraItems != null)
                updates.AddRange(result.ExtraItems);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0050, ItemUpgradeAckBuilder.BuildSuccess(result)));

            if (updates.Count > 0)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, ItemListUpdateBuilder.BuildCommonUpdates(updates)));

            if (result.GoldCost > 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E,
                    TeleportPacketBuilder.BuildItemListUpdate(0, 0, result.UpdatedGold)));
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: gold refresh queued gold={result.UpdatedGold}");
            }

            await SendSortItemLockRefresh(session, InventoryListType.Main);

            if (result.NoticeRequired)
                await BroadcastItemUpgradeNotice(session, result);

            FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: OK scene={result.Scene} mode={result.Mode} targetSlot={result.TargetSlotIndex} level={result.OldLevel}->{result.NewLevel} success={result.UpgradeSucceeded} resultCode={result.ResultCode} rate={result.FinalSuccessWeight} gold={result.UpdatedGold}");
        }

        private async Task BroadcastItemUpgradeNotice(EnhancedClientSession session, ItemUpgradeResult result)
        {
            if (_broadcastGamePacket == null || result == null)
                return;

            try
            {
                var userUniqueId = session?.Player?.UserId ?? 0;
                if (userUniqueId == 0 && session?.Player?.CharacterId > 0)
                    userUniqueId = (ushort)session.Player.CharacterId;

                var body = ItemUpgradeNoticeBuilder.Build(result, userUniqueId);
                await _broadcastGamePacket(GamePacketEnvelopeBuilder.Build(0x00, 0x0056, body));
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: notice broadcast type=0x0056 uniqueId={userUniqueId} item=0x{result.TargetItemTemplateId:X8} level={result.NewLevel} mode={result.Mode}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: notice broadcast failed: {ex.Message}");
            }
        }

        public async Task Handle_EQUIPMENT_SOCKET_OPEN(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] EQUIP_SOCKET_OPEN 0x031D raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseSocketOpenBody(body, out var targetSlot, out var targetItemId, out var materialSlot))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x031D, new byte[] { 0x00 }));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryOpenEquipmentSocket(cid, aid, targetSlot, targetItemId, materialSlot, out var result))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x031D, new byte[] { 0x00, 0x04 }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x031D, BuildSocketOpenAck(targetSlot, targetItemId, materialSlot)));
            if (result.TargetItem != null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x000E,
                    ItemListUpdateBuilder.BuildCommonUpdates(new[] { result.TargetItem })));
            }

            if (result.MaterialConsumed && result.MaterialItem != null)
                await SendCommonMaterialRefresh(session, result.MaterialItem);

            await SendSortItemLockRefresh(session, InventoryListType.Main);
            if (result.MaterialConsumed && result.MaterialItem != null)
                FileLogger.Log($"[{ProtocolName}] EQUIP_SOCKET_OPEN: OK targetSlot={targetSlot} item=0x{targetItemId:X8} materialSlot={materialSlot} left={result.MaterialItem.RemainingStackCount}");
            else
                FileLogger.Log($"[{ProtocolName}] EQUIP_SOCKET_OPEN: OK targetSlot={targetSlot} item=0x{targetItemId:X8} already-open repaired without consuming material");
        }

        public async Task Handle_EQUIPMENT_EMBLEM_ATTACH(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] EQUIP_EMBLEM_ATTACH 0x031C raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseEmblemAttachBody(body, out var targetSlot, out var targetItemId, out var emblems))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x031C, new byte[] { 0x00 }));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TrySetEquipmentEmblems(cid, aid, targetSlot, targetItemId, emblems, out var result))
            {
                if (await TryHandleAvatarEmblemAttach(session, 0x031C, targetSlot, targetItemId, emblems, cid, aid))
                    return;

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x031C, new byte[] { 0x00, 0x04 }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x031C, BuildEmblemAttachAck(targetSlot, targetItemId, emblems.Count)));
            if (!result.TargetEquipped && result.TargetItem != null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x000E,
                    ItemListUpdateBuilder.BuildCommonUpdates(new[] { result.TargetItem })));
            }
            await SendSortItemLockRefresh(session, InventoryListType.Main);
            FileLogger.Log($"[{ProtocolName}] EQUIP_EMBLEM_ATTACH: OK targetSlot={targetSlot} item=0x{targetItemId:X8} emblems={emblems.Count}");
        }

        public async Task Handle_AVATAR_SOCKET_OPEN(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] AVATAR_SOCKET_OPEN 0x00CE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseSocketOpenBody(body, out var targetSlot, out var targetItemId, out var materialSlot))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CE, new byte[] { 0x00 }));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryOpenAvatarSocket(cid, aid, targetSlot, targetItemId, materialSlot, out var result))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CE, new byte[] { 0x00, 0x04 }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CE, BuildSocketOpenAck(targetSlot, targetItemId, materialSlot)));

            if (result.MaterialConsumed && result.MaterialItem != null)
                await SendCommonMaterialRefresh(session, result.MaterialItem);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000E,
                ItemListUpdateBuilder.BuildAvatarUpdates(new[] { result.TargetItem })));
            await SendSortItemLockRefresh(session, InventoryListType.Avatar);

            if (result.MaterialConsumed && result.MaterialItem != null)
                FileLogger.Log($"[{ProtocolName}] AVATAR_SOCKET_OPEN: OK targetSlot={targetSlot} item=0x{targetItemId:X8} materialSlot={materialSlot} left={result.MaterialItem.RemainingStackCount}");
            else
                FileLogger.Log($"[{ProtocolName}] AVATAR_SOCKET_OPEN: OK targetSlot={targetSlot} item=0x{targetItemId:X8} already-open repaired without consuming material");
        }

        public async Task Handle_AVATAR_EMBLEM_ATTACH(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] AVATAR_EMBLEM_ATTACH 0x{header.type:X4} raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseAvatarEmblemAttachBody(body, out var targetSlot, out var targetItemId, out var emblems))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!await TryHandleAvatarEmblemAttach(session, header.type, targetSlot, targetItemId, emblems, cid, aid))
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00, 0x04 }));
        }

        private async Task<bool> TryHandleAvatarEmblemAttach(EnhancedClientSession session, ushort ackType, short targetSlot, int targetItemId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, int cid, int aid)
        {
            if (!_sqliteSelectCharacterDataSource.TrySetAvatarEmblems(cid, aid, targetSlot, targetItemId, emblems, out var result))
                return false;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, ackType, BuildEmblemAttachAck(targetSlot, targetItemId, emblems.Count)));
            if (!result.TargetEquipped && result.TargetItem != null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x000E,
                    ItemListUpdateBuilder.BuildAvatarUpdates(new[] { result.TargetItem })));
            }

            await SendSortItemLockRefresh(session, InventoryListType.Main);
            if (!result.TargetEquipped)
                await SendSortItemLockRefresh(session, InventoryListType.Avatar);
            FileLogger.Log($"[{ProtocolName}] AVATAR_EMBLEM_ATTACH: OK targetSlot={targetSlot} item=0x{targetItemId:X8} emblems={emblems.Count} ack=0x{ackType:X4}");
            return true;
        }

        private async Task SendCommonMaterialRefresh(EnhancedClientSession session, InventoryMutationResult material)
        {
            if (material == null)
                return;

            var (cid, aid) = ResolveOwner(session);
            var item = _sqliteSelectCharacterDataSource.LoadCommonItemForRefresh(cid, aid, material.ListType, material.SlotIndex);
            if (item == null)
            {
                item = new CommonInventoryItem
                {
                    SlotIndex = material.SlotIndex,
                    ItemTemplateId = -1,
                    CountOrInstanceValue = 0,
                    Marker16 = 0,
                    PrefixData0E = new byte[8],
                    MiddleData1A = new byte[17],
                    TailData2F = new byte[37],
                };
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000E,
                ItemListUpdateBuilder.BuildCommonUpdates(new[] { item })));
        }

        private static bool TryParseSocketOpenBody(byte[] body, out short targetSlot, out int targetItemId, out short materialSlot)
        {
            targetSlot = 0;
            targetItemId = 0;
            materialSlot = 0;
            if (body == null || body.Length < 8)
                return false;

            targetSlot = BitConverter.ToInt16(body, 0);
            targetItemId = BitConverter.ToInt32(body, 2);
            materialSlot = BitConverter.ToInt16(body, 6);
            return true;
        }

        private static bool TryParseEmblemAttachBody(byte[] body, out short targetSlot, out int targetItemId, out List<EquipmentEmblemApplyRequest> emblems)
        {
            targetSlot = 0;
            targetItemId = 0;
            emblems = null;
            if (body == null || body.Length < 7)
                return false;

            targetSlot = BitConverter.ToInt16(body, 0);
            targetItemId = BitConverter.ToInt32(body, 2);
            var count = body[6];
            var offset = 7;
            emblems = new List<EquipmentEmblemApplyRequest>();
            for (var index = 0; index < count; index++)
            {
                if (offset + 7 > body.Length)
                    return false;

                emblems.Add(new EquipmentEmblemApplyRequest
                {
                    EmblemSlot = BitConverter.ToInt16(body, offset),
                    EmblemItemTemplateId = BitConverter.ToInt32(body, offset + 2),
                    SocketIndex = body[offset + 6],
                });
                offset += 7;
            }
            return true;
        }

        private static bool TryParseAvatarEmblemAttachBody(byte[] body, out short targetSlot, out int targetItemId, out List<EquipmentEmblemApplyRequest> emblems)
        {
            targetSlot = 0;
            targetItemId = 0;
            emblems = null;
            if (body == null)
                return false;

            if (body.Length >= 8 && body[0] == (byte)InventoryListType.Avatar)
                return TryParseEmblemAttachBodyAt(body, 1, out targetSlot, out targetItemId, out emblems);

            return TryParseEmblemAttachBody(body, out targetSlot, out targetItemId, out emblems);
        }

        private static bool TryParseEmblemAttachBodyAt(byte[] body, int startOffset, out short targetSlot, out int targetItemId, out List<EquipmentEmblemApplyRequest> emblems)
        {
            targetSlot = 0;
            targetItemId = 0;
            emblems = null;
            if (body == null || startOffset < 0 || body.Length < startOffset + 7)
                return false;

            targetSlot = BitConverter.ToInt16(body, startOffset);
            targetItemId = BitConverter.ToInt32(body, startOffset + 2);
            var count = body[startOffset + 6];
            var offset = startOffset + 7;
            emblems = new List<EquipmentEmblemApplyRequest>();
            for (var index = 0; index < count; index++)
            {
                if (offset + 7 > body.Length)
                    return false;

                emblems.Add(new EquipmentEmblemApplyRequest
                {
                    EmblemSlot = BitConverter.ToInt16(body, offset),
                    EmblemItemTemplateId = BitConverter.ToInt32(body, offset + 2),
                    SocketIndex = body[offset + 6],
                });
                offset += 7;
            }
            return true;
        }

        private static byte[] BuildSocketOpenAck(short targetSlot, int targetItemId, short materialSlot)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(targetSlot);
            writer.WriteInt32(targetItemId);
            writer.WriteInt16(materialSlot);
            return writer.ToArray();
        }

        private static byte[] BuildEmblemAttachAck(short targetSlot, int targetItemId, int emblemCount)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(targetSlot);
            writer.WriteInt32(targetItemId);
            writer.WriteByte((byte)Math.Max(0, Math.Min(255, emblemCount)));
            return writer.ToArray();
        }
    }
}
