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

            if (!_sqliteSelectCharacterDataSource.TryDeleteItem(cid, aid, listType, slotIndex, 1, out var result))
            {
                FileLogger.Log($"[{ProtocolName}] USE_STACKABLE: failed to consume item 0x{itemCode:X8} at listType={listType} slot={slotIndex}");
                var errBody = UseStackableAckBuilder.BuildError((byte)listType, itemCode, instanceValue);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x002C, errBody));
                return;
            }


            var ackBody = UseStackableAckBuilder.BuildSuccess(slotIndex, (byte)listType, instanceValue, itemCode);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x002C, ackBody));

            FileLogger.Log($"[{ProtocolName}] USE_STACKABLE: consumed 1x item 0x{itemCode:X8} from slot {slotIndex}, remaining={result.RemainingStackCount}");
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
                if (_sqliteSelectCharacterDataSource.TryOpenAvatarPackage(cid, aid, request, out var result))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0207, AvatarPackageAckBuilder.BuildSuccess(result.SlotIndex)));
                    if (result.GrantedItems.Count > 0)
                    {
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                            SelectablePackageAckBuilder.BuildSuccess(result.SlotIndex, result.GrantedItems)));
                    }

                    if (result.SourceRemainingStackCount <= 0)
                        await SendConsumedSourceItemUpdate(session, result.SlotIndex, result.PackageItemTemplateId);

                    var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
                    var mainUpdateBody = BuildGrantedMainItemUpdates(snapshot, result.GrantedItems, result.SlotIndex, result.PackageItemTemplateId, result.SourceRemainingStackCount > 0);
                    if (mainUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, mainUpdateBody));

                    var petUpdateBody = BuildGrantedPetItemUpdates(snapshot, result.GrantedItems);
                    if (petUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, petUpdateBody));

                    var avatarUpdateBody = BuildGrantedAvatarItemUpdates(snapshot, result.GrantedItems);
                    if (avatarUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, avatarUpdateBody));

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
                if (_sqliteSelectCharacterDataSource.TryOpenSelectablePackage(cid, aid, request, out var result))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0, SelectablePackageAckBuilder.BuildSuccess(result.SlotIndex, result.GrantedItems)));

                    if (result.SourceRemainingStackCount <= 0)
                        await SendConsumedSourceItemUpdate(session, result.SlotIndex, result.PackageItemTemplateId);

                    var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
                    var mainUpdateBody = BuildGrantedMainItemUpdates(snapshot, result.GrantedItems, result.SlotIndex, result.PackageItemTemplateId, result.SourceRemainingStackCount > 0);
                    if (mainUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, mainUpdateBody));

                    var petUpdateBody = BuildGrantedPetItemUpdates(snapshot, result.GrantedItems);
                    if (petUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, petUpdateBody));

                    var avatarUpdateBody = BuildGrantedAvatarItemUpdates(snapshot, result.GrantedItems);
                    if (avatarUpdateBody != null)
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, avatarUpdateBody));

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
            if (!_sqliteSelectCharacterDataSource.TryUseBoosterItem(
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

            await SendBoosterUseResult(session, header.type, result);
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} requested={request.RequestedCount} applied={result.ConsumedSourceCount} remaining={result.SourceRemainingStackCount} material=0x{result.ConsumedMaterialItemTemplateId:X8}x{result.ConsumedMaterialCount}@{result.ConsumedMaterialSlotIndex} materialRemaining={result.ConsumedMaterialRemainingStackCount} rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))} elapsed={elapsed.ElapsedMilliseconds}ms");
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
            if (!_sqliteSelectCharacterDataSource.TryUseBoosterItem(
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

            await SendBoosterUseResult(session, header.type, result);
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} requested={request.RequestedCount} applied={result.ConsumedSourceCount} remaining={result.SourceRemainingStackCount} material=0x{result.ConsumedMaterialItemTemplateId:X8}x{result.ConsumedMaterialCount}@{result.ConsumedMaterialSlotIndex} materialRemaining={result.ConsumedMaterialRemainingStackCount} rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))} elapsed={elapsed.ElapsedMilliseconds}ms");
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

            if (slotIndex == 0 && header.type == 0x0218)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, CommonPacketBodyBuilder.BuildSuccessAck()));
                FileLogger.Log($"[{ProtocolName}] USE_BOOSTER: confirm ack type=0x{header.type:X4}");
                return true;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryUseBoosterItem(cid, aid, new BoosterUseRequest
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

        private async Task<bool> TryHandleOpenPackage0207(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 3)
                return false;

            var slotIndex = BitConverter.ToInt16(body, 0);
            var selectedItemTemplateIds = Parse0207ItemIds(body);
            FileLogger.Log($"[{ProtocolName}] OPEN_PACKAGE_0207 raw({body.Length}B): {BitConverter.ToString(body)} slot={slotIndex} selected={string.Join(",", selectedItemTemplateIds.Select(id => $"0x{id:X8}"))}");

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryOpenPackage0207(cid, aid, slotIndex, selectedItemTemplateIds, out var result))
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
            var grantedItems = ToPackageGrantedItems(result);

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
            else if (!ShouldSendSourceAckForBoosterResponse(responseType))
            {
                if (grantedItems.Count > 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                        SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
                }
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

            var snapshot = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
            var mainUpdateBody = BuildBoosterMainItemUpdates(snapshot, result, result.SourceRemainingStackCount > 0);
            if (mainUpdateBody != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, mainUpdateBody));

            var petUpdateBody = BuildBoosterPetItemUpdates(snapshot, result);
            if (petUpdateBody != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, petUpdateBody));

            var avatarUpdateBody = BuildBoosterAvatarItemUpdates(snapshot, result);
            if (avatarUpdateBody != null)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, avatarUpdateBody));
        }

        internal static bool ShouldSendSourceAckForBoosterResponse(ushort responseType)
        {
            return responseType != 0x00D0 && responseType != 0x03F3;
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

        private static byte[] BuildBoosterPetItemUpdates(CharacterItemListSnapshot snapshot, BoosterUseResult result)
        {
            if (snapshot == null || result == null)
                return null;

            return BuildPetItemUpdates(snapshot, CollectBoosterRewardSlots(result.Rewards, InventoryListType.Pet));
        }

        private static byte[] BuildBoosterAvatarItemUpdates(CharacterItemListSnapshot snapshot, BoosterUseResult result)
        {
            if (snapshot == null || result == null)
                return null;

            return BuildAvatarItemUpdates(snapshot, CollectBoosterRewardSlots(result.Rewards, InventoryListType.Avatar));
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

        private static byte[] BuildGrantedPetItemUpdates(CharacterItemListSnapshot snapshot, IReadOnlyList<PackageGrantedItem> grantedItems)
        {
            if (snapshot == null || grantedItems == null)
                return null;

            return BuildPetItemUpdates(snapshot, CollectGrantedItemSlots(grantedItems, InventoryListType.Pet));
        }

        private static byte[] BuildGrantedAvatarItemUpdates(CharacterItemListSnapshot snapshot, IReadOnlyList<PackageGrantedItem> grantedItems)
        {
            if (snapshot == null || grantedItems == null)
                return null;

            return BuildAvatarItemUpdates(snapshot, CollectGrantedItemSlots(grantedItems, InventoryListType.Avatar));
        }

        private static HashSet<short> CollectBoosterRewardSlots(IEnumerable<BoosterRewardResult> rewards, InventoryListType listType)
        {
            var slots = new HashSet<short>();
            if (rewards == null)
                return slots;

            foreach (var reward in rewards)
            {
                if (reward.ListType == listType)
                    slots.Add(reward.SlotIndex);
            }

            return slots;
        }

        private static HashSet<short> CollectGrantedItemSlots(IEnumerable<PackageGrantedItem> grantedItems, InventoryListType listType)
        {
            var slots = new HashSet<short>();
            if (grantedItems == null)
                return slots;

            foreach (var item in grantedItems)
            {
                if (item.ListType == listType)
                    slots.Add(item.SlotIndex);
            }

            return slots;
        }

        private static byte[] BuildPetItemUpdates(CharacterItemListSnapshot snapshot, HashSet<short> slots)
        {
            if (snapshot == null || slots == null || slots.Count == 0)
                return null;

            var updates = new List<PetInventoryItem>();
            foreach (var slot in slots)
            {
                var item = snapshot.PetItems.FirstOrDefault(x => x.SlotIndex == slot);
                if (item != null)
                    updates.Add(item);
            }

            if (updates.Count == 0)
                return null;

            return ItemListUpdateBuilder.BuildPetUpdates(updates);
        }

        private static byte[] BuildAvatarItemUpdates(CharacterItemListSnapshot snapshot, HashSet<short> slots)
        {
            if (snapshot == null || slots == null || slots.Count == 0)
                return null;

            var updates = new List<AvatarInventoryItem>();
            foreach (var slot in slots)
            {
                var item = snapshot.AvatarItems.FirstOrDefault(x => x.SlotIndex == slot);
                if (item != null)
                    updates.Add(item);
            }

            if (updates.Count == 0)
                return null;

            return ItemListUpdateBuilder.BuildAvatarUpdates(updates);
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
                ItemTemplateId = itemTemplateId,
                CountOrInstanceValue = 0,
            };
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

            if (!_sqliteSelectCharacterDataSource.TryCompoundAvatar(cid, aid, slot1, slot2, consumeSlot,
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

            if (!_sqliteSelectCharacterDataSource.TryCompoundAvatarSet(cid, aid, consumeSlots, consumeSlotItemIds, ResolveNewItemId, (byte)option,
                    consumeStackableSlot, out int newSlot, out var oldItemIds, out int newItemId, out int consumedTemplateId, out int consumedRemaining))
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

            if (consumedTemplateId > 0)
            {
                var consumeItem = new CommonInventoryItem
                {
                    SlotIndex = consumeStackableSlot,
                    ItemTemplateId = consumedRemaining > 0 ? consumedTemplateId : -1,
                    CountOrInstanceValue = consumedRemaining,
                };
                var consumeUpd = ItemListUpdateBuilder.BuildCommonUpdates(new List<CommonInventoryItem> { consumeItem });
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, consumeUpd));
            }
        }
    }
}
