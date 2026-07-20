using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public static class ResetItemAttrRequestParser
    {
        public const int BodyLength = 8;
        private const short MaxPlausibleMainSlot = 500;

        // Some 86JP launches miss the client-side CipherEncrypt bypass and
        // send this command XORed with the legacy eight-byte mask. Keep the
        // compatibility decode local to 0x0051 instead of changing framing
        // for every command.
        private static readonly byte[] LegacyCipherMask =
        {
            0xD1, 0x3C, 0x82, 0x7C, 0xA1, 0x5A, 0x43, 0x0F,
        };

        public static bool TryParse(byte[] body, out ResetItemAttrRequest request)
        {
            request = null;
            if (body == null || body.Length != BodyLength)
                return false;

            var targetSlotIndex = BitConverter.ToInt16(body, 0);
            var targetItemTemplateId = BitConverter.ToInt32(body, 2);
            var materialSlotIndex = BitConverter.ToInt16(body, 6);
            if (targetSlotIndex < 0 || materialSlotIndex < 0 || targetSlotIndex == materialSlotIndex || targetItemTemplateId <= 0)
                return false;

            request = new ResetItemAttrRequest
            {
                TargetSlotIndex = targetSlotIndex,
                TargetItemTemplateId = targetItemTemplateId,
                MaterialSlotIndex = materialSlotIndex,
            };
            return true;
        }

        public static bool TryParseCompatible(
            byte[] body,
            out ResetItemAttrRequest request,
            out bool decodedLegacyCipher)
        {
            decodedLegacyCipher = false;
            var rawParsed = TryParse(body, out var rawRequest);
            if (rawParsed && HasPlausibleSlots(rawRequest))
            {
                request = rawRequest;
                return true;
            }

            if (TryDecodeLegacyCipher(body, out var decodedRequest))
            {
                request = decodedRequest;
                decodedLegacyCipher = true;
                return true;
            }

            request = rawRequest;
            return rawParsed;
        }

        private static bool TryDecodeLegacyCipher(byte[] body, out ResetItemAttrRequest request)
        {
            request = null;
            if (body == null || body.Length != BodyLength)
                return false;

            var decoded = new byte[BodyLength];
            for (var index = 0; index < decoded.Length; index++)
                decoded[index] = (byte)(body[index] ^ LegacyCipherMask[index]);

            return TryParse(decoded, out request) && HasPlausibleSlots(request);
        }

        private static bool HasPlausibleSlots(ResetItemAttrRequest request)
        {
            return request != null
                && request.TargetSlotIndex <= MaxPlausibleMainSlot
                && request.MaterialSlotIndex <= MaxPlausibleMainSlot;
        }
    }
}
