using Microsoft.Data.Sqlite;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public sealed partial class SqliteInventoryStore
    {
        public bool TryOpenAvatarPackage(int characterId, int accountId, AvatarPackageOpenRequest request, out AvatarPackageOpenResult result)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var ok = _packageStore.TryOpenAvatarPackage(connection, transaction, characterId, accountId, request, out result);
                    if (ok) transaction.Commit();
                    return ok;
                }
            }
        }

        public bool TryOpenSelectablePackage(int characterId, int accountId, SelectablePackageOpenRequest request, out SelectablePackageOpenResult result)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var ok = _packageStore.TryOpenSelectablePackage(connection, transaction, characterId, accountId, request, out result);
                    if (ok) transaction.Commit();
                    return ok;
                }
            }
        }

        public bool TryUseBoosterItem(int characterId, int accountId, BoosterUseRequest request, out BoosterUseResult result)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var ok = _packageStore.TryUseBoosterItem(connection, transaction, characterId, accountId, request, out result);
                    if (ok) transaction.Commit();
                    return ok;
                }
            }
        }

        public bool CanUseBoosterItem(int characterId, int accountId, BoosterUseRequest request)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                    return _packageStore.CanUseBoosterItem(connection, transaction, characterId, accountId, request);
            }
        }

        public bool TryOpenPackage0207(int characterId, int accountId, short slotIndex, IReadOnlyList<int> selectedItemTemplateIds, out BoosterUseResult result)
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var ok = _packageStore.TryOpenPackage0207(connection, transaction, characterId, accountId, slotIndex, selectedItemTemplateIds, out result);
                    if (ok) transaction.Commit();
                    return ok;
                }
            }
        }
    }
}
