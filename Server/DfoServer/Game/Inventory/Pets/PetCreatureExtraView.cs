using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace DfoServer.Game.Inventory
{
    internal sealed class PetCreatureExtraView
    {
        private const int CommonPrefixData0EBaseOffset = 0x0E;
        private const int PetTailData0ABaseOffset = 0x0A;
        private const int PetTailData0ALength = 74;
        private const int PetEnchantCardIdOffset = 0x0E;
        private const int PetEnchantUpgradeCountOffset = 0x12;
        private const int PetPermanentBindOffset = 0x4B;
        private const int PetTradeRestrictionOffset = 0x4C;
        private const int PetSealRemainUseCountOffset = 0x52;
        private const int CommonTailData2FBaseOffset = 0x2F;
        private const int CommonPrefixEnchantCardIdIndex = PetEnchantCardIdOffset - CommonPrefixData0EBaseOffset;
        private const int CommonPrefixEnchantUpgradeCountIndex = PetEnchantUpgradeCountOffset - CommonPrefixData0EBaseOffset;
        private const int CommonTailPermanentBindIndex = PetPermanentBindOffset - CommonTailData2FBaseOffset;
        private const int CommonTailTradeRestrictionIndex = PetTradeRestrictionOffset - CommonTailData2FBaseOffset;
        private const int CommonTailRemainUseCountIndex = PetSealRemainUseCountOffset - CommonTailData2FBaseOffset;
        private const int CommonTailRemainUseCountCompatIndex = CommonTailRemainUseCountIndex - 1;
        private const int PetTailEnchantCardIdIndex = PetEnchantCardIdOffset - PetTailData0ABaseOffset;
        private const int PetTailEnchantUpgradeCountIndex = PetEnchantUpgradeCountOffset - PetTailData0ABaseOffset;
        private const int PetTailPermanentBindIndex = PetPermanentBindOffset - PetTailData0ABaseOffset;
        private const int PetTailTradeRestrictionIndex = PetTradeRestrictionOffset - PetTailData0ABaseOffset;
        private const int PetTailRemainUseCountIndex = PetSealRemainUseCountOffset - PetTailData0ABaseOffset;
        private const int PetTailRemainUseCountCompatIndex = PetTailRemainUseCountIndex - 1;
        private const byte PetCharacterPermanentBind = 1;
        private const byte PetTradeRestrictionNone = 0;
        private const byte PetTradeRestrictionExhausted = 1;
        private const string PetSealRemainUseCountInitializedProperty = "petSealRemainUseCountInitialized";
        private const string PetSealRemainUseCountProperty = "petSealRemainUseCount";
        private const string PetEnchantCardItemIdProperty = "petEnchantCardItemId";
        private const string PetEnchantUpgradeCountProperty = "petEnchantUpgradeCount";

        private readonly JsonObject _json;
        private readonly byte[] _tailData0A;

        private PetCreatureExtraView(JsonObject json, byte[] tailData0A)
        {
            _json = json ?? new JsonObject();
            _tailData0A = InventoryItemViewBytes.CopyFixed(tailData0A, PetTailData0ALength);
        }

        internal static PetCreatureExtraView Parse(string extraJson)
        {
            var json = ParseJsonObject(extraJson);
            var pet = InventoryItemView.ForPet(new SqliteInventoryStore.ItemRecord
            {
                ExtraJson = string.IsNullOrWhiteSpace(extraJson) ? "{}" : extraJson,
            });
            return new PetCreatureExtraView(json, pet.PetTailData0A);
        }

        internal string ToJsonString()
        {
            NormalizeSealFieldAliases();
            _json["tailData0A"] = InventoryItemViewBytes.ToHex(_tailData0A);
            return _json.ToJsonString();
        }

        internal bool HasProtocolTail()
        {
            if (HasProtocolMarker(_json))
                return true;

            for (var index = 0; index < _tailData0A.Length; index++)
                if (_tailData0A[index] != 0)
                    return true;

            return false;
        }

        internal void SetEnchant(int enchantCardItemId, byte enchantUpgradeCount)
        {
            BitConverter.GetBytes(enchantCardItemId).CopyTo(_tailData0A, PetTailEnchantCardIdIndex);
            _tailData0A[PetTailEnchantUpgradeCountIndex] = enchantUpgradeCount;
            _json[PetEnchantCardItemIdProperty] = enchantCardItemId;
            _json[PetEnchantUpgradeCountProperty] = enchantUpgradeCount;
        }

        internal bool TryGetEnchant(out uint enchantCardItemId, out byte enchantUpgradeCount)
        {
            enchantCardItemId = 0;
            enchantUpgradeCount = 0;

            var hasCard = TryReadJsonInt(_json, PetEnchantCardItemIdProperty, out var directCardItemId);
            var hasUpgrade = TryReadJsonInt(_json, PetEnchantUpgradeCountProperty, out var directUpgradeCount);
            if (hasCard || hasUpgrade)
            {
                if (hasCard)
                    enchantCardItemId = unchecked((uint)directCardItemId);
                if (hasUpgrade)
                    enchantUpgradeCount = ClampByte(directUpgradeCount);

                if ((!hasCard || !hasUpgrade)
                    && TryReadEnchantFromTail(out var tailCardItemId, out var tailUpgradeCount))
                {
                    if (!hasCard)
                        enchantCardItemId = tailCardItemId;
                    if (!hasUpgrade)
                        enchantUpgradeCount = tailUpgradeCount;
                }

                return true;
            }

            return TryReadEnchantFromTail(out enchantCardItemId, out enchantUpgradeCount);
        }

        internal void InitializeSealRemainUseCount(byte remainUseCount)
        {
            var tradeRestriction = remainUseCount <= 0
                ? PetTradeRestrictionExhausted
                : PetTradeRestrictionNone;
            _json[PetSealRemainUseCountInitializedProperty] = true;
            _json[PetSealRemainUseCountProperty] = remainUseCount;
            if (tradeRestriction != 0 && ReadTailByte(PetTailPermanentBindIndex) == 0)
                WriteTailByte(PetTailPermanentBindIndex, PetCharacterPermanentBind);
            WriteTailByte(PetTailTradeRestrictionIndex, tradeRestriction);
            WriteTailByte(PetTailRemainUseCountIndex, remainUseCount);
            ClearGeneratedRemainUseCountCompat(remainUseCount);
        }

        internal bool TryGetSealRemainUseCount(out byte remainUseCount)
        {
            remainUseCount = 0;
            if (_tailData0A.Length <= PetTailRemainUseCountIndex)
                return false;

            if (TryReadJsonInt(_json, PetSealRemainUseCountProperty, out var direct))
            {
                remainUseCount = ClampByte(direct);
                return true;
            }

            if (HasSealRemainUseCountInitialized(_json))
            {
                remainUseCount = ReadTailAlias(PetTailRemainUseCountIndex, PetTailRemainUseCountCompatIndex);
                return true;
            }

            remainUseCount = ReadTailAlias(PetTailRemainUseCountIndex, PetTailRemainUseCountCompatIndex);
            if (remainUseCount > 0)
            {
                return true;
            }

            return false;
        }

        internal void ApplyEnchantToCommonPrefix(byte[] commonPrefixData0E)
        {
            if (commonPrefixData0E == null || commonPrefixData0E.Length <= CommonPrefixEnchantUpgradeCountIndex)
                return;

            if (!TryGetEnchant(out var enchantCardItemId, out var enchantUpgradeCount))
                return;

            BitConverter.GetBytes(unchecked((int)enchantCardItemId)).CopyTo(commonPrefixData0E, CommonPrefixEnchantCardIdIndex);
            commonPrefixData0E[CommonPrefixEnchantUpgradeCountIndex] = enchantUpgradeCount;
        }

        internal void ApplySealFieldsToCommonTail(byte[] commonTailData2F)
        {
            if (commonTailData2F == null || commonTailData2F.Length <= CommonTailRemainUseCountIndex)
                return;

            if (TryGetPermanentBind(out var permanentBind))
                WriteTargetByte(commonTailData2F, CommonTailPermanentBindIndex, permanentBind);

            if (TryGetSealTradeRestriction(out var tradeRestriction))
                WriteTargetByte(commonTailData2F, CommonTailTradeRestrictionIndex, tradeRestriction);

            if (TryGetSealRemainUseCount(out var remainUseCount))
                WriteTargetAliases(commonTailData2F, CommonTailRemainUseCountIndex, CommonTailRemainUseCountCompatIndex, remainUseCount);
        }

        private bool TryReadEnchantFromTail(out uint enchantCardItemId, out byte enchantUpgradeCount)
        {
            enchantCardItemId = 0;
            enchantUpgradeCount = 0;
            if (_tailData0A.Length <= PetTailEnchantUpgradeCountIndex)
                return false;

            enchantCardItemId = BitConverter.ToUInt32(_tailData0A, PetTailEnchantCardIdIndex);
            enchantUpgradeCount = _tailData0A[PetTailEnchantUpgradeCountIndex];
            return enchantCardItemId != 0 || enchantUpgradeCount != 0;
        }

        private void NormalizeSealFieldAliases()
        {
            if (TryGetPermanentBind(out var permanentBind))
                WriteTailByte(PetTailPermanentBindIndex, permanentBind);

            if (TryGetSealTradeRestriction(out var tradeRestriction))
                WriteTailByte(PetTailTradeRestrictionIndex, tradeRestriction);

            if (TryGetSealRemainUseCount(out var remainUseCount))
            {
                WriteTailByte(PetTailRemainUseCountIndex, remainUseCount);
                ClearGeneratedRemainUseCountCompat(remainUseCount);
            }
        }

        private bool TryGetSealTradeRestriction(out byte tradeRestriction)
        {
            tradeRestriction = 0;

            if (TryGetSealRemainUseCount(out var remainUseCount))
            {
                tradeRestriction = remainUseCount <= 0
                    ? PetTradeRestrictionExhausted
                    : PetTradeRestrictionNone;
                return true;
            }

            tradeRestriction = ReadTailByte(PetTailTradeRestrictionIndex);
            return tradeRestriction != 0;
        }

        private bool TryGetPermanentBind(out byte permanentBind)
        {
            permanentBind = ReadTailByte(PetTailPermanentBindIndex);
            if (permanentBind != 0)
                return true;

            if (TryGetSealRemainUseCount(out var remainUseCount) && remainUseCount <= 0)
            {
                permanentBind = PetCharacterPermanentBind;
                return true;
            }

            return false;
        }

        private void ClearGeneratedRemainUseCountCompat(byte remainUseCount)
        {
            if (ReadTailByte(PetTailRemainUseCountCompatIndex) == remainUseCount
                && HasSealRemainUseCountInitialized(_json))
            {
                WriteTailByte(PetTailRemainUseCountCompatIndex, 0);
            }
        }

        private byte ReadTailAlias(int primaryIndex, int compatIndex)
        {
            var primary = ReadTailByte(primaryIndex);
            return primary != 0 ? primary : ReadTailByte(compatIndex);
        }

        private byte ReadTailByte(int index)
        {
            return index >= 0 && _tailData0A.Length > index ? _tailData0A[index] : (byte)0;
        }

        private void WriteTailAliases(int primaryIndex, int compatIndex, byte value)
        {
            WriteTailByte(primaryIndex, value);
            WriteTailByte(compatIndex, value);
        }

        private static void WriteTargetAliases(byte[] target, int primaryIndex, int compatIndex, byte value)
        {
            WriteTargetByte(target, primaryIndex, value);
            WriteTargetByte(target, compatIndex, value);
        }

        private static void WriteTargetByte(byte[] target, int index, byte value)
        {
            if (target != null && index >= 0 && target.Length > index)
                target[index] = value;
        }

        private void WriteTailByte(int index, byte value)
        {
            if (index >= 0 && _tailData0A.Length > index)
                _tailData0A[index] = value;
        }

        private static JsonObject ParseJsonObject(string jsonText)
        {
            if (!string.IsNullOrWhiteSpace(jsonText))
            {
                try
                {
                    if (JsonNode.Parse(jsonText) is JsonObject json)
                        return json;
                }
                catch
                {
                }
            }

            return new JsonObject();
        }

        internal static bool TryReadJsonObject(string jsonText, out JsonObject json)
        {
            json = null;
            if (string.IsNullOrWhiteSpace(jsonText))
                return false;

            try
            {
                json = JsonNode.Parse(jsonText) as JsonObject;
                return json != null;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryReadJsonInt(JsonObject json, string propertyName, out int value)
        {
            value = 0;
            if (json == null || !json.TryGetPropertyValue(propertyName, out var node) || node == null)
                return false;

            try
            {
                value = node.GetValue<int>();
                return true;
            }
            catch
            {
                return int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }
        }

        private static bool HasProtocolMarker(JsonObject json)
        {
            return json != null
                && (json.ContainsKey(PetSealRemainUseCountInitializedProperty)
                    || json.ContainsKey(PetSealRemainUseCountProperty)
                    || json.ContainsKey(PetEnchantCardItemIdProperty));
        }

        private static bool HasSealRemainUseCountInitialized(JsonObject json)
        {
            if (json == null)
                return false;

            if (json.ContainsKey(PetSealRemainUseCountProperty))
                return true;

            if (!json.TryGetPropertyValue(PetSealRemainUseCountInitializedProperty, out var node) || node == null)
                return false;

            try
            {
                return node.GetValue<bool>();
            }
            catch
            {
                var text = node.ToString();
                return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "1", StringComparison.Ordinal);
            }
        }

        internal static byte ClampByte(int value)
        {
            if (value <= 0)
                return 0;
            return value >= byte.MaxValue ? byte.MaxValue : (byte)value;
        }
    }
}
