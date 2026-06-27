using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using DfoServer.Infrastructure;

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

        public ScopedStoreContext(string databasePath, string schemaFilePath)
        {
            if (databasePath == null) throw new ArgumentNullException(nameof(databasePath));
            if (schemaFilePath == null) throw new ArgumentNullException(nameof(schemaFilePath));

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        public IDisposable BeginScope(int characterId, int accountId)
        {
            Monitor.Enter(_activeLock);
            _activeCharacterId = characterId;
            _activeAccountId = accountId;
            return new ScopeReleaser(this);
        }

        private void EndScope()
        {
            Monitor.Exit(_activeLock);
        }

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private sealed class ScopeReleaser : IDisposable
        {
            private readonly ScopedStoreContext _context;
            private bool _disposed;
            public ScopeReleaser(ScopedStoreContext context) { _context = context; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _context.EndScope();
            }
        }
    }
}
