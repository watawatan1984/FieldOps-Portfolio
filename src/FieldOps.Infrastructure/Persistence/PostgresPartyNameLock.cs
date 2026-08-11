using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

namespace FieldOps.Infrastructure.Persistence;

public sealed class PostgresPartyNameLock(FieldOpsDbContext dbContext) : IPartyNameLock
{
    private const long LockNamespace = 4_601_107;

    public async Task<string> NormalizeAndAcquireAsync(
        string partyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partyName);
        IDbContextTransaction transaction = dbContext.Database.CurrentTransaction
            ?? throw new InvalidOperationException("The party name lock requires an active database transaction.");
        NpgsqlConnection connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        NpgsqlTransaction postgresTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        await using NpgsqlCommand command = new(
            """
            SELECT
                upper(@party_name),
                pg_advisory_xact_lock(hashtextextended(upper(@party_name), @lock_namespace))
            """,
            connection,
            postgresTransaction);
        command.Parameters.AddWithValue("party_name", partyName);
        command.Parameters.AddWithValue("lock_namespace", LockNamespace);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("PostgreSQL did not return the normalized party name.");
        }

        return reader.GetString(0);
    }
}