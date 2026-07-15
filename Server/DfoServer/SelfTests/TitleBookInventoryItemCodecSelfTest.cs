using DfoServer.Game.Inventory;
using DfoServer.Game.TitleBook;
using System;

namespace DfoServer.SelfTests
{
    public static class TitleBookInventoryItemCodecSelfTest
    {
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== TITLEBOOK_ITEM_CODEC selftest ===");

            var chronicle = new TitleBookChronicleData { Count = 2 };
            chronicle.Options.Add(new TitleBookChronicleOption
            {
                OptionId = 0x11223344,
                CharacJob = 1,
                FirstGrowType = 2,
                EquipmentType = 3,
                OptionNo = 4,
            });
            chronicle.Options.Add(new TitleBookChronicleOption
            {
                OptionId = 0x55667788,
                CharacJob = 5,
                FirstGrowType = 6,
                EquipmentType = 7,
                OptionNo = 8,
            });

            var middle = TitleBookInventoryItemCodec.EncodeChronicle(chronicle);
            var tail = Sequence(37, 0x40);

            var record = new SqliteInventoryStore.ItemRecord
            {
                SlotIndex = 12,
                ItemTemplateId = 400330051,
                StackCount = 400330051,
                Durability = 1234,
                SealFlag = 2,
                Marker16 = -1,
                ExpireTime = 0,
                EquipmentLockId = 9,
                ExtraJson = "{}",
            };
            var seedView = InventoryItemView.ForCommon(record);
            seedView.Attr = 0x6D;
            seedView.EnchantCardId = 0x01020304;
            seedView.EnchantUpgradeCount = 7;
            seedView.AmplifyType = 2;
            seedView.AmplifyValue = 0x3456;
            seedView.Entry84.MiddleData1A = middle;
            seedView.Entry84.TailData2F = tail;
            seedView.Entry84.JewelSocket = Sequence(30, 0xA0);

            var title = TitleBookInventoryItemCodec.FromItemRecord(0, 3, record);
            Check("record 转称号簿基础字段", title.Category == 0
                && title.BookIndex == 3
                && title.Slot == 12
                && title.ItemId == record.ItemTemplateId
                && title.Value == record.StackCount
                && title.EquipmentLockId == 9);
            Check("record 转称号簿 prefix 语义", title.Attr == 0x6D
                && title.EnchantIndex == 0x01020304
                && title.EnchantUpgradeCount == 7
                && title.AmplifyType == 2
                && title.AmplifyValue == 0x3456);
            Check("record 转称号簿 middle/tail 原始字段", title.Chronicle.Options.Count == 2
                && title.Chronicle.Options[1].OptionId == 0x55667788
                && Hex(title.TailData) == Hex(tail));

            title.Slot = title.BookIndex;
            var persisted = TitleBookInventoryItemCodec.Serialize(title);
            var decoded = TitleBookInventoryItemCodec.Deserialize(0, 3, persisted);
            Check("称号簿持久化长度", persisted.Length == TitleBookInventoryItemCodec.PersistedRecordSize);
            Check("称号簿持久化往返基础字段", decoded.BookIndex == 3
                && decoded.Slot == 3
                && decoded.ItemId == title.ItemId
                && decoded.Value == title.Value
                && decoded.EquipmentLockId == title.EquipmentLockId);
            Check("称号簿持久化往返动态字段", decoded.EnchantIndex == title.EnchantIndex
                && decoded.EnchantUpgradeCount == title.EnchantUpgradeCount
                && decoded.AmplifyType == title.AmplifyType
                && decoded.AmplifyValue == title.AmplifyValue
                && Hex(decoded.TailData) == Hex(title.TailData));

            var restoredRecord = new SqliteInventoryStore.ItemRecord
            {
                ExtraJson = TitleBookInventoryItemCodec.ToExtraJson(decoded),
            };
            var restoredView = InventoryItemView.ForCommon(restoredRecord);
            Check("称号簿写回 extra_json prefix/middle/tail", restoredView.Entry84.Attr == decoded.Attr
                && restoredView.EnchantCardId == decoded.EnchantIndex
                && restoredView.EnchantUpgradeCount == decoded.EnchantUpgradeCount
                && Hex(restoredView.Entry84.MiddleData1A) == Hex(middle)
                && Hex(restoredView.Entry84.TailData2F) == Hex(tail));
            Check("称号簿写回 extra_json 保持旧 common jewelSocket 形态", TitleBookInventoryItemCodec.ToExtraJson(decoded).Contains("\"jewelSocket\"", StringComparison.Ordinal)
                && Hex(restoredView.Entry84.JewelSocket) == Hex(new byte[30]));

            Check("称号簿 item_kind 推断保持旧规则", TitleBookInventoryItemCodec.InferItemKind(decoded) == "equipment"
                && TitleBookInventoryItemCodec.InferItemKind(new TitleBookInventoryItem { ItemId = 1, Marker16 = 0 }) == "stackable"
                && TitleBookInventoryItemCodec.InferItemKind(new TitleBookInventoryItem { ItemId = 1, Marker16 = -1, ExpireTime = 1 }) == "special");

            PrintSummary();
            return _fail == 0 ? 0 : 1;
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
