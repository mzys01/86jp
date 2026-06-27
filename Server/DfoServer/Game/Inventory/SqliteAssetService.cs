using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Inventory
{
    public sealed class SqliteAssetService : IAssetService
    {
        private readonly string _connectionString;
        private readonly SqliteInventoryStore _store;

        public SqliteAssetService(string databasePath, string schemaFilePath)
        {
            if (databasePath == null) throw new ArgumentNullException(nameof(databasePath));
            if (schemaFilePath == null) throw new ArgumentNullException(nameof(schemaFilePath));

            _connectionString = SqliteDatabaseBootstrap.BuildConnectionString(databasePath);
            _store = new SqliteInventoryStore(databasePath, schemaFilePath);
        }

        public DbScope OpenScope(int characterId, int accountId)
        {
            return new DbScope(_connectionString, characterId, accountId);
        }

        public bool TryAddItem(DbScope scope, int itemTemplateId, int count, out short assignedSlot)
        {
            assignedSlot = -1;
            var storage = AssetRouter.Classify(itemTemplateId);

            switch (storage)
            {
                case AssetStorage.AccountCubeFragment:
                    CurrencyService.AddCubeFragment(scope.Connection, scope.Transaction, scope.AccountId, itemTemplateId, count);
                    assignedSlot = (short)CurrencyService.GetCubeFragmentSlot(itemTemplateId);
                    return true;

                case AssetStorage.CharacterItem:
                    using (_store.BeginScope(scope.CharacterId, scope.AccountId))
                    {
                        return _store.TryPickupItemCore(
                            scope.Connection, scope.Transaction,
                            scope.CharacterId, scope.AccountId,
                            itemTemplateId, count, out assignedSlot);
                    }

                default:
                    return false;
            }
        }

        public bool TryRemoveItem(DbScope scope, int itemTemplateId, int count, out short slot, out int remaining)
        {
            slot = -1;
            remaining = 0;

            var result = InventoryDbPrimitives.RemoveItemByTemplateId(
                scope.Connection, scope.Transaction,
                scope.CharacterId, itemTemplateId, count);

            if (result == null)
                return false;

            slot = result.Value.SlotIndex;
            remaining = result.Value.RemainingCount;
            return true;
        }

        public int CountItem(DbScope scope, int itemTemplateId)
        {
            if (CurrencyService.IsCubeFragment(itemTemplateId))
            {
                var cubes = CurrencyService.LoadCubeFragments(scope.Connection, scope.Transaction, scope.AccountId);
                foreach (var cube in cubes)
                {
                    if (cube.ItemId == itemTemplateId)
                        return cube.Count;
                }
                return 0;
            }

            using (var cmd = scope.Connection.CreateCommand())
            {
                cmd.Transaction = scope.Transaction;
                cmd.CommandText = "SELECT stack_count FROM character_items WHERE character_id = @cid AND list_type = 0 AND item_template_id = @tid LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", scope.CharacterId);
                cmd.Parameters.AddWithValue("@tid", itemTemplateId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    return Convert.ToInt32(result);
                return 0;
            }
        }

        public WalletSnapshot LoadWallet(DbScope scope)
        {
            return CurrencyService.LoadWallet(scope.Connection, scope.Transaction, scope.CharacterId);
        }

        public void AddGold(DbScope scope, int delta)
        {
            var wallet = CurrencyService.LoadWallet(scope.Connection, scope.Transaction, scope.CharacterId);
            var newGold = wallet.Gold + delta;
            if (newGold < 0)
                newGold = 0;
            CurrencyService.UpdateGold(scope.Connection, scope.Transaction, scope.CharacterId, newGold);
        }

        public void AddCera(DbScope scope, int delta)
        {
            var wallet = CurrencyService.LoadWallet(scope.Connection, scope.Transaction, scope.CharacterId);
            var newValue = wallet.Cera + delta;
            if (newValue < 0) newValue = 0;
            CurrencyService.UpdateCera(scope.Connection, scope.Transaction, scope.CharacterId, newValue);
        }

        public void AddTokenCera(DbScope scope, int delta)
        {
            var wallet = CurrencyService.LoadWallet(scope.Connection, scope.Transaction, scope.CharacterId);
            var newValue = wallet.TokenCera + delta;
            if (newValue < 0) newValue = 0;
            CurrencyService.UpdateTokenCera(scope.Connection, scope.Transaction, scope.CharacterId, newValue);
        }

        public void AddHappyTokenCera(DbScope scope, int delta)
        {
            var wallet = CurrencyService.LoadWallet(scope.Connection, scope.Transaction, scope.CharacterId);
            var newValue = wallet.HappyTokenCera + delta;
            if (newValue < 0) newValue = 0;
            CurrencyService.UpdateHappyTokenCera(scope.Connection, scope.Transaction, scope.CharacterId, newValue);
        }

        public void AddLuckyStar(DbScope scope, int delta)
        {
            var wallet = CurrencyService.LoadWallet(scope.Connection, scope.Transaction, scope.CharacterId);
            var newValue = wallet.LuckyStar + delta;
            if (newValue < 0) newValue = 0;
            if (newValue > 999) newValue = 999;
            CurrencyService.UpdateLuckyStar(scope.Connection, scope.Transaction, scope.AccountId, (ushort)newValue);
        }

        public CharacterItemListSnapshot LoadSnapshot(DbScope scope)
        {
            // LoadCharacterItemListSnapshot uses its own connection internally (read-only, no deadlock risk).
            // Phase 4 will optimize this to share the scope's connection.
            using (_store.BeginScope(scope.CharacterId, scope.AccountId))
                return _store.LoadCharacterItemListSnapshot();
        }
    }
}
