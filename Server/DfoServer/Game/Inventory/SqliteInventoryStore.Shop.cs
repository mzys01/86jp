using DfoServer.Infrastructure;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.ItemUpgrade;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        // 商城购买入口: 开启事务, 透传 paymentMode 与 attributeValue 到 InventoryShopStore。
        // 事务在方法内开启, 成功则 Commit, 失败自动回滚(Dispose 不 Commit)。
        public bool TryBuyCeraShopItem(int productId, int buyCount, int paymentMode, byte attributeValue, out InventoryMutationResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _shopStore.TryBuyCeraShopItem(connection, transaction, _context.CharacterId, _context.AccountId, productId, buyCount, paymentMode, attributeValue, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        // 宠物判定: 物品在 equipment.lst 且 .equ 的 [equipment type] 为 [creature]。
    }
}
