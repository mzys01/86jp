using DfoServer.Game.Inventory;
using System;
using System.Text.Json.Nodes;

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
            Console.WriteLine("=== INVENTORY_EXTRA_VIEW selftest ===");

            var prefix = new byte[8];
            BitConverter.GetBytes(0x01020304).CopyTo(prefix, 0);
            prefix[4] = 7;
            prefix[5] = 2;
            BitConverter.GetBytes((ushort)0x3456).CopyTo(prefix, 6);

            var middle = new byte[17];
            middle[0] = 2;
            WriteChronicle(middle, 1, 0x11223344, 1, 2, 3, 4);
            WriteChronicle(middle, 9, 0x55667788, 5, 6, 7, 8);

            var jewel = new byte[30];
            for (var index = 0; index < 5; index++)
            {
                var offset = index * 6;
                BitConverter.GetBytes((ushort)(0x20 + index)).CopyTo(jewel, offset);
                BitConverter.GetBytes(0x01020304u + (uint)index).CopyTo(jewel, offset + 2);
            }

            var tail = new byte[37];
            tail[0] = 2;
            BitConverter.GetBytes(0x01020304u).CopyTo(tail, 1);
            BitConverter.GetBytes(0x05060708u).CopyTo(tail, 5);
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
            var extraJson = "{"
                + "\"extData0\":109"
                + ",\"prefixData0E\":\"" + Hex(prefix) + "\""
                + ",\"middleData1A\":\"" + Hex(middle) + "\""
                + ",\"tailData2F\":\"" + Hex(tail) + "\""
                + ",\"jewelSocket\":\"" + Hex(jewel) + "\""
                + ",\"unknown\":\"keep\""
                + "}";

            var view = ItemExtraView.Parse(extraJson);
            Check("raw attr 保留 extData0", view.Raw84.Attr == 109);
            Check("装备 upgrade/reseal 按 bit 拆分", view.Equipment.Upgrade == 13 && view.Equipment.ReSealCount == 3);
            Check("prefix enchant card id", view.Equipment.EnchantCardId == 0x01020304);
            Check("prefix enchant upgrade count", view.Equipment.EnchantUpgradeCount == 7);
            Check("prefix amplify type/value", view.Equipment.AmplifyType == 2 && view.Equipment.AmplifyValue == 0x3456);
            Check("tail emblem data 解析", view.Equipment.EmblemData.Length == 9
                && view.Equipment.EmblemData[0] == 2
                && BitConverter.ToUInt32(view.Equipment.EmblemData, 1) == 0x01020304u
                && BitConverter.ToUInt32(view.Equipment.EmblemData, 5) == 0x05060708u);
            Check("tail rune 解析", view.Equipment.Rune == 0x4567);
            Check("tail seal 解析", view.Equipment.SealCount == 2
                && view.Equipment.SealTypes[0] == 0x11
                && view.Equipment.SealTypes[1] == 0x12
                && view.Equipment.SealVal1s[0] == 0x21
                && view.Equipment.SealVal1s[1] == 0x22
                && view.Equipment.SealVal2s[0] == 0x31
                && view.Equipment.SealVal2s[1] == 0x32
                && view.Equipment.SealTail.Length == 2
                && view.Equipment.SealTail[0] == 0x41
                && view.Equipment.SealTail[1] == 0xFF);
            Check("tail forging 解析", view.Equipment.Forging == 0x55);
            Check("middle 异界气息最多两组", view.Equipment.ChronicleOptions.Count == 2);
            Check("middle 第一组语义", view.Equipment.ChronicleOptions[0].OptionId == 0x11223344
                && view.Equipment.ChronicleOptions[0].CharacJob == 1
                && view.Equipment.ChronicleOptions[0].FirstGrowType == 2
                && view.Equipment.ChronicleOptions[0].EquipmentType == 3
                && view.Equipment.ChronicleOptions[0].OptionNo == 4);
            Check("middle 第二组语义", view.Equipment.ChronicleOptions[1].OptionId == 0x55667788
                && view.Equipment.ChronicleOptions[1].CharacJob == 5
                && view.Equipment.ChronicleOptions[1].FirstGrowType == 6
                && view.Equipment.ChronicleOptions[1].EquipmentType == 7
                && view.Equipment.ChronicleOptions[1].OptionNo == 8);
            Check("jewel socket 固定五组", view.Equipment.JewelSockets.Count == 5);
            Check("jewel socket 第五组语义", view.Equipment.JewelSockets[4].SocketType == 0x24
                && view.Equipment.JewelSockets[4].EmblemItemId == 0x01020308u);

            var zeroChronicle = ItemExtraView.Parse("{\"middleData1A\":\"00\"}");
            Check("middle 长度 1 表示无异界气息", zeroChronicle.Equipment.MiddleData1A.Length == 1
                && zeroChronicle.Equipment.ChronicleOptions.Count == 0
                && zeroChronicle.Raw84.MiddleData1A.Length == 17);

            var oneChronicleBytes = new byte[9];
            oneChronicleBytes[0] = 1;
            WriteChronicle(oneChronicleBytes, 1, 0x12345678, 9, 8, 7, 6);
            var oneChronicle = ItemExtraView.Parse("{\"middleData1A\":\"" + Hex(oneChronicleBytes) + "\"}");
            Check("middle 长度 9 表示一组异界气息", oneChronicle.Equipment.MiddleData1A.Length == 9
                && oneChronicle.Equipment.ChronicleOptions.Count == 1
                && oneChronicle.Equipment.ChronicleOptions[0].OptionId == 0x12345678);

            Check("serialize 保留未知字段", view.Serialize().Contains("\"unknown\":\"keep\"", StringComparison.Ordinal));
            var merged = new JsonObject { ["existing"] = 1 };
            view.MergeInto(merged);
            Check("merge 保留目标并合入 extra_json", merged["existing"]?.ToString() == "1"
                && merged["unknown"]?.ToString() == "keep");

            var builder = new ItemExtraViewBuilder();
            builder.Equipment.ExtData0 = 109;
            builder.Equipment.EnchantCardId = 0x01020304;
            builder.Equipment.EnchantUpgradeCount = 7;
            builder.Equipment.AmplifyType = 2;
            builder.Equipment.AmplifyValue = 0x3456;
            builder.Equipment.EmblemData = Slice(tail, 0, 9);
            builder.Equipment.Rune = 0x4567;
            builder.Equipment.SealCount = 2;
            builder.Equipment.SealTypes = new byte[] { 0x11, 0x12, 0 };
            builder.Equipment.SealVal1s = new byte[] { 0x21, 0x22, 0 };
            builder.Equipment.SealVal2s = new byte[] { 0x31, 0x32, 0 };
            builder.Equipment.SealTail = new byte[] { 0x41, 0xFF };
            builder.Equipment.Forging = 0x55;
            builder.Equipment.JewelSocket = jewel;
            var builtJson = builder.Build().Serialize();
            var expectedBuiltJson = "{\"extData0\":109"
                + ",\"prefixData0E\":\"" + Hex(prefix) + "\""
                + ",\"tailData2F\":\"" + Hex(tail) + "\""
                + ",\"jewelSocket\":\"" + Hex(jewel) + "\"}";
            Check("builder 输出旧 hand-build JSON 形态", builtJson == expectedBuiltJson);
            var builtView = ItemExtraView.Parse(builtJson);
            Check("builder 输出可 roundtrip 为装备语义", builtView.Equipment.Forging == 0x55
                && builtView.Equipment.Rune == 0x4567
                && builtView.Equipment.SealCount == 2
                && builtView.Equipment.JewelSockets[4].EmblemItemId == 0x01020308u);

            var upgradeBuilder = ItemExtraViewBuilder.FromView(view);
            upgradeBuilder.Equipment.Upgrade = 14;
            var upgradedView = upgradeBuilder.Build();
            Check("from-view builder 只改 upgrade 并保留 reseal", upgradedView.Equipment.Upgrade == 14
                && upgradedView.Equipment.ReSealCount == 3);
            Check("from-view builder 保留 prefix/middle/tail/jewel 原始字段", Hex(upgradedView.Raw84.PrefixData0E) == Hex(prefix)
                && Hex(upgradedView.Raw84.MiddleData1A) == Hex(middle)
                && Hex(upgradedView.Raw84.TailData2F) == Hex(tail)
                && Hex(upgradedView.Raw84.JewelSocket) == Hex(jewel));
            Check("from-view builder 写回 extData0 仅替换低5位", upgradedView.Raw84.Attr == ((109 & 0xE0) | 14));

            var enchantBuilder = ItemExtraViewBuilder.FromView(view);
            enchantBuilder.Equipment.EnchantCardId = 0x10203040;
            enchantBuilder.Equipment.EnchantUpgradeCount = 9;
            var enchantedView = enchantBuilder.Build();
            var expectedEnchantPrefix = Slice(prefix, 0, prefix.Length);
            BitConverter.GetBytes(0x10203040).CopyTo(expectedEnchantPrefix, 0);
            expectedEnchantPrefix[4] = 9;
            Check("from-view builder 附魔只改 prefix 卡片和升级次数", Hex(enchantedView.Raw84.PrefixData0E) == Hex(expectedEnchantPrefix)
                && enchantedView.Equipment.AmplifyType == 2
                && enchantedView.Equipment.AmplifyValue == 0x3456);
            Check("from-view builder 附魔保留 ext/middle/tail/jewel", enchantedView.Raw84.Attr == 109
                && Hex(enchantedView.Raw84.MiddleData1A) == Hex(middle)
                && Hex(enchantedView.Raw84.TailData2F) == Hex(tail)
                && Hex(enchantedView.Raw84.JewelSocket) == Hex(jewel));

            var noJewelBuilder = new ItemExtraViewBuilder();
            noJewelBuilder.Equipment.Upgrade = 3;
            Check("builder 无镶嵌 raw 时不写 jewelSocket", !noJewelBuilder.Build().Serialize().Contains("jewelSocket", StringComparison.Ordinal));

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
                ExtraJson = extraJson,
            };
            var common = InventoryProtocolMapper.ToCommonItem(record, view);
            Check("common mapper 使用 record 基础字段", common.SlotIndex == 10
                && common.ItemTemplateId == 123456
                && common.CountOrInstanceValue == 987
                && common.Durability == 4321
                && common.EquipmentLockId == 5);
            Check("common mapper 使用 extra_json 协议字段", Hex(common.PrefixData0E) == Hex(prefix)
                && Hex(common.MiddleData1A) == Hex(middle)
                && Hex(common.TailData2F) == Hex(tail)
                && Hex(common.JewelSocket) == Hex(jewel));

            var avatar = InventoryProtocolMapper.ToAvatarItem(new SqliteInventoryStore.ItemRecord
            {
                SlotIndex = 2,
                ItemTemplateId = 223344,
                OptionValue = 11,
                Marker16 = 30,
                ExtraJson = "{\"reserved0\":\"" + Hex(Sequence(5, 1)) + "\""
                    + ",\"reserved1\":\"" + Hex(Sequence(71, 2)) + "\""
                    + ",\"reserved2\":\"" + Hex(Sequence(30, 3)) + "\""
                    + ",\"unknownFixed4\":4"
                    + ",\"tailData\":\"" + Hex(Sequence(7, 4)) + "\"}",
            }, null);
            Check("avatar mapper 使用 view 字段", avatar.AvatarItemId == 223344
                && avatar.OptionValue == 11
                && avatar.UnknownFixed30 == 30
                && avatar.UnknownFixed4 == 4
                && avatar.Reserved2.Length == 30
                && avatar.TailData.Length == 7);

            var pet = InventoryProtocolMapper.ToPetItem(new SqliteInventoryStore.ItemRecord
            {
                SlotIndex = 7,
                ItemTemplateId = 334455,
                PetSerialOrHandle = 998877,
                ExtraJson = "{\"tailData0A\":\"" + Hex(Sequence(74, 5)) + "\"}",
            }, null);
            Check("pet mapper 使用 view 字段", pet.CreatureItemId == 334455
                && pet.CreatureSerialOrHandle == 998877
                && pet.TailData0A.Length == 74
                && pet.TailData0A[0] == 5);

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static void WriteChronicle(byte[] target, int offset, int optionId, byte characJob, byte firstGrowType, byte equipmentType, byte optionNo)
        {
            BitConverter.GetBytes(optionId).CopyTo(target, offset);
            target[offset + 4] = characJob;
            target[offset + 5] = firstGrowType;
            target[offset + 6] = equipmentType;
            target[offset + 7] = optionNo;
        }

        private static byte[] Sequence(int length, byte start)
        {
            var data = new byte[length];
            for (var index = 0; index < length; index++)
                data[index] = unchecked((byte)(start + index));
            return data;
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            var data = new byte[length];
            Buffer.BlockCopy(source, offset, data, 0, length);
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
