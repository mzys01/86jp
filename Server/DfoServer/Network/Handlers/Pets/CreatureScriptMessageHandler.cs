using System;
using System.Text;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class CreatureScriptMessageHandler
    {
        private const ushort CreatureScriptMessageNoti = 0x0077;
        private const int MaxCreatureScriptTextBytes = 256;

        private readonly Func<byte[], Task> _broadcastGamePacket;

        public CreatureScriptMessageHandler(Func<byte[], Task> broadcastGamePacket = null)
        {
            _broadcastGamePacket = broadcastGamePacket;
        }

        public async Task Handle_ENUM_CMDPACKET_CREATURE_SCRIPT_MESSAGE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!TryParseCreatureScriptMessage(body, out var request))
            {
                FileLogger.Log($"[GameProtocol] CREATURE_SCRIPT_MESSAGE invalid body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                return;
            }

            var senderUniqueId = session?.Player?.UserId ?? 0;
            if (senderUniqueId == 0 && session?.Player?.CharacterId > 0)
                senderUniqueId = (ushort)session.Player.CharacterId;

            var packet = GamePacketEnvelopeBuilder.Build(
                0x00,
                CreatureScriptMessageNoti,
                BuildCreatureScriptMessageNotiBody(request, senderUniqueId, serverGroup: 0));

            // The old Taiwan server routes this command through
            // GameWorld::send_chat_msg(..., NOTI 0x0077). Mode 3 is an area
            // message. This server currently only has port-level broadcast, not
            // a strict same-screen area set, so fall back to self-send when no
            // broadcast callback is available.
            if (_broadcastGamePacket != null && ShouldBroadcastCreatureScriptMessage(request))
                await _broadcastGamePacket(packet);
            else
                await session.SendPacketAsync(packet);

            FileLogger.Log(
                $"[GameProtocol] CREATURE_SCRIPT_MESSAGE mode={request.Mode} target={request.TargetUniqueId} " +
                $"char={request.CharacterId} len={request.MessageBytes.Length} text={DecodeForLog(request.MessageBytes)}");
        }

        private static bool ShouldBroadcastCreatureScriptMessage(CreatureScriptMessageRequest request)
        {
            return request.Mode == 3;
        }

        private static byte[] BuildCreatureScriptMessageNotiBody(
            CreatureScriptMessageRequest request,
            ushort senderUniqueId,
            byte serverGroup)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(request.Mode);
            writer.WriteUInt16(senderUniqueId);
            writer.WriteByte(serverGroup);
            writer.WriteInt32(request.MessageBytes.Length);
            writer.WriteBytes(request.MessageBytes);
            return writer.ToArray();
        }

        private static bool TryParseCreatureScriptMessage(byte[] body, out CreatureScriptMessageRequest request)
        {
            request = null;
            if (body == null || body.Length < 11)
                return false;

            var mode = body[0];
            var targetUniqueId = BitConverter.ToUInt16(body, 1);
            var characterId = BitConverter.ToUInt32(body, 3);
            var messageLength = BitConverter.ToInt32(body, 7);

            if (messageLength < 0 || messageLength > MaxCreatureScriptTextBytes || body.Length < 11 + messageLength)
                return false;

            var messageBytes = new byte[messageLength];
            if (messageLength > 0)
                Buffer.BlockCopy(body, 11, messageBytes, 0, messageLength);

            request = new CreatureScriptMessageRequest
            {
                Mode = mode,
                TargetUniqueId = targetUniqueId,
                CharacterId = characterId,
                MessageBytes = messageBytes,
            };
            return true;
        }

        private static string DecodeForLog(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return BitConverter.ToString(bytes);
            }
        }

        private sealed class CreatureScriptMessageRequest
        {
            public byte Mode { get; set; }
            public ushort TargetUniqueId { get; set; }
            public uint CharacterId { get; set; }
            public byte[] MessageBytes { get; set; } = Array.Empty<byte>();
        }
    }
}
