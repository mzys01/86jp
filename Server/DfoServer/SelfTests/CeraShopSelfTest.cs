using System;
using DfoServer.Game.Shop;

namespace DfoServer.SelfTests
{
    // 验证 cerashop.etc 的 [regular package] 段被正确解析: 该段商品(如强化成功/增幅成功幸运礼盒)
    // 曾因 CeraShopProductCatalog 漏解析该段而 TryResolve 恒 false, 购买失败并被客户端显示为
    // "物品栏空间不足"。本自测断言这些商品可解析且点券价读自正确的列(col4)。
    // 依赖: 运行目录 Data/Pvf/Script.pvf (与其它需 PVF 的自测一致)。
    public static class CeraShopSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== CERASHOP selftest ===");
            int pass = 0;
            int fail = 0;

            void Check(string name, bool ok)
            {
                if (ok)
                {
                    pass++;
                    Console.WriteLine($"  [PASS] {name}");
                }
                else
                {
                    fail++;
                    Console.WriteLine($"  [FAIL] {name}");
                }
            }

            void CheckProduct(string label, int productId, int expectedItemId, int expectedCoinPrice)
            {
                if (!CeraShopProductCatalog.TryResolve(productId, out var entry) || entry == null)
                {
                    Check($"{label} (commodityNo {productId}) resolves", false);
                    return;
                }

                Check($"{label} (commodityNo {productId}) resolves", true);
                Check($"{label} itemTemplateId == {expectedItemId} (got {entry.ItemTemplateId})", entry.ItemTemplateId == expectedItemId);
                Check($"{label} coinPrice == {expectedCoinPrice} (got {entry.CoinPrice})", entry.CoinPrice == expectedCoinPrice);
            }

            // [regular package] 段商品 —— 修复前该段未解析, 这两件礼盒购买必失败。
            CheckProduct("强化成功幸运礼盒", 102661, 10007836, 9800);
            CheckProduct("增幅成功幸运礼盒", 102660, 10007837, 12800);

            // 回归: 已解析的 [regular package] 段样本商品(Lv80~84 专用礼包)也应正确读到 col4 价格。
            CheckProduct("Lv80~84专用礼包", 102290, 2683268, 2860);

            // [community package] 段(stride=11, 价格 col4) —— 修复前未解析, 婚庆/社区礼包购买必失败。
            CheckProduct("社区礼包(结婚戒指-男)", 102317, 2683326, 18888);

            Console.WriteLine($"=== result: {pass} PASS, {fail} FAIL ===");
            return fail == 0 ? 0 : 1;
        }
    }
}
