using System.Diagnostics;

using FieldOps.Features.Abstractions;
using FieldOps.Features.Parties;
using FieldOps.Features.Sales;
using FieldOps.Features.Work;

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
        Stopwatch mutationStopwatch = Stopwatch.StartNew();
        long? lockWaitElapsedMs = null;
        long? saveChangesElapsedMs = null;
        long? commitElapsedMs = null;

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            Stopwatch phaseStopwatch = Stopwatch.StartNew();
            await dbContext.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_xact_lock_shared({CoordinationLockKey})",
                cancellationToken);
            lockWaitElapsedMs = phaseStopwatch.ElapsedMilliseconds;

            TResult result = await action(cancellationToken);

            phaseStopwatch.Restart();
            await dbContext.SaveChangesAsync(cancellationToken);
            saveChangesElapsedMs = phaseStopwatch.ElapsedMilliseconds;

            phaseStopwatch.Restart();
            await transaction.CommitAsync(cancellationToken);
            commitElapsedMs = phaseStopwatch.ElapsedMilliseconds;

            logger.LogInformation(
                "Database mutation {Operation} completed with {Outcome} in {MutationElapsedMs} ms; lock wait {LockWaitElapsedMs} ms; save {SaveChangesElapsedMs} ms; commit {CommitElapsedMs} ms",
                operation,
                "success",
                mutationStopwatch.ElapsedMilliseconds,
                lockWaitElapsedMs,
                saveChangesElapsedMs,
                commitElapsedMs);
            return result;
        }
        catch (Exception exception)
        {
            string outcome = IsConcurrencyConflict(exception) ? "conflict" : "failure";
            logger.LogWarning(
                "Database mutation {Operation} completed with {Outcome} in {MutationElapsedMs} ms; lock wait {LockWaitElapsedMs} ms; save {SaveChangesElapsedMs} ms; commit {CommitElapsedMs} ms",
                operation,
                outcome,
                mutationStopwatch.ElapsedMilliseconds,
                lockWaitElapsedMs,
                saveChangesElapsedMs,
                commitElapsedMs);
            throw;
        }
    }

    private static bool IsConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException or
                PartyConcurrencyException or
                SalesConcurrencyException or
                WorkOrderConcurrencyException)
            {
                return true;
            }
        }

        return false;
    }
}
