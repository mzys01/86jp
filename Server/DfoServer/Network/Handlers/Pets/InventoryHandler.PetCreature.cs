using DfoServer.Game.Inventory;
using DfoServer.Game.Names;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Text;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_RENAME_CREATURE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] RENAME_CREATURE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
            if (!TryParsePetCreatureRenameRequest(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            if (!NameInputValidator.TryValidateRawName(request.NameBytes, minBytes: 0, maxBytes: 13, out _, out var nameFailure))
            {
                FileLogger.Log($"[{ProtocolName}] RENAME_CREATURE invalid name reason={nameFailure} name={DecodePetCreatureNameForLog(request.NameBytes)}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    CommonPacketBodyBuilder.BuildCmdError(NameInputValidator.InvalidNameErrorCode)));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!_sqliteSelectCharacterDataSource.TryRenameEquippedPetCreature(cid, aid, request, out var result))
            {
                FileLogger.Log($"[{ProtocolName}] RENAME_CREATURE failed source=({request.SourceListType},{request.SourceSlotIndex}) name={DecodePetCreatureNameForLog(request.NameBytes)}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            await SendCreatureRenameNoti(session, result);
            // 0x0065 refreshes the equipped creature label; 0x000E refreshes the pet inventory slot text.
            await _refresh.SendUpdateItemList(session, InventoryListType.Pet, result.SourceSlotIndex);
        }

        private static bool TryParsePetCreatureRenameRequest(byte[] body, out PetCreatureRenameRequest request)
        {
            request = null;
            if (body == null || body.Length < 7)
                return false;

            var sourceSlot = BitConverter.ToInt16(body, 0);
            var sourceListType = (InventoryListType)body[2];
            var nameLength = BitConverter.ToInt32(body, 3);
            if (nameLength < 0 || nameLength > 13 || body.Length < 7 + nameLength)
                return false;

            var nameBytes = new byte[nameLength];
            if (nameLength > 0)
                Buffer.BlockCopy(body, 7, nameBytes, 0, nameLength);

            request = new PetCreatureRenameRequest
            {
                SourceListType = sourceListType,
                SourceSlotIndex = sourceSlot,
                NameBytes = nameBytes,
            };
            return true;
        }

        private static async Task SendCreatureRenameNoti(EnhancedClientSession session, PetCreatureRenameResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(session?.Player?.UserId ?? 0);
            writer.WriteRawDstr(result?.NameBytes);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0065, writer.ToArray()));
        }

        private static string DecodePetCreatureNameForLog(byte[] nameBytes)
        {
            if (nameBytes == null || nameBytes.Length == 0)
                return string.Empty;

            try
            {
                return Encoding.UTF8.GetString(nameBytes);
            }
            catch
            {
                return BitConverter.ToString(nameBytes);
            }
        }
    }
}
