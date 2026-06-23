using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class TownHandler
    {
        private static readonly TimeSpan PositionPersistThrottle = TimeSpan.FromSeconds(5);

        private readonly ICharacterRepository _characterRepository;
        private readonly SqliteSelectCharacterDataSource _sqliteSelectCharacterDataSource;

        public string ProtocolName => "GameProtocol";

        public TownHandler(ICharacterRepository characterRepository, SqliteSelectCharacterDataSource sqliteSelectCharacterDataSource)
        {
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _sqliteSelectCharacterDataSource = sqliteSelectCharacterDataSource ?? throw new ArgumentNullException(nameof(sqliteSelectCharacterDataSource));
        }

        /// <summary>
        /// </summary>
        public void PersistPosition(EnhancedClientSession session, bool forceImmediate, string source)
        {
            try
            {
                if (session?.Player == null || session.Player.CharacterId <= 0)
                    return;

                var now = DateTime.UtcNow;
                if (!forceImmediate)
                {
                    if (now - session.Player.LastPositionPersistAt < PositionPersistThrottle)
                        return;
                }

                var gate = GameWorld.Town.GetCeraRoomInfo(session.Player.CurTownId);
                if (gate.Town <= 0)
                    return;

                _characterRepository.UpdatePosition(
                    session.Player.CharacterId,
                    session.Player.CurTownId,
                    session.Player.CurAreaId,
                    session.Player.CurPosX,
                    session.Player.CurPosY,
                    session.Player.CurDirection,
                    session.Player.CurAreaState);
                session.Player.LastPositionPersistAt = now;
                FileLogger.Log($"[{ProtocolName}] Persisted position ({source}) character_id={session.Player.CharacterId} town={session.Player.CurTownId} area={session.Player.CurAreaId} pos=({session.Player.CurPosX},{session.Player.CurPosY})");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] Persist position ({source}) failed: {ex.Message}");
            }
        }

        public Task Handle_ENUM_CMDPACKET_SET_USER_POSITION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4) return Task.CompletedTask;
            var gotoPosX = BitConverter.ToInt16(body, 0);
            var gotoPosY = BitConverter.ToInt16(body, 2);
            session.Player.CurPosX = gotoPosX;
            session.Player.CurPosY = gotoPosY;
            PersistPosition(session, forceImmediate: false, source: "set_user_position");
            return Task.CompletedTask;
        }

        public async Task Handle_ENUM_CMDPACKET_SET_USER_AREA(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 6) return;
            var gotoTownId = body[0];
            var gotoAreaId = body[1];
            var gotoPosX = BitConverter.ToInt16(body, 2);
            var gotoPosY = BitConverter.ToInt16(body, 4);

            session.Player.CurTownId = gotoTownId;
            session.Player.CurAreaId = gotoAreaId;
            session.Player.CurPosX = gotoPosX;
            session.Player.CurPosY = gotoPosY;
            session.Player.CurDirection = 0x05;
            session.Player.CurAreaState = 0x03;

            var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017, TownAreaNotificationBuilder.BuildUserArea(snapshot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018, TownAreaNotificationBuilder.BuildAreaUsers(snapshot)));

            PersistPosition(session, forceImmediate: true, source: "set_user_area");
        }

        public async Task Handle_ENUM_CMDPACKET_FINISH_LOADING(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0025, CommonPacketBodyBuilder.BuildSuccessAck()));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001E, FinishLoadingBuilder.BuildNotification()));

        }

        public async Task Handle_ENUM_CMDPACKET_TELEPORT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 8)
                return;

            var type = BitConverter.ToInt16(body, 0);
            var itemCode = BitConverter.ToInt32(body, 2);
            if (itemCode != 0x0027AC4E)
                return;

            var townId = body[7];
            var ceraRoomInfo = Town.GetCeraRoomInfo(townId);
            session.Player.CurTownId = ceraRoomInfo.Town;
            session.Player.CurAreaId = ceraRoomInfo.Area;
            session.Player.CurPosX = ceraRoomInfo.X;
            session.Player.CurPosY = ceraRoomInfo.Y;
            session.Player.CurDirection = 0;
            session.Player.CurAreaState = 3;

            var (cid, aid) = InventoryHandler.ResolveOwner(session);
            int remainingCount = 0;
            var itemList = _sqliteSelectCharacterDataSource.LoadItemListSnapshot(cid, aid);
            short targetSlot = -1;
            foreach (var item in itemList.MainItems)
            {
                if (item.ItemTemplateId == itemCode)
                {
                    targetSlot = item.SlotIndex;
                    remainingCount = item.CountOrInstanceValue;
                    break;
                }
            }

            if (targetSlot >= 0)
            {
                if (_sqliteSelectCharacterDataSource.TryDeleteItem(cid, aid, InventoryListType.Main, targetSlot, 1, out var result))
                {
                    remainingCount = remainingCount > 0 ? remainingCount - 1 : 0;
                    FileLogger.Log($"[{ProtocolName}] TELEPORT: consumed 1x teleport item, remaining={remainingCount}");
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018, TownAreaNotificationBuilder.BuildAreaUsers(TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player))));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000E, TeleportPacketBuilder.BuildItemListUpdate(type, itemCode, remainingCount)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00ED, TeleportPacketBuilder.BuildTeleportResponse(type, itemCode)));

            PersistPosition(session, forceImmediate: true, source: "teleport");
        }

        public async Task Handle_ENUM_CMDPACKET_GIVEUP_GAME(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            DungeonHandler.ResetDungeonState(session);
            session.Player.UserState = 0x00;

            var list = new List<byte>();
            list.Add(session.Player.CurTownId);
            list.Add(session.Player.CurAreaId);
            list.AddRange(BitConverter.GetBytes(session.Player.CurPosX));
            list.AddRange(BitConverter.GetBytes(session.Player.CurPosY));
            list.Add(session.Player.CurDirection);
            list.Add(session.Player.CurTownId);
            list.Add(session.Player.CurAreaState);
            list.Add(session.Player.CurAreaId);
            await Handle_ENUM_CMDPACKET_SET_USER_AREA(session, header, list.ToArray());
        }
    }
}
