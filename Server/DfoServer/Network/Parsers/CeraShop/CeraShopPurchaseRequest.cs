using System;

namespace DfoServer.Network.Parsers.CeraShop
{
    public sealed class CeraShopPurchaseRequest
    {
        // 一次购买可包含多件不同商品(购物车), 每件一个 commodityNo
        public System.Collections.Generic.List<int> CommodityNos { get; } = new System.Collections.Generic.List<int>();

        public byte UnknownFlag { get; private set; }

        // 兼容旧调用: 取第一件
        public int ProductId => CommodityNos.Count > 0 ? CommodityNos[0] : 0;
        public int ItemTemplateId => ProductId;
        public int Count => 1; // 每个 commodityNo 购买 1 份, 份内数量由 cerashop 商品定义

        // 请求 body 布局(由客户端 sendBuyPacket 逆向):
        //   [0..1] 未知  [2] totalCount(byte)  [3] flag
        //   之后每件商品项 15 字节, commodityNo(int) 在项内偏移 +3
        private const int HeaderSize = 4;
        private const int ItemStride = 15;
        private const int CommodityOffsetInItem = 3;

        public static bool TryParse(byte[] body, out CeraShopPurchaseRequest request)
        {
            request = null;
            if (body == null || body.Length < HeaderSize + ItemStride)
                return false;

            int totalCount = body[2];
            if (totalCount <= 0)
                totalCount = 1;

            var parsed = new CeraShopPurchaseRequest { UnknownFlag = body[5] };
            for (int i = 0; i < totalCount; i++)
            {
                int commodityOffset = HeaderSize + i * ItemStride + CommodityOffsetInItem;
                if (commodityOffset + 4 > body.Length)
                    break;
                int commodityNo = BitConverter.ToInt32(body, commodityOffset);
                if (commodityNo > 0)
                    parsed.CommodityNos.Add(commodityNo);
            }

            if (parsed.CommodityNos.Count == 0)
                return false;
            request = parsed;
            return true;
        }
    }
}
