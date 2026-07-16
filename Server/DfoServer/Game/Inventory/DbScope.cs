using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public sealed class DbScope : IDisposable
    {
        public SqliteConnection Connection { get; }
        public SqliteTransaction Transaction { get; }
        public int CharacterId { get; }
        public int AccountId { get; }

        // 默认立即事务(BEGIN IMMEDIATE): 与无参 BeginTransaction() 的历史行为一致。
        // 延迟事务(deferred: true)只适合纯读场景——读后升级写锁遇到并发提交会直接抛
        // SQLITE_BUSY_SNAPSHOT 且不经过 busy_timeout 重试。
        internal DbScope(
            string connectionString,
            int characterId,
            int accountId,
            bool deferred = false)
        {
            CharacterId = characterId;
            AccountId = accountId;
            Connection = new SqliteConnection(connectionString);
            Connection.Open();
            Transaction = Connection.BeginTransaction(deferred);
        }

        public void Commit() => Transaction.Commit();

        public void Dispose()
        {
            Transaction?.Dispose();
            Connection?.Dispose();
        }
    }
}
