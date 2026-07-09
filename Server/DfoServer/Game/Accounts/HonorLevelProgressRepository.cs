using DfoServer.Game.Characters;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Accounts
{
    public sealed class HonorLevelProgressRepository
    {
        private readonly string _connectionString;
        private readonly ICharacterRepository _characterRepository;

        public HonorLevelProgressRepository(string databasePath, string schemaFilePath, ICharacterRepository characterRepository = null)
        {
            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
            _characterRepository = characterRepository;
        }

        public HonorLevelSummary LoadSummary(int accountId)
        {
            var characters = _characterRepository?.ListByAccount(accountId);
            return LoadSummary(accountId, characters);
        }

        public HonorLevelSummary LoadSummary(int accountId, IEnumerable<CharacterRecord> characters)
        {
            var totalExp = LoadOrCreateAccountHonorExp(accountId);
            return HonorLevelDataProvider.CalculateFromHonorExp(totalExp, characters);
        }

        public HonorLevelSummary AddHonorExp(int accountId, uint delta, IEnumerable<CharacterRecord> characters = null)
        {
            if (accountId <= 0)
                return HonorLevelDataProvider.CalculateFromHonorExp(0UL, Array.Empty<CharacterRecord>());

            characters = characters ?? _characterRepository?.ListByAccount(accountId);
            var totalExp = delta > 0
                ? AddAccountHonorExp(accountId, delta)
                : LoadOrCreateAccountHonorExp(accountId);
            return HonorLevelDataProvider.CalculateFromHonorExp(totalExp, characters);
        }

        private ulong AddAccountHonorExp(int accountId, uint delta)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var current = LoadOrCreateAccountHonorExp(conn, tx, accountId);
                    var max = HonorLevelDataProvider.MaxTotalHonorExp;
                    var next = (ulong)delta >= max - current ? max : current + delta;
                    UpsertAccountHonorExp(conn, tx, accountId, next);
                    tx.Commit();
                    return next;
                }
            }
        }

        private ulong LoadOrCreateAccountHonorExp(int accountId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var total = LoadOrCreateAccountHonorExp(conn, tx, accountId);
                    tx.Commit();
                    return total;
                }
            }
        }

        private ulong LoadOrCreateAccountHonorExp(SqliteConnection conn, SqliteTransaction tx, int accountId)
        {
            if (accountId <= 0)
                return 0;

            var existing = TryLoadAccountHonorExp(conn, tx, accountId);
            if (existing.HasValue)
                return existing.Value;

            UpsertAccountHonorExp(conn, tx, accountId, 0);
            return 0;
        }

        private ulong? TryLoadAccountHonorExp(SqliteConnection conn, SqliteTransaction tx, int accountId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT total_exp FROM account_honor_level WHERE account_id=@aid;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return null;

                var raw = Convert.ToInt64(value);
                if (raw <= 0)
                    return 0;
                return Math.Min((ulong)raw, HonorLevelDataProvider.MaxTotalHonorExp);
            }
        }

        private void UpsertAccountHonorExp(SqliteConnection conn, SqliteTransaction tx, int accountId, ulong totalExp)
        {
            var capped = Math.Min(totalExp, HonorLevelDataProvider.MaxTotalHonorExp);
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO account_honor_level(account_id, total_exp, updated_at)
VALUES(@aid, @exp, CURRENT_TIMESTAMP)
ON CONFLICT(account_id) DO UPDATE SET
    total_exp=excluded.total_exp,
    updated_at=CURRENT_TIMESTAMP;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.Parameters.AddWithValue("@exp", capped > long.MaxValue ? long.MaxValue : (long)capped);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
