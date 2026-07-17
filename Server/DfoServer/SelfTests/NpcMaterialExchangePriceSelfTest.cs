using DfoServer.Game.Inventory;
using PvfLib;
using System;

namespace DfoServer.SelfTests
{
    // Keeps the PVF price semantics used by ordinary NPC material exchanges
    // independent from the live Script.pvf contents.
    public static class NpcMaterialExchangePriceSelfTest
    {
        private static int _fail;

        public static int Run()
        {
            _fail = 0;
            Console.WriteLine("=== NPC_MATERIAL_EXCHANGE_PRICE selftest ===");

            Check("price plus material keeps gold price",
                ItemMetadataResolver.ResolveBuyGold(50000, 0) == 50000);
            Check("matching negative add price makes exchange material-only",
                ItemMetadataResolver.ResolveBuyGold(50000, -50000) == 0);
            Check("partial negative add price reduces gold price",
                ItemMetadataResolver.ResolveBuyGold(50000, -10000) == 40000);
            Check("missing price is material-only regardless of value",
                ItemMetadataResolver.ResolveBuyGold(-1, 0) == 0);

            var equipment = EquipmentFile.Parse(@"
[price]
50000
[add price]
-50000
[value]
85120
[need material]
10088692 230");
            Check("equipment parses signed add price", equipment.AddPrice == -50000);
            Check("equipment parses material cost", equipment.NeedMaterial == "10088692 230");
            Check("equipment effective exchange gold is zero",
                ItemMetadataResolver.ResolveBuyGold(equipment.Price, equipment.AddPrice) == 0);

            var stackable = StackableItemFile.Parse(@"
[price]
50000
[add price]
-10000
[need material]
10088692 230");
            Check("stackable parses signed add price", stackable.AddPrice == -10000);
            Check("stackable effective exchange gold is adjusted",
                ItemMetadataResolver.ResolveBuyGold(stackable.Price, stackable.AddPrice) == 40000);

            Console.WriteLine($"=== SUMMARY: fail={_fail} ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void Check(string label, bool passed)
        {
            Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {label}");
            if (!passed)
                _fail++;
        }
    }
}
