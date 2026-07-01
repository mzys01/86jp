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
        public bool TryOpenAvatarPackage(AvatarPackageOpenRequest request, out AvatarPackageOpenResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _packageStore.TryOpenAvatarPackage(connection, transaction, _context.CharacterId, _context.AccountId, request, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        public bool TryOpenSelectablePackage(SelectablePackageOpenRequest request, out SelectablePackageOpenResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _packageStore.TryOpenSelectablePackage(connection, transaction, _context.CharacterId, _context.AccountId, request, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        public bool TryUseBoosterItem(BoosterUseRequest request, out BoosterUseResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _packageStore.TryUseBoosterItem(connection, transaction, _context.CharacterId, _context.AccountId, request, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        public bool TryOpenPackage0207(short slotIndex, IReadOnlyList<int> selectedItemTemplateIds, out BoosterUseResult result)
        {
            using (var connection = _context.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var ok = _packageStore.TryOpenPackage0207(connection, transaction, _context.CharacterId, _context.AccountId, slotIndex, selectedItemTemplateIds, out result);
                if (ok) transaction.Commit();
                return ok;
            }
        }

        // 合并装扮(时装合成): 扣掉 slot1/slot2 两件旧时装 + 1 个消耗品(合成器), 在时装栏第一个
        // 空位插入新时装。新时装itemId由 resolveNewItemId(oldItemId1, oldItemId2, consumeMaterialId)
    }
}
