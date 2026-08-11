using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Infrastructure.Persistence;

public sealed class PostgresPartyNameLock(FieldOpsDbContext dbContext) : IPartyNameLock
{
    private const long LockNamespace = 4_601_107;

    public async Task AcquireAsync(string normalizedName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedName);
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The party name lock requires an active database transaction.");
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({normalizedName}, {LockNamespace}))",
            cancellationToken);
    }
}