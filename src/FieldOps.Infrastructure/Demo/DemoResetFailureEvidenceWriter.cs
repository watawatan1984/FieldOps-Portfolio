using System.Data.Common;
using System.Globalization;

using FieldOps.Domain.Entities;
using FieldOps.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace FieldOps.Infrastructure.Demo;

public sealed class DemoResetFailureEvidenceWriter(
    DbContextOptions<FieldOpsDbContext> dbContextOptions,
    TimeProvider timeProvider,
    ILogger<DemoResetFailureEvidenceWriter> logger)
{
    public static readonly TimeSpan PersistenceTimeout = TimeSpan.FromSeconds(5);

    public async Task<bool> TryPersistAsync(
        Guid executionId,
        string idempotencyKey,
        string actorUserId,
        string correlationId,
        DateTime startedAtUtc,
        long durationMilliseconds)
    {
        using CancellationTokenSource timeout = new(PersistenceTimeout);
        try
        {
            return await PersistCoreAsync(
                    executionId,
                    idempotencyKey,
                    actorUserId,
                    correlationId,
                    startedAtUtc,
                    durationMilliseconds,
                    timeout.Token)
                .WaitAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Demo reset failure evidence persistence failed; correlation {CorrelationId}; category {FailureCategory}",
                correlationId,
                DemoResetFailureClassifier.Classify(exception));
            return false;
        }
    }

    private async Task<bool> PersistCoreAsync(
        Guid executionId,
        string idempotencyKey,
        string actorUserId,
        string correlationId,
        DateTime startedAtUtc,
        long durationMilliseconds,
        CancellationToken cancellationToken)
    {
        await using FieldOpsDbContext failureDbContext = new(dbContextOptions);
        await using IDbContextTransaction failureTransaction =
            await failureDbContext.Database.BeginTransactionAsync(cancellationToken);
        await failureDbContext.Database.ExecuteSqlRawAsync(
            $"SELECT pg_advisory_xact_lock({MutationExecutor.CoordinationLockKey})",
            cancellationToken);
        DemoResetExecution? execution = await failureDbContext.DemoResetExecutions
            .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
        if (execution is not null)
        {
            await failureTransaction.CommitAsync(cancellationToken);
            return true;
        }

        DateTime failedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        execution = DemoResetExecution.Start(
            executionId,
            idempotencyKey,
            actorUserId,
            correlationId,
            startedAtUtc);
        execution.Fail(failedAtUtc, durationMilliseconds);
        failureDbContext.DemoResetExecutions.Add(execution);
        failureDbContext.AuditEntries.Add(new AuditEntry(
            "DemoReset",
            execution.Id,
            null,
            "ResetFailed",
            AuditOutcome("Failed", durationMilliseconds, correlationId),
            string.Empty,
            failedAtUtc,
            actorUserId));
        await failureDbContext.SaveChangesAsync(cancellationToken);
        await failureTransaction.CommitAsync(cancellationToken);
        return true;
    }

    private static string AuditOutcome(string state, long durationMilliseconds, string correlationId)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{state};durationMs={durationMilliseconds};correlationId={correlationId}");
    }
}

internal static class DemoResetFailureClassifier
{
    public static string Classify(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => "Interrupted",
            TimeoutException => "Timeout",
            DbException => "DatabaseUnavailable",
            _ => "Unexpected"
        };
    }
}