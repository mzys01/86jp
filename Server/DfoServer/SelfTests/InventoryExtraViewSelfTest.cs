using DfoServer.Game.Inventory;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using System;

namespace DfoServer.SelfTests
{
    public static class InventoryExtraViewSelfTest
    {
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== INVENTORY_ITEM_VIEW selftest ===");

            var prefix = new byte[8];
            BitConverter.GetBytes(0x01020304).CopyTo(prefix, 0);
            prefix[4] = 7;
            prefix[5] = 2;
            BitConverter.GetBytes((ushort)0x3456).CopyTo(prefix, 6);

            var middle = new byte[17];
            middle[0] = 2;
            WriteChronicle(middle, 0, 0x11223344, 1, 2, 3, 4);
            WriteChronicle(middle, 1, 0x55667788, 5, 6, 7, 8);

            var jewel = new byte[30];
            for (var index = 0; index < 5; index++)
            {
                var offset = index * 6;
                BitConverter.GetBytes((ushort)(0x20 + index)).CopyTo(jewel, offset);
                BitConverter.GetBytes(0x01020304u + (uint)index).CopyTo(jewel, offset + 2);
            }

            var tail = new byte[37];
            tail[0] = 2;
            BitConverter.GetBytes(0x01020304).CopyTo(tail, 1);
            BitConverter.GetBytes(0x05060708).CopyTo(tail, 5);
            BitConverter.GetBytes((ushort)0x4567).CopyTo(tail, 9);
            tail[11] = 2;
            tail[12] = 0x11;
            tail[13] = 0x12;
            tail[15] = 0x21;
            tail[16] = 0x22;
            tail[18] = 0x31;
            tail[19] = 0x32;
            tail[21] = 0x41;
            tail[22] = 0xFF;
            tail[27] = 0x55;

            var record = new SqliteInventoryStore.ItemRecord
            {
                SlotIndex = 10,
                ItemTemplateId = 123456,
                StackCount = 987,
                Durability = 4321,
                SealFlag = 6,
                ExpireTime = 7654321,
                Marker16 = -1,
                EquipmentLockId = 5,
                ExtraJson = "{"
                    + "\"extData0\":109"
                    + ",\"prefixData0E\":\"" + Hex(prefix) + "\""
                    + ",\"middleData1A\":\"" + Hex(middle) + "\""
                    + ",\"tailData2F\":\"" + Hex(tail) + "\""
                    + ",\"jewelSocket\":\"" + Hex(jewel) + "\""
                    + ",\"unknown\":\"keep\""
                    + "}",
            };

            var view = InventoryItemView.ForCommon(record);
            Check("common view 保留 record 基础字段", view.Entry84.SlotIndex == 10
                && view.Entry84.ItemTemplateId == 123456
                && view.Entry84.Value == 987
                && view.Entry84.Durability == 4321
                && view.Entry84.SealFlag == 6
                && view.Entry84.Marker16 == -1
                && view.Entry84.ExpireTime == 7654321);
            Check("common view 解析强化和再封装", view.Upgrade == 13 && view.ReSealCount == 3);
            Check("common view 解析附魔和增幅", view.EnchantCardId == 0x01020304
                && view.EnchantUpgradeCount == 7
                && view.AmplifyType == 2
                && view.AmplifyValue == 0x3456);
            Check("common view 解析徽章和符文", view.Entry84.EmblemSocketCount == 2
                && view.Entry84.EmblemId1 == 0x01020304
                && view.Entry84.EmblemId2 == 0x05060708
                && view.Entry84.EmblemData.Length == 9
                && view.Entry84.Rune == 0x4567);
            Check("common view 解析魔法封印", view.Entry84.RandomOptionCount == 2
                && view.Entry84.RandomOption0Type == 0x11
                && view.Entry84.RandomOption1Type == 0x12
                && view.Entry84.RandomOption0Value1 == 0x21
                && view.Entry84.RandomOption1Value1 == 0x22
                && view.Entry84.RandomOption0Value2 == 0x31
                && view.Entry84.RandomOption1Value2 == 0x32
                && view.Entry84.RandomOptionState == 0x41
                && view.Entry84.RandomOptionChangedIndex == 0xFF);
            Check("common view 解析锻造和异界气息", view.Forging == 0x55
                && view.Entry84.ChronicleOptions.Count == 2
                && view.Entry84.ChronicleOptions[0].OptionId == 0x11223344
                && view.Entry84.ChronicleOptions[0].CharacJob == 1
                && view.Entry84.ChronicleOptions[0].FirstGrowType == 2
                && view.Entry84.ChronicleOptions[0].EquipmentType == 3
                && view.Entry84.ChronicleOptions[0].OptionNo == 4
                && view.Entry84.ChronicleOptions[1].OptionId == 0x55667788);
            Check("common view 解析五组镶嵌片段", view.Entry84.JewelSockets.Count == 5
                && view.Entry84.JewelSockets[4].SocketType == 0x24
                && view.Entry84.JewelSockets[4].EmblemItemId == 0x01020308u);

            var magicSealOptions = SqliteInventoryStore.ReadMagicSealOptions(tail);
            Check("tail magic seal 原始布局为 type[3]/value1[3]/value2[3]", magicSealOptions.Count == 2
                && magicSealOptions[0].Type == 0x11
                && magicSealOptions[0].Value1 == 0x21
                && magicSealOptions[0].Value2 == 0x31
                && magicSealOptions[1].Type == 0x12
                && magicSealOptions[1].Value1 == 0x22
                && magicSealOptions[1].Value2 == 0x32);

            var protocolItem = InventoryProtocolMapper.ToCommonItem(record);
            Check("common mapper 使用 InventoryItemView 字段", protocolItem.SlotIndex == 10
                && protocolItem.ItemTemplateId == 123456
                && protocolItem.CountOrInstanceValue == 987
                && protocolItem.Durability == 4321
                && Hex(protocolItem.PrefixData0E) == Hex(prefix)
                && Hex(protocolItem.MiddleData1A) == Hex(middle)
                && Hex(protocolItem.TailData2F) == Hex(tail)
                && Hex(protocolItem.JewelSocket) == Hex(jewel));

            view.Upgrade = 10;
            view.Entry84.ReSealCount = 3;
            view.EnchantCardId = 0x12345678;
            view.Forging = 9;
            view.Entry84.EmblemSocketCount = 2;
            view.Entry84.EmblemId1 = 0x11121314;
            view.Entry84.EmblemId2 = 0x21222324;
            view.Durability = 2222;
            var updatedCommon = InventoryItemView.ForCommon(record);
            Check("common view 业务 setter 写回 record/extra_json", updatedCommon.Upgrade == 10
                && updatedCommon.ReSealCount == 3
                && record.Durability == 2222
                && updatedCommon.EnchantCardId == 0x12345678
                && updatedCommon.Forging == 9
                && updatedCommon.Entry84.EmblemSocketCount == 2
                && updatedCommon.Entry84.EmblemId1 == 0x11121314
                && updatedCommon.Entry84.EmblemId2 == 0x21222324
                && record.ExtraJson.Contains("\"unknown\":\"keep\"", StringComparison.Ordinal));

            var sealTail = SqliteInventoryStore.NormalizeMagicSealTail(updatedCommon.Entry84.TailData2F);
            SqliteInventoryStore.WriteMagicSealOptions(sealTail, new[]
            {
                new RandomOptionEntry { Type = 0x31, Value1 = 0x41, Value2 = 0x51 },
                new RandomOptionEntry { Type = 0x32, Value1 = 0x42, Value2 = 0x52 },
                new RandomOptionEntry { Type = 0x33, Value1 = 0x43, Value2 = 0x53 },
            });
            updatedCommon.Entry84.TailData2F = sealTail;
            var updatedSeal = InventoryItemView.ForCommon(record);
            Check("common view 写回魔法封印 tailData2F", updatedSeal.Entry84.RandomOptionCount == 3
                && updatedSeal.Entry84.RandomOption0Type == 0x31
                && updatedSeal.Entry84.RandomOption1Value1 == 0x42
                && updatedSeal.Entry84.RandomOption2Value2 == 0x53
                && updatedSeal.Entry84.RandomOptionState == 0x00
                && updatedSeal.Entry84.RandomOptionChangedIndex == 0xFF);

            var avatarRecord = new SqliteInventoryStore.ItemRecord
            {
                SlotIndex = 2,
                ItemTemplateId = 223344,
                OptionValue = 11,
                Marker16 = 0x1E01,
                ExtraJson = "{\"reserved0\":\"" + Hex(Sequence(5, 1)) + "\""
                    + ",\"reserved1\":\"" + Hex(Sequence(71, 2)) + "\""
                    + ",\"reserved2\":\"" + Hex(Sequence(30, 3)) + "\""
                    + ",\"unknownFixed4\":4"
                    + ",\"tailData\":\"" + Hex(Sequence(7, 4)) + "\"}",
            };
            var avatar = InventoryProtocolMapper.ToAvatarItem(avatarRecord);
            var avatarView = InventoryItemView.ForAvatar(avatarRecord);
            Check("avatar mapper 使用 view 字段", avatar.AvatarItemId == 223344
                && avatar.OptionValue == 11
                && avatar.UnknownFixed30 == 0x1E01
                && avatar.ColorDataLen == 4
                && avatar.AvatarSocketData.Length == 30
                && avatar.TailData.Length == 7);
            Check("avatar view 合成 84 字节和 detail", avatarView.Entry84.Value == 0x04030201
                && avatarView.Entry84.Attr == 5
                && avatarView.Entry84.AbilityNo == 0x020B
                && avatarView.Entry84.SealFlag == 3
                && avatarView.Entry84.Marker16 == BitConverter.ToInt32(Sequence(71, 2), 10)
                && avatarView.Entry84.SortLockFlag == 1
                && avatarView.AvatarDetail.AvatarSocketLen == 30
                && avatarView.AvatarDetail.ColorDataLen == 4
                && avatarView.AvatarDetail.Color1 == 0x0504
                && avatarView.AvatarDetail.Color2 == 0x0706);
            avatarView.AvatarDetail.Socket0.Set(0xFFEF, 0x10203040);
            avatarView.AvatarDetail.Socket1.SocketType = 0x0004;
            avatarView.AvatarDetail.Socket1.EmblemItemId = 0x55667788;
            avatarView.AvatarDetail.Color1 = 0x1111;
            avatarView.AvatarDetail.Color2 = 0x2222;
            var updatedAvatar = InventoryItemView.ForAvatar(avatarRecord);
            Check("avatar detail setter 写回 record extra_json", updatedAvatar.AvatarDetail.Socket0.SocketType == 0xFFEF
                && updatedAvatar.AvatarDetail.Socket0.EmblemItemId == 0x10203040
                && updatedAvatar.AvatarDetail.Socket1.SocketType == 0x0004
                && updatedAvatar.AvatarDetail.Socket1.EmblemItemId == 0x55667788
                && updatedAvatar.AvatarDetail.GetSocket(0).Type == 0xFFEF
                && updatedAvatar.AvatarDetail.Color1 == 0x1111
                && updatedAvatar.AvatarDetail.Color2 == 0x2222);

            var avatarEquippedRaw = MakeEquipListCodec.BuildEntryFromDisplayFields(2, 223344, new MakeEquipListCodec.DisplayFields
            {
                InstanceValue = 0x01020304,
                ExpansionData = new byte[] { 0x04, 0x05, 0x06, 0x07 },
            });
            var avatarEquippedFields = MakeEquipListCodec.ParseDisplayFields(avatarEquippedRaw);
            var avatarEquippedItem = InvenItem.Parse(avatarEquippedRaw);
            var appearanceEntry = new CharacterAppearanceEntry(2, 223344, 4, avatarEquippedItem.Expansion, 0, 0, 0, 0);
            Check("appearance entry expansionData 拆出染色", avatarEquippedFields.ExpansionData.Length == 4
                && Hex(avatarEquippedFields.ExpansionData) == "04050607"
                && Hex(avatarEquippedItem.Expansion) == "04050607"
                && appearanceEntry.Color1 == 0x0504
                && appearanceEntry.Color2 == 0x0706);
            appearanceEntry.Color1 = 0x1111;
            appearanceEntry.Color2 = 0x2222;
            Check("appearance entry 染色 setter 写回 expansionData", Hex(appearanceEntry.ExpansionData) == "11112222");
            Check("appearance state 只使用 attr 低5位强化", AppearanceService.BuildAppearanceState(new InvenItem
            {
                Attr = 0xA5,
                AmplifyType = 1,
            }) == 11);

            var petRecord = new SqliteInventoryStore.ItemRecord
            {
                SlotIndex = 7,
                ItemTemplateId = 334455,
                PetSerialOrHandle = 998877,
                ExtraJson = "{\"tailData0A\":\"" + Hex(Sequence(74, 5)) + "\"}",
            };
            var pet = InventoryProtocolMapper.ToPetItem(petRecord);
            var petView = InventoryItemView.ForPet(petRecord);
            Check("pet mapper 使用 view 字段", pet.CreatureItemId == 334455
                && pet.CreatureSerialOrHandle == 998877
                && pet.CreatureUid == 998877
                && pet.Attr == 5
                && pet.Durability == 0x0706
                && pet.SealFlag == 8
                && pet.EnchantCardId == 0x0C0B0A09
                && pet.EnchantUpgradeCount == 13
                && pet.AmplifyType == 14
                && pet.AmplifyValue == 0x100F
                && pet.Marker16 == 0x14131211
                && pet.GenuineUpgrade == 69
                && pet.TradeRestriction == 71
                && pet.RemainUseCount == 77
                && pet.SortLockFlag == 78
                && pet.TailData0A.Length == 74);
            Check("pet view 拆出 common84 tail", petView.Entry84.Attr == 5
                && petView.Entry84.Durability == 0x0706
                && petView.Entry84.SealFlag == 8
                && petView.Entry84.PrefixData0E[0] == 9
                && petView.Entry84.Marker16 == 0x14131211
                && petView.Entry84.MiddleData1A.Length == 17
                && petView.Entry84.TailData2F.Length == 37
                && petView.Entry84.TailData2F[0] == 42
                && petView.Entry84.GenuineUpgrade == 69
                && petView.Entry84.EmancipateEquipmentLevel == 70
                && petView.Entry84.TradeRestriction == 71
                && petView.Entry84.RemainUseCount == 77
                && petView.Entry84.SortLock == 78);

            var equippedRaw = MakeEquipListCodec.BuildEntryFromDisplayFields(11, 445566, new MakeEquipListCodec.DisplayFields
            {
                InstanceValue = 0x01020304,
                Reinforce = 12,
                Durability = 321,
                SealFlag = 1,
                Enchant = 0x11223344,
                EnchantUpgradeCount = 5,
                AmplifyType = 2,
                AmplifyValue = 0x4567,
                ChronicleOptions = new[]
                {
                    new MakeEquipListCodec.ChronicleOptionFields { OptionId = 0x10203040, CharacJob = 1, FirstGrowType = 2, EquipmentType = 3, OptionNo = 4 },
                    new MakeEquipListCodec.ChronicleOptionFields { OptionId = 0x50607080, CharacJob = 5, FirstGrowType = 6, EquipmentType = 7, OptionNo = 8 },
                },
                ExpireTime = 0x01020304,
                EmblemSocketCount = 2,
                EmblemId1 = 0x11121314,
                EmblemId2 = 0x21222324,
                Rune = 0x7788,
                Forging = 9,
                EmancipateEquipmentLevel = 2,
                TradeRestriction = 3,
                TailUnknown0 = 0x4567,
                TailUnknown1 = 0x68,
                TailUnknown2 = 0x69,
                TailUnknown3 = 0x6A,
                RemainUseCount = 0x6B,
                SortLockFlag = 1,
                MagicSealCount = 3,
                MagicSealTypes = new byte[] { 0x31, 0x32, 0x33 },
                MagicSealVal1s = new byte[] { 0x41, 0x42, 0x43 },
                MagicSealVal2s = new byte[] { 0x51, 0x52, 0x53 },
                RandomOptionState = 0x61,
                RandomOptionChangedIndex = 1,
                RandomOptionChangeState = 0x62,
                RandomOptionChangeType = 0x63,
                RandomOptionChangeValue1 = 0x64,
                RandomOptionChangeValue2 = 0x65,
            });
            var equippedFields = MakeEquipListCodec.ParseDisplayFields(equippedRaw);
            var roundtripDiff = InvenItem.VerifyRoundTrip(equippedRaw, out var equippedItem);
            Check("raw_entry 可变长穿戴 entry 可 roundtrip", roundtripDiff < 0
                && equippedItem.Slot == 11
                && equippedItem.ItemId == 445566
                && equippedItem.Value == 0x01020304
                && equippedItem.Seals.Count == 3);
            Check("raw_entry 魔法封印为穿戴变长三元组布局", equippedFields.MagicSealCount == 3
                && equippedFields.MagicSealTypes[0] == 0x31
                && equippedFields.MagicSealVal1s[0] == 0x41
                && equippedFields.MagicSealVal2s[0] == 0x51
                && equippedFields.MagicSealTypes[1] == 0x32
                && equippedFields.MagicSealVal1s[1] == 0x42
                && equippedFields.MagicSealVal2s[1] == 0x52
                && equippedItem.Seals[2].Type == 0x33
                && equippedItem.Seals[2].Val1 == 0x43
                && equippedItem.Seals[2].Val2 == 0x53
                && equippedItem.SealGenuineUpgrade == 0x61
                && equippedItem.SealCheck == 1
                && equippedItem.SealExtra == 0x65646362);
            Check("raw_entry 穿戴动态块拆分语义", equippedFields.ChronicleOptions.Length == 2
                && equippedFields.ChronicleOptions[0].OptionId == 0x10203040
                && equippedFields.ChronicleOptions[1].OptionNo == 8
                && equippedFields.ExpireTime == 0x01020304
                && equippedFields.EmblemSocketCount == 2
                && equippedFields.EmblemId1 == 0x11121314
                && equippedFields.EmblemId2 == 0x21222324
                && equippedFields.RandomOptionState == 0x61
                && equippedFields.RandomOptionChangedIndex == 1
                && equippedFields.RandomOptionChangeState == 0x62
                && equippedFields.RandomOptionChangeType == 0x63
                && equippedFields.RandomOptionChangeValue1 == 0x64
                && equippedFields.RandomOptionChangeValue2 == 0x65
                && equippedFields.TailUnknown0 == 0x4567
                && equippedFields.TailUnknown1 == 0x68
                && equippedFields.TailUnknown2 == 0x69
                && equippedFields.TailUnknown3 == 0x6A
                && equippedFields.RemainUseCount == 0x6B);
            Check("raw_entry 穿戴尾部保留排序锁", equippedFields.SortLockFlag == 1
                && equippedFields.SealFlag == 1
                && equippedFields.EmancipateEquipmentLevel == 2
                && equippedFields.TradeRestriction == 3
                && equippedRaw[equippedRaw.Length - 1] == 1);
            var equippedDisplayRaw = SqliteSubtype1Repository.ClearEquippedSortLockForClient(equippedRaw);
            var equippedDisplayFields = MakeEquipListCodec.ParseDisplayFields(equippedDisplayRaw);
            Check("subtype1 装备栏展示 raw 清除排序锁", equippedDisplayFields.SortLockFlag == 0
                && equippedDisplayFields.Forging == 9
                && equippedDisplayFields.EmancipateEquipmentLevel == 2
                && equippedDisplayFields.TradeRestriction == 3
                && equippedDisplayFields.RemainUseCount == 0x6B
                && equippedRaw[equippedRaw.Length - 1] == 1);
            var equippedView = EquippedItemView.FromRecord(new MakeEquipListCodec.Entry
            {
                Slot = 11,
                ItemId = 445566,
                Raw = equippedRaw,
                EquipmentLockId = 6,
            });
            Check("equipped view 投影 raw_entry 语义", equippedView.Slot == 11
                && equippedView.ItemId == 445566
                && equippedView.Value == 0x01020304
                && equippedView.Attr == 12
                && equippedView.Durability == 321
                && equippedView.SealFlag == 1
                && equippedView.EnchantCardId == 0x11223344
                && equippedView.EnchantUpgradeCount == 5
                && equippedView.AmplifyType == 2
                && equippedView.AmplifyValue == 0x4567
                && equippedView.EquipmentLockId == 6
                && equippedView.Entry84.SortLockFlag == 1
                && equippedView.Entry84.Forging == 9
                && equippedView.Entry84.EmancipateEquipmentLevel == 2
                && equippedView.Entry84.TradeRestriction == 3
                && equippedView.Entry84.RemainUseCount == 0x6B
                && equippedView.Entry84.Rune == 0x7788);
            equippedView.SortLockFlag = 0;
            equippedView.EmancipateEquipmentLevel = 4;
            equippedView.TradeRestriction = 5;
            equippedView.Entry84.RemainUseCount = 6;
            equippedView.SealFlag = 0;
            equippedView.EquipmentLockId = 7;
            var updatedEquippedFields = MakeEquipListCodec.ParseDisplayFields(equippedView.Record.Raw);
            Check("equipped view 写回 raw_entry", equippedView.Record.Raw[equippedView.Record.Raw.Length - 1] == 0
                && updatedEquippedFields.SortLockFlag == 0
                && updatedEquippedFields.EmancipateEquipmentLevel == 4
                && updatedEquippedFields.TradeRestriction == 5
                && updatedEquippedFields.RemainUseCount == 6
                && updatedEquippedFields.SealFlag == 0
                && equippedView.EquipmentLockId == 7);

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static void WriteChronicle(byte[] target, int index, int optionId, byte characJob, byte firstGrowType, byte equipmentType, byte optionNo)
        {
            BitConverter.GetBytes(optionId).CopyTo(target, 1 + index * 4);
            target[9 + index] = characJob;
            target[11 + index] = firstGrowType;
            target[13 + index] = equipmentType;
            target[15 + index] = optionNo;
        }

        private static byte[] Sequence(int length, byte start)
        {
            var data = new byte[length];
            for (var index = 0; index < length; index++)
                data[index] = unchecked((byte)(start + index));
            return data;
        }

        private static string Hex(byte[] data)
        {
            return BitConverter.ToString(data ?? Array.Empty<byte>()).Replace("-", string.Empty);
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++;
            else _fail++;
        }

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }
    }
}
