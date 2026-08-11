using System.Diagnostics;

using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FieldOps.Infrastructure.Persistence;

public sealed class MutationExecutor(
    FieldOpsDbContext dbContext,
    ILogger<MutationExecutor> logger) : IMutationExecutor
{
    public const long CoordinationLockKey = 4601101;

    public async Task<TResult> ExecuteAsync<TResult>(
        string operation,
        Func<CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_xact_lock_shared({CoordinationLockKey})",
                cancellationToken);

            TResult result = await action(cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Database mutation {Operation} completed with {Outcome} in {DbElapsedMs} ms",
                operation,
                "success",
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch
        {
            logger.LogWarning(
                "Database mutation {Operation} completed with {Outcome} in {DbElapsedMs} ms",
                operation,
                "failure",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}