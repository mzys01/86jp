using System;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    [Obsolete("Use DbScope for new code")]
    public sealed class ScopedStoreContext
    {
        private int _activeCharacterId = 1000;
        private int _activeAccountId = 1;
        private readonly object _activeLock = new object();
        private readonly string _connectionString;

        public int CharacterId => _activeCharacterId;
        public int AccountId => _activeAccountId;
        public string ConnectionString => _connectionString;

        public ScopedStoreContext(string connectionString)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
            _connectionString = connectionString;
        }

        public IDisposable BeginScope(int characterId, int accountId)
        {
            Monitor.Enter(_activeLock);
            _activeCharacterId = characterId;
            _activeAccountId = accountId;
            return new ScopeReleaser(this);
        }

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private void EndScope()
        {
            Monitor.Exit(_activeLock);
        }

        private sealed class ScopeReleaser : IDisposable
        {
            private readonly ScopedStoreContext _context;
            private bool _disposed;

            public ScopeReleaser(ScopedStoreContext context)
            {
                _context = context;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                _context.EndScope();
            }
        }
    }
}
