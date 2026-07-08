using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_USE_STACKABLE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {

            if (body == null || body.Length < 7)
                return;

            var slotIndex = BitConverter.ToInt16(body, 0);
            var listType = (InventoryListType)body[2];
            var instanceValue = BitConverter.ToInt32(body, 3);
            var itemCode = body.Length >= 11 ? BitConverter.ToInt32(body, 7) : 0;

            var (cid, aid) = ResolveOwner(session);

            if (_inventoryStore.TryUseAccountCargoUpgradeTool(cid, aid, listType, slotIndex, out var accountCargoToolResult)
                && accountCargoToolResult.Handled)
            {
                await SendAccountCargoUpgradeToolResponse(session, listType, slotIndex, instanceValue, itemCode, accountCargoToolResult);
                return;
            }

            if (_inventoryStore.TryUsePersonalCargoUpgradeTicket(cid, aid, listType, slotIndex, itemCode, out var cargoTicketResult)
                && cargoTicketResult.Handled)
            {
                await SendPersonalCargoUpgradeTicketResponse(session, listType, slotIndex, instanceValue, itemCode, cargoTicketResult);
                return;
            }

            var runeRequest = new EquipmentEffectRuneUseRequest
            {
                SourceListType = listType,
                SourceSlotIndex = slotIndex,
                SourceInstanceValue = instanceValue,
                ExpectedSourceItemTemplateId = itemCode,
                RawBody = body,
            };
            if (_inventoryStore.TryUseEquipmentEffectRune(cid, aid, runeRequest, out var runeResult)
                && runeResult.Handled)
            {
                await SendEquipmentEffectRuneResponse(session, listType, slotIndex, instanceValue, itemCode, runeResult);
                return;
            }

            var consumed = _inventoryStore.TryDeleteItem(cid, aid, listType, slotIndex, 1, out var result);
            var responsePlan = BuildUseStackableResponsePlan(consumed, result, listType, slotIndex, instanceValue, itemCode);
            if (!consumed)
            {
                FileLogger.Log(responsePlan.StalePetConsumable
                    ? $"[{ProtocolName}] USE_STACKABLE: stale pet consumable use acknowledged item 0x{itemCode:X8} at listType={listType} slot={slotIndex}"
                    : $"[{ProtocolName}] USE_STACKABLE: failed to consume item 0x{itemCode:X8} at listType={listType} slot={slotIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x002C, responsePlan.AckBody));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x002C, responsePlan.AckBody));
            if (responsePlan.ItemListUpdateBody != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, responsePlan.ItemListUpdateBody));

            var petSatietyLog = result.PetSatietyChanged
                ? $" petSatiety key={result.PetCreatureKey} {result.PetSatietyBefore}->{result.PetSatietyAfter}"
                : string.Empty;
            FileLogger.Log($"[{ProtocolName}] USE_STACKABLE: consumed 1x item 0x{itemCode:X8} from slot {slotIndex}, remaining={result.RemainingStackCount}{petSatietyLog}");
        }

        public async Task Handle_ADD_EQUIPMENT_EFFECT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] ADD_EQUIPMENT_EFFECT 0x0342 raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!EquipmentEffectRuneUseRequest.TryParseAddEquipmentEffectBody(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0342, new byte[] { 0x00 }));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!_inventoryStore.TryUseEquipmentEffectRune(cid, aid, request, out var result)
                || result == null
                || !result.Handled)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0342, new byte[] { 0x00, 0x04 }));
                FileLogger.Log($"[{ProtocolName}] ADD_EQUIPMENT_EFFECT: unhandled sourceSlot={request.SourceSlotIndex} target={request.TargetListType}:{request.TargetSlotIndex}");
                return;
            }

            await SendEquipmentEffectRuneResponse(
                session,
                request.SourceListType,
                request.SourceSlotIndex,
                request.SourceInstanceValue,
                result.SourceItemTemplateId,
                result,
                0x0342,
                result.Success ? BuildAddEquipmentEffectAck(body) : new byte[] { 0x00, 0x04 });
        }

        private async Task SendEquipmentEffectRuneResponse(
            EnhancedClientSession session,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int itemCode,
            EquipmentEffectRuneUseResult result,
            ushort responseType = 0x002C,
            byte[] ackOverride = null)
        {
            var responseItemCode = itemCode != 0 ? itemCode : result.SourceItemTemplateId;
            var responseInstanceValue = instanceValue != 0 ? instanceValue : result.SourceInstanceValue;
            var ackBody = ackOverride ?? (result.Success
                ? UseStackableAckBuilder.BuildSuccess(slotIndex, (byte)listType, responseInstanceValue, responseItemCode)
                : UseStackableAckBuilder.BuildError((byte)listType, responseItemCode, responseInstanceValue));

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, responseType, ackBody));

            if (!result.Success)
            {
                FileLogger.Log($"[{ProtocolName}] USE_STACKABLE equipment-effect: failed status={result.Status} item=0x{result.SourceItemTemplateId:X8} listType={listType} slot={slotIndex}");
                return;
            }

            await _refresh.SendUpdateItemList(session, result.SourceListType, result.SourceSlotIndex);
            await _refresh.SendUpdateItemList(session, result.TargetListType, result.TargetSlotIndex);

            FileLogger.Log($"[{ProtocolName}] USE_STACKABLE equipment-effect: rune=0x{result.SourceItemTemplateId:X8} effect={result.AppliedEffectId} target=0x{result.TargetItemTemplateId:X8}@{result.TargetListType}:{result.TargetSlotIndex} remaining={result.SourceRemainingStackCount}");
        }

        private static byte[] BuildAddEquipmentEffectAck(byte[] requestBody)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            if (requestBody != null && requestBody.Length > 0)
                writer.WriteBytes(requestBody);
            return writer.ToArray();
        }

        private async Task SendPersonalCargoUpgradeTicketResponse(
            EnhancedClientSession session,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int itemCode,
            PersonalCargoUpgradeTicketResult result)
        {
            var responseItemCode = itemCode != 0 ? itemCode : result.ItemTemplateId;
            var ackBody = result.Success
                ? UseStackableAckBuilder.BuildSuccess(slotIndex, (byte)listType, instanceValue, responseItemCode)
                : UseStackableAckBuilder.BuildError((byte)listType, responseItemCode, instanceValue);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x002C, ackBody));

            if (!result.Success)
            {
                FileLogger.Log($"[{ProtocolName}] USE_STACKABLE upgrade-cargo: failed status={result.Status} item=0x{result.ItemTemplateId:X8} listType={listType} slot={slotIndex} current={result.PreviousListParam16}");
                return;
            }

            if (result.ConsumedItem != null)
                await _refresh.SendUpdateItemList(session, result.ConsumedItem.ListType, result.ConsumedItem.SlotIndex);

            await _refresh.SendItemListRefresh(session, InventoryListType.PersonalCargo);
            FileLogger.Log($"[{ProtocolName}] USE_STACKABLE upgrade-cargo: item=0x{result.ItemTemplateId:X8} slot={slotIndex} personalCargo={result.PreviousListParam16}->{result.NewListParam16} remaining={result.ConsumedItem?.RemainingStackCount ?? 0}");
        }

        private async Task SendAccountCargoUpgradeToolResponse(
            EnhancedClientSession session,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int itemCode,
            AccountCargoUpgradeToolResult result)
        {
            var responseItemCode = itemCode != 0 ? itemCode : result.ItemTemplateId;
            var ackBody = result.Success
                ? UseStackableAckBuilder.BuildSuccess(slotIndex, (byte)listType, instanceValue, responseItemCode)
                : UseStackableAckBuilder.BuildError((byte)listType, responseItemCode, instanceValue);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x002C, ackBody));

            if (!result.Success)
            {
                FileLogger.Log($"[{ProtocolName}] USE_STACKABLE account-cargo-upgrade: failed status={result.Status} item=0x{result.ItemTemplateId:X8} listType={listType} slot={slotIndex} current={result.PreviousSelectionKey}");
                return;
            }

            if (result.ConsumedItem != null)
                await _refresh.SendUpdateItemList(session, result.ConsumedItem.ListType, result.ConsumedItem.SlotIndex);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0132,
                CommonPacketBodyBuilder.BuildSuccessAck()));
            await _refresh.SendItemListRefresh(session, InventoryListType.AccountCargo);
            FileLogger.Log($"[{ProtocolName}] USE_STACKABLE account-cargo-upgrade: item=0x{result.ItemTemplateId:X8} slot={slotIndex} accountCargo={result.PreviousSelectionKey}->{result.NewSelectionKey} remaining={result.ConsumedItem?.RemainingStackCount ?? 0}");
        }

        internal static UseStackableResponsePlan BuildUseStackableResponsePlan(
            bool consumed,
            InventoryMutationResult result,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int itemCode)
        {
            var stalePetConsumable = !consumed && IsPetConsumableSlot(listType, slotIndex);
            var ackBody = consumed || stalePetConsumable
                ? UseStackableAckBuilder.BuildSuccess(slotIndex, (byte)listType, instanceValue, itemCode)
                : UseStackableAckBuilder.BuildError((byte)listType, itemCode, instanceValue);

            return new UseStackableResponsePlan
            {
                AckBody = ackBody,
                ItemListUpdateBody = null,
                StalePetConsumable = stalePetConsumable,
            };
        }

        private static bool IsPetConsumableSlot(InventoryListType listType, short slotIndex)
        {
            return listType == InventoryListType.Pet
                && slotIndex >= SqliteInventoryStore.PetConsumableSlotStart
                && slotIndex <= SqliteInventoryStore.PetConsumableSlotEnd;
        }

        internal sealed class UseStackableResponsePlan
        {
            public byte[] AckBody { get; set; }

            public byte[] ItemListUpdateBody { get; set; }

            public bool StalePetConsumable { get; set; }
        }

        public async Task Handle_OPEN_AVATAR_PACKAGE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            var parsedAvatar = AvatarPackageOpenRequest.TryParse(body, out var request);
            if (!parsedAvatar)
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE: parse failed");
            }
            else
            {
                var (cid, aid) = ResolveOwner(session);
                if (_inventoryStore.TryOpenAvatarPackage(cid, aid, request, out var result))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0207, AvatarPackageAckBuilder.BuildSuccess(result.SlotIndex)));
                    if (result.GrantedItems.Count > 0)
                    {
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                            SelectablePackageAckBuilder.BuildSuccess(result.SlotIndex, result.GrantedItems)));
                    }

                    if (result.SourceRemainingStackCount <= 0)
                        await SendConsumedSourceItemUpdate(session, result.SlotIndex, result.PackageItemTemplateId);

                    var snapshot = _inventoryStore.LoadCharacterItemListSnapshot(cid, aid);
                    var mainUpdateBody = BuildGrantedMainItemUpdates(snapshot, result.GrantedItems, result.SlotIndex, result.PackageItemTemplateId, result.SourceRemainingStackCount > 0);
                    if (mainUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, mainUpdateBody));

                    await SendAvatarOrPetUpdateListForGrantedItems(session, result.GrantedItems);

                    FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE: OK slot={result.SlotIndex} item=0x{result.PackageItemTemplateId:X8} avatar={result.AddedAvatarCount} main={result.AddedMainItemCount} pet={result.AddedPetCount}");
                    return;
                }

                FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE: avatar path failed slot={request.SlotIndex} choices={request.Choices.Count}, trying general package 0x0207");
            }

            if (await TryHandleOpenPackage0207(session, header, body))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0207, new byte[] { 0x00 }));
        }

        public async Task Handle_OPEN_SELECTABLE_PACKAGE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            var parsedSelectable = SelectablePackageOpenRequest.TryParse(body, out var request);
            if (!parsedSelectable)
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE: parse failed");
            }
            else
            {
                var (cid, aid) = ResolveOwner(session);
                if (_inventoryStore.TryOpenSelectablePackage(cid, aid, request, out var result))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0, SelectablePackageAckBuilder.BuildSuccess(result.SlotIndex, result.GrantedItems)));

                    if (result.SourceRemainingStackCount <= 0)
                        await SendConsumedSourceItemUpdate(session, result.SlotIndex, result.PackageItemTemplateId);

                    var snapshot = _inventoryStore.LoadCharacterItemListSnapshot(cid, aid);
                    var mainUpdateBody = BuildGrantedMainItemUpdates(snapshot, result.GrantedItems, result.SlotIndex, result.PackageItemTemplateId, result.SourceRemainingStackCount > 0);
                    if (mainUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, mainUpdateBody));

                    await SendAvatarOrPetUpdateListForGrantedItems(session, result.GrantedItems);

                    FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE: OK slot={result.SlotIndex} item=0x{result.PackageItemTemplateId:X8} reward=0x{result.RewardItemTemplateId:X8} main={result.AddedMainItemCount} avatar={result.AddedAvatarCount} pet={result.AddedPetCount} ackItems={result.GrantedItems.Count}");
                    return;
                }

                FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE: selectable path failed slot={request.SlotIndex} selected=0x{request.SelectedItemTemplateId:X8}, trying general booster");
            }

            if (await TryHandleBoosterOpen(session, header, body))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0, SelectablePackageAckBuilder.BuildError()));
        }

        public async Task Handle_USE_BOOSTER_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!await TryHandleBoosterOpen(session, header, body))
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
        }

        public async Task Handle_OPEN_MAGIC_BOX(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var elapsed = Stopwatch.StartNew();
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!MagicBoxOpenRequest.TryParse(body, out var request) || request.ListType != InventoryListType.Main)
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX: parse/list failed");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            var materialSlotIndex = request.MaterialSlotIndex >= 0
                ? (short?)request.MaterialSlotIndex
                : null;
            var expectedMaterialItemTemplateId = request.MaterialItemTemplateId > 0
                ? request.MaterialItemTemplateId
                : 0;

            var (cid, aid) = ResolveOwner(session);
            if (!_inventoryStore.TryUseBoosterItem(
                    cid,
                    aid,
                    new BoosterUseRequest
                    {
                        SlotIndex = request.SlotIndex,
                        SelectedItemTemplateIds = Array.Empty<int>(),
                        ExpectedItemTemplateId = request.ItemTemplateId,
                        MaterialSlotIndex = materialSlotIndex,
                        ExpectedMaterialItemTemplateId = expectedMaterialItemTemplateId,
                        RequestedCount = request.RequestedCount,
                    },
                    out var result))
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX: failed cid={cid} aid={aid} slot={request.SlotIndex} item=0x{request.ItemTemplateId:X8} material=0x{request.MaterialItemTemplateId:X8}@{request.MaterialSlotIndex} requested={request.RequestedCount} elapsed={elapsed.ElapsedMilliseconds}ms");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            result.MagicBoxClientType = request.RawListType;
            await SendBoosterUseResult(session, header.type, result);
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} requested={request.RequestedCount} applied={result.ConsumedSourceCount} remaining={result.SourceRemainingStackCount} material=0x{result.ConsumedMaterialItemTemplateId:X8}x{result.ConsumedMaterialCount}@{result.ConsumedMaterialSlotIndex} materialRemaining={result.ConsumedMaterialRemainingStackCount}{FormatBoosterOpenState(result)} rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))} elapsed={elapsed.ElapsedMilliseconds}ms");
        }

        public async Task Handle_OPEN_MAGIC_BOX_SINGLE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var elapsed = Stopwatch.StartNew();
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!MagicBoxOpenRequest.TryParseSingle(body, out var request) || request.ListType != InventoryListType.Main)
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE: parse/list failed");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            var materialSlotIndex = request.MaterialSlotIndex >= 0
                ? (short?)request.MaterialSlotIndex
                : null;
            var expectedMaterialItemTemplateId = request.MaterialItemTemplateId > 0
                ? request.MaterialItemTemplateId
                : 0;

            var (cid, aid) = ResolveOwner(session);
            if (!_inventoryStore.TryUseBoosterItem(
                    cid,
                    aid,
                    new BoosterUseRequest
                    {
                        SlotIndex = request.SlotIndex,
                        SelectedItemTemplateIds = Array.Empty<int>(),
                        ExpectedItemTemplateId = request.ItemTemplateId,
                        MaterialSlotIndex = materialSlotIndex,
                        ExpectedMaterialItemTemplateId = expectedMaterialItemTemplateId,
                        RequestedCount = request.RequestedCount,
                    },
                    out var result))
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE: failed cid={cid} aid={aid} slot={request.SlotIndex} materialSlot={(materialSlotIndex.HasValue ? materialSlotIndex.Value.ToString() : "auto")} requested={request.RequestedCount} elapsed={elapsed.ElapsedMilliseconds}ms");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            result.MagicBoxClientType = request.RawListType;
            await SendBoosterUseResult(session, header.type, result);
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} requested={request.RequestedCount} applied={result.ConsumedSourceCount} remaining={result.SourceRemainingStackCount} material=0x{result.ConsumedMaterialItemTemplateId:X8}x{result.ConsumedMaterialCount}@{result.ConsumedMaterialSlotIndex} materialRemaining={result.ConsumedMaterialRemainingStackCount}{FormatBoosterOpenState(result)} rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))} elapsed={elapsed.ElapsedMilliseconds}ms");
        }

        private async Task<bool> TryHandleBoosterOpen(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var elapsed = Stopwatch.StartNew();
            short? slotIndex = body != null && body.Length >= 2
                ? BitConverter.ToInt16(body, 0)
                : (short?)null;
            var selectedItemTemplateIds = ParseBoosterSelectionItemIds(body);
            var selectedText = selectedItemTemplateIds.Count == 0
                ? "none"
                : string.Join(",", selectedItemTemplateIds.Select(id => $"0x{id:X8}"));
            FileLogger.Log($"[{ProtocolName}] USE_BOOSTER raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} slot={(slotIndex.HasValue ? slotIndex.Value.ToString() : "auto")} selected={selectedText}");

            if (TryBuildCrystalContractBodyFromUpdateRequest(header.type, body, out var crystalContractBody))
            {
                var owner = ResolveOwner(session);
                if (!_sqliteSelectCharacterDataSource.TrySaveCrystalContractSelection(owner.characterId, crystalContractBody))
                {
                    FileLogger.Log($"[{ProtocolName}] UPDATE_CONTRACT_OF_CUBE_INFO: failed cid={owner.characterId} body={BitConverter.ToString(crystalContractBody)}");
                    return false;
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, CommonPacketBodyBuilder.BuildSuccessAck()));
                FileLogger.Log($"[{ProtocolName}] UPDATE_CONTRACT_OF_CUBE_INFO: saved cid={owner.characterId} body={BitConverter.ToString(crystalContractBody)}");
                return true;
            }

            if (slotIndex == 0 && header.type == 0x0218)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, CommonPacketBodyBuilder.BuildSuccessAck()));
                FileLogger.Log($"[{ProtocolName}] USE_BOOSTER: confirm ack type=0x{header.type:X4}");
                return true;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!_inventoryStore.TryUseBoosterItem(cid, aid, new BoosterUseRequest
            {
                SlotIndex = slotIndex,
                SelectedItemTemplateIds = selectedItemTemplateIds,
            }, out var result))
            {
                FileLogger.Log($"[{ProtocolName}] USE_BOOSTER: failed cid={cid} aid={aid} slot={(slotIndex.HasValue ? slotIndex.Value.ToString() : "auto")} elapsed={elapsed.ElapsedMilliseconds}ms");
                return false;
            }

            await SendBoosterUseResult(session, header.type, result);
            FileLogger.Log($"[{ProtocolName}] USE_BOOSTER: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} remaining={result.SourceRemainingStackCount}, rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))}, elapsed={elapsed.ElapsedMilliseconds}ms");
            return true;
        }

        internal static bool TryBuildCrystalContractBodyFromUpdateRequest(ushort requestType, byte[] body, out byte[] crystalContractBody)
        {
            crystalContractBody = null;
            if (requestType != 0x0218 || body == null || body.Length != 2)
                return false;

            // 00 FF 表示契约已开启但不选晶体，也必须覆盖旧选择。
            if (body[0] != 0x00 || (body[1] > 0x05 && body[1] != 0xFF))
                return false;

            crystalContractBody = new byte[] { body[0], body[1] };
            return true;
        }

        private async Task<bool> TryHandleOpenPackage0207(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 3)
                return false;

            var slotIndex = BitConverter.ToInt16(body, 0);
            var selectedItemTemplateIds = Parse0207ItemIds(body);
            FileLogger.Log($"[{ProtocolName}] OPEN_PACKAGE_0207 raw({body.Length}B): {BitConverter.ToString(body)} slot={slotIndex} selected={string.Join(",", selectedItemTemplateIds.Select(id => $"0x{id:X8}"))}");

            var (cid, aid) = ResolveOwner(session);
            if (!_inventoryStore.TryOpenPackage0207(cid, aid, slotIndex, selectedItemTemplateIds, out var result))
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_PACKAGE_0207: failed slot={slotIndex}");
                return false;
            }

            await SendBoosterUseResult(session, header.type, result);
            FileLogger.Log($"[{ProtocolName}] OPEN_PACKAGE_0207: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} rewards={result.Rewards.Count}");
            return true;
        }

        private async Task SendBoosterUseResult(EnhancedClientSession session, ushort responseType, BoosterUseResult result)
        {
            var useNativeMagicBoxBatchAck = responseType == 0x03F3 && ShouldUseNativeMagicBoxBatchAck(result);
            var grantedItems = responseType == 0x03F3 && !useNativeMagicBoxBatchAck
                ? ToPackageGrantedItems(result)
                : ToBoosterPopupGrantedItems(result);

            if (responseType == 0x00A0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                    SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
            }
            else if (responseType == 0x0207)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0207,
                    AvatarPackageAckBuilder.BuildSuccess(result.SourceSlotIndex)));
                if (grantedItems.Count > 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                        SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
                }
            }
            else if (responseType == 0x03F3)
            {
                if (useNativeMagicBoxBatchAck)
                {
                    var ackBody = MagicBoxOpenAckBuilder.BuildBatch(result);
                    FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX ACK: type=0x03F3 bodyLen={ackBody.Length} head={FormatPacketHead(ackBody, 24)}{FormatBoosterOpenState(result)} {FormatBoosterRows(result, false)}");
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x03F3, ackBody));
                }
                else if (grantedItems.Count > 0)
                {
                    FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX legacy popup: type=0x03F3 rows={grantedItems.Count}{FormatBoosterOpenState(result)}");
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                        SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
                }
            }
            else if (responseType == 0x00D0)
            {
                var ackBody = MagicBoxOpenAckBuilder.BuildSingle(result);
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX ACK: type=0x00D0 bodyLen={ackBody.Length} head={FormatPacketHead(ackBody, 24)}{FormatBoosterOpenState(result)} {FormatBoosterRows(result, true)}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00D0, ackBody));
            }
            else
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, responseType, CommonPacketBodyBuilder.BuildSuccessAck()));
                if (grantedItems.Count > 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                        SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
                }
            }

            var (cid, aid) = ResolveOwner(session);

            if (result.SourceRemainingStackCount <= 0)
                await SendConsumedSourceItemUpdate(session, result.SourceSlotIndex, result.SourceItemTemplateId);

            var snapshot = _inventoryStore.LoadCharacterItemListSnapshot(cid, aid);
            var mainUpdateBody = BuildBoosterMainItemUpdates(snapshot, result, result.SourceRemainingStackCount > 0);
            if (mainUpdateBody != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, mainUpdateBody));

            await SendAvatarOrPetUpdateListForBoosterRewards(session, result);
        }

        internal static bool ShouldSendSourceAckForBoosterResponse(ushort responseType)
        {
            return responseType != 0x00D0 && responseType != 0x03F3;
        }

        internal static bool ShouldUseNativeMagicBoxBatchAck(BoosterUseResult result)
        {
            return result != null && result.IsSeriaLuckValueSource;
        }

        internal static bool ShouldSendObtainedItemsPopupForBoosterResponse(ushort responseType)
        {
            return responseType != 0x00D0 && responseType != 0x03F3;
        }

        internal static bool ShouldSendObtainedItemsPopupForBoosterResponse(ushort responseType, BoosterUseResult result)
        {
            if (responseType == 0x00D0)
                return false;

            if (responseType == 0x03F3)
                return !ShouldUseNativeMagicBoxBatchAck(result);

            return true;
        }

        private static string FormatPacketHead(byte[] body, int maxBytes)
        {
            if (body == null || body.Length == 0)
                return string.Empty;

            var count = Math.Min(body.Length, Math.Max(0, maxBytes));
            return BitConverter.ToString(body, 0, count);
        }

        private static string FormatBoosterOpenState(BoosterUseResult result)
        {
            if (result == null)
                return string.Empty;

            var state = $" clientType=0x{result.MagicBoxClientType:X2} displayRows={result.DisplayRewards.Count} doubleRows={result.DoubleRewards.Count}";
            if (!result.IsSeriaLuckValueSource)
                return state;

            return state + $" seriaLuck={result.SeriaLuckValueBefore}->{result.SeriaLuckValueAfter}/{result.SeriaLuckValueMax} doubleTriggered={result.SeriaLuckDoubleTriggered}";
        }

        private async Task SendConsumedSourceItemUpdate(EnhancedClientSession session, short sourceSlotIndex, int sourceItemTemplateId)
        {
            var body = ItemListUpdateBuilder.BuildCommonUpdates(new[]
            {
                CreateConsumedSourceItem(sourceSlotIndex, sourceItemTemplateId)
            });
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, body));
        }

        private static byte[] BuildBoosterMainItemUpdates(CharacterItemListSnapshot snapshot, BoosterUseResult result, bool includeSourceUpdate)
        {
            if (snapshot == null || result == null)
                return null;

            var slots = new HashSet<short>();
            foreach (var reward in result.Rewards)
            {
                if (reward.ListType == InventoryListType.Main)
                    slots.Add(reward.SlotIndex);
            }

            if (includeSourceUpdate)
                slots.Add(result.SourceSlotIndex);
            if (result.ConsumedMaterialItemTemplateId > 0)
                slots.Add(result.ConsumedMaterialSlotIndex);

            var updates = new List<CommonInventoryItem>();
            foreach (var slot in slots)
            {
                var item = snapshot.MainItems.FirstOrDefault(x => x.SlotIndex == slot);
                if (item != null)
                {
                    updates.Add(item);
                    continue;
                }

                if (slot == result.SourceSlotIndex)
                    updates.Add(CreateConsumedSourceItem(result));
                else if (slot == result.ConsumedMaterialSlotIndex && result.ConsumedMaterialItemTemplateId > 0)
                    updates.Add(CreateConsumedSourceItem(result.ConsumedMaterialSlotIndex, result.ConsumedMaterialItemTemplateId));
            }

            if (updates.Count == 0)
                return null;

            return ItemListUpdateBuilder.BuildCommonUpdates(updates);
        }

        private static byte[] BuildGrantedMainItemUpdates(
            CharacterItemListSnapshot snapshot,
            IReadOnlyList<PackageGrantedItem> grantedItems,
            short sourceSlotIndex,
            int sourceItemTemplateId,
            bool includeSourceUpdate)
        {
            if (snapshot == null || grantedItems == null)
                return null;

            var slots = new HashSet<short>();
            if (includeSourceUpdate)
                slots.Add(sourceSlotIndex);
            foreach (var reward in grantedItems)
            {
                if (reward.ListType == InventoryListType.Main)
                    slots.Add(reward.SlotIndex);
            }

            var updates = new List<CommonInventoryItem>();
            foreach (var slot in slots)
            {
                var item = snapshot.MainItems.FirstOrDefault(x => x.SlotIndex == slot);
                if (item != null)
                {
                    updates.Add(item);
                    continue;
                }

                if (slot == sourceSlotIndex)
                    updates.Add(CreateConsumedSourceItem(sourceSlotIndex, sourceItemTemplateId));
            }

            if (updates.Count == 0)
                return null;

            return ItemListUpdateBuilder.BuildCommonUpdates(updates);
        }

        private async Task SendAvatarOrPetUpdateListForGrantedItems(EnhancedClientSession session, IReadOnlyList<PackageGrantedItem> grantedItems)
        {
            if (grantedItems == null)
                return;

            var avatarSlots = new HashSet<short>();
            var petSlots = new HashSet<short>();
            foreach (var item in grantedItems)
            {
                if (item.ListType == InventoryListType.Avatar)
                    avatarSlots.Add(item.SlotIndex);
                else if (item.ListType == InventoryListType.Pet)
                    petSlots.Add(item.SlotIndex);
            }

            if (avatarSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Avatar, avatarSlots);
            if (petSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Pet, petSlots);
        }

        private async Task SendAvatarOrPetUpdateListForBoosterRewards(EnhancedClientSession session, BoosterUseResult result)
        {
            if (result?.Rewards == null)
                return;

            var avatarSlots = new HashSet<short>();
            var petSlots = new HashSet<short>();
            foreach (var item in result.Rewards)
            {
                if (item.ListType == InventoryListType.Avatar)
                    avatarSlots.Add(item.SlotIndex);
                else if (item.ListType == InventoryListType.Pet)
                    petSlots.Add(item.SlotIndex);
            }

            if (avatarSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Avatar, avatarSlots);
            if (petSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Pet, petSlots);
        }

        private static CommonInventoryItem CreateConsumedSourceItem(BoosterUseResult result)
        {
            return CreateConsumedSourceItem(result.SourceSlotIndex, result.SourceItemTemplateId);
        }

        private static CommonInventoryItem CreateConsumedSourceItem(short slotIndex, int itemTemplateId)
        {
            return new CommonInventoryItem
            {
                SlotIndex = slotIndex,
                ItemTemplateId = -1,
                CountOrInstanceValue = 0,
            };
        }

        private static string FormatBoosterRows(BoosterUseResult result, bool singleAckRows)
        {
            if (result == null)
                return string.Empty;

            if (singleAckRows)
                return $"ack={FormatBoosterRewardRows(result.Rewards, 16)} display={FormatPackageRows(result.DisplayRewards, 16)} double={FormatPackageRows(result.DoubleRewards, 16)}";

            return $"ack={FormatPackageRows(result.DisplayRewards, 16)} double={FormatPackageRows(result.DoubleRewards, 16)}";
        }

        private static string FormatPackageRows(IReadOnlyList<PackageGrantedItem> rows, int maxRows)
        {
            if (rows == null || rows.Count == 0)
                return "none";

            var limit = Math.Min(rows.Count, Math.Max(0, maxRows));
            var parts = new List<string>();
            for (var i = 0; i < limit; i++)
            {
                var row = rows[i];
                parts.Add($"{row.ListType}:0x{row.ItemTemplateId:X8}x{row.DisplayCount}@{row.SlotIndex}");
            }

            if (rows.Count > limit)
                parts.Add($"...+{rows.Count - limit}");

            return string.Join(",", parts);
        }

        private static string FormatBoosterRewardRows(IReadOnlyList<BoosterRewardResult> rows, int maxRows)
        {
            if (rows == null || rows.Count == 0)
                return "none";

            var limit = Math.Min(rows.Count, Math.Max(0, maxRows));
            var parts = new List<string>();
            for (var i = 0; i < limit; i++)
            {
                var row = rows[i];
                parts.Add($"{row.ListType}:0x{row.ItemTemplateId:X8}x{row.GrantedCount}@{row.SlotIndex}");
            }

            if (rows.Count > limit)
                parts.Add($"...+{rows.Count - limit}");

            return string.Join(",", parts);
        }

        private static IReadOnlyList<int> ParseBoosterSelectionItemIds(byte[] body)
        {
            var selected = new List<int>();
            if (body == null || body.Length < 6)
                return selected;

            AddAlignedInt32Candidates(body, 4, 4, selected);
            if (body.Length >= 3)
                AddRecordCandidates(body, 3, body[2], 5, selected);
            AddAlignedInt32Candidates(body, 2, 4, selected);

            return selected;
        }

        private static IReadOnlyList<int> Parse0207ItemIds(byte[] body)
        {
            var selected = new List<int>();
            if (body == null || body.Length < 3)
                return selected;

            var itemCount = body[2];
            for (var i = 0; i < itemCount; i++)
            {
                var offset = 3 + i * 5;
                if (offset + 4 > body.Length)
                    break;

                AddItemCandidate(BitConverter.ToInt32(body, offset), selected);
            }

            return selected;
        }

        private static void AddAlignedInt32Candidates(byte[] body, int startOffset, int stride, List<int> selected)
        {
            for (var offset = startOffset; offset + 4 <= body.Length; offset += stride)
                AddItemCandidate(BitConverter.ToInt32(body, offset), selected);
        }

        private static void AddRecordCandidates(byte[] body, int startOffset, int count, int recordSize, List<int> selected)
        {
            for (var i = 0; i < count; i++)
            {
                var offset = startOffset + i * recordSize;
                if (offset + 4 > body.Length)
                    break;

                AddItemCandidate(BitConverter.ToInt32(body, offset), selected);
            }
        }

        private static void AddItemCandidate(int itemTemplateId, List<int> selected)
        {
            if (itemTemplateId >= 1000 && !selected.Contains(itemTemplateId))
                selected.Add(itemTemplateId);
        }

        public async Task Handle_COMPOUND_AVATAR(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {

            if (body == null || body.Length < 22)
            {
                var shortErr = new GamePacketWriter();
                shortErr.WriteByte(0x00);
                shortErr.WriteByte(0x16);
                shortErr.WriteByte(0x00);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0063, shortErr.ToArray()));
                return;
            }

            short consumeSlot = BitConverter.ToInt16(body, 0);
            short slot1 = BitConverter.ToInt16(body, 2);
            short slot2 = BitConverter.ToInt16(body, 8);
            int reqItemId = BitConverter.ToInt32(body, 14);

            var (cid, aid) = ResolveOwner(session);
            var job = _characterRepository.GetById(cid)?.Job ?? 0;
            byte newOption = 0;

            if (!_inventoryStore.TryCompoundAvatar(cid, aid, slot1, slot2, consumeSlot,
                    (old1, old2, materialId) =>
                    {
                        var prob = CompoundAvatarProbabilityService.Resolve(job, old1, old2, materialId, reqItemId);
                        return prob.Success ? prob.NewItemIds : new List<int> { reqItemId };
                    },
                    newOption,
                    out List<int> newSlots, out int oldItemId1, out int oldItemId2, out List<int> newItemIds,
                    out int consumedItemTemplateId, out int consumedItemRemainingCount))
            {
                var err = new GamePacketWriter();
                err.WriteByte(0x00);
                err.WriteByte(0x16);
                err.WriteByte(0x00);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0063, err.ToArray()));
                return;
            }

            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteByte(0x03);
            w.WriteByte(0x01);
            w.WriteInt16(slot1);
            w.WriteInt32(1);
            w.WriteByte(0x01);
            w.WriteInt16(slot2);
            w.WriteInt32(1);
            w.WriteByte(0x00);
            w.WriteInt16(consumeSlot);
            w.WriteInt32(1);
            for (int i = 0; i < 2; i++)
            {
                bool hasItem = i < newItemIds.Count;
                w.WriteInt16(hasItem ? (short)newSlots[i] : (short)-1);
                w.WriteInt32(hasItem ? newItemIds[i] : 0);
                w.WriteInt32(0);
                w.WriteInt16(newOption);
                w.WriteInt32(30);
                w.WriteZeroBytes(30);
                w.WriteInt32(4);
                w.WriteZeroBytes(4);
            }

            var respBody = w.ToArray();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0063, respBody));
        }


        public async Task Handle_COMPOUND_AVATAR_SET(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 72)
                return;

            short consumeStackableSlot = body[13];
            int requestedItemId = BitConverter.ToInt32(body, 16);
            short option = BitConverter.ToInt16(body, 20);

            var consumeSlots = new short[8];
            var consumeSlotItemIds = new int[8];
            int off = 24;
            for (int i = 0; i < 8; i++)
            {
                consumeSlots[i] = BitConverter.ToInt16(body, off);
                consumeSlotItemIds[i] = BitConverter.ToInt32(body, off + 2);
                off += 6;
            }

            if (consumeSlots.Distinct().Count() != consumeSlots.Length)
            {
                var dupErr = new GamePacketWriter();
                dupErr.WriteByte(0x00);
                dupErr.WriteByte(0x16);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x03EA, dupErr.ToArray()));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            var job = _characterRepository.GetById(cid)?.Job ?? 0;

            int ResolveNewItemId(int consumeMaterialId)
            {
                var cube = AbsoluteBindCubeService.Resolve(consumeMaterialId, job);
                if (!cube.Success)
                {
                    return -1;
                }

                foreach (var kv in cube.PartToItemId)
                {
                    if (kv.Value == requestedItemId)
                        return requestedItemId;
                }
                return -1;
            }

            if (!_inventoryStore.TryCompoundAvatarSet(cid, aid, consumeSlots, consumeSlotItemIds, ResolveNewItemId, (byte)option,
                    consumeStackableSlot, out int newSlot, out _, out int newItemId, out _, out _))
            {
                var err = new GamePacketWriter();
                err.WriteByte(0x00);
                err.WriteByte(0x16);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x03EA, err.ToArray()));
                return;
            }

            var w2 = new GamePacketWriter();
            w2.WriteByte(0x01);
            w2.WriteByte(0x01); w2.WriteByte(0x00); w2.WriteByte(0x03); w2.WriteByte(0x00);
            w2.WriteByte(0x01); w2.WriteByte(0x00); w2.WriteByte(0x00); w2.WriteByte(0x00);
            w2.WriteInt16((short)newSlot);
            w2.WriteInt32(newItemId);
            w2.WriteInt16((short)option);
            w2.WriteInt16(1);
            for (int i = 0; i < 8; i++)
                w2.WriteInt16(consumeSlots[i]);
            w2.WriteZeroBytes(24);

            var respBody2 = w2.ToArray();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x03EA, respBody2));

        }
    }
}
