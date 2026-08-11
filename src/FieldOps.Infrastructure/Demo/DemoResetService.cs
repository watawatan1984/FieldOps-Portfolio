using System.Diagnostics;
using System.Globalization;

using FieldOps.Domain.Entities;
using FieldOps.Features.Administration;
using FieldOps.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace FieldOps.Infrastructure.Demo;

public sealed class DemoResetService(
    FieldOpsDbContext dbContext,
    DemoDataSeeder dataSeeder,
    IDemoResetPhaseObserver phaseObserver,
    TimeProvider timeProvider,
    ILogger<DemoResetService> logger) : IDemoResetService
{
    public async Task<DemoResetResult> ResetAsync(
        DemoResetCommand command,
        CancellationToken cancellationToken = default)
    {
        Validate(command);
        Stopwatch stopwatch = Stopwatch.StartNew();
        DateTime startedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        Guid executionId = Guid.NewGuid();
        IDbContextTransaction? transaction = null;

        try
        {
            transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_xact_lock({MutationExecutor.CoordinationLockKey})",
                cancellationToken);
            await phaseObserver.ObserveAsync(DemoResetPhase.LockAcquired, cancellationToken);

            DemoResetExecution? execution = await dbContext.DemoResetExecutions
                .SingleOrDefaultAsync(
                    item => item.IdempotencyKey == command.IdempotencyKey,
                    cancellationToken);
            if (execution?.State == DemoResetState.Completed)
            {
                await transaction.CommitAsync(cancellationToken);
                return StoredResult(execution);
            }

            if (execution?.State == DemoResetState.Running)
            {
                throw new InvalidOperationException("A completed advisory-lock owner left a running reset state.");
            }

            if (execution is null)
            {
                execution = DemoResetExecution.Start(
                    executionId,
                    command.IdempotencyKey,
                    command.ActorUserId,
                    command.CorrelationId,
                    startedAtUtc);
                dbContext.DemoResetExecutions.Add(execution);
            }
            else
            {
                executionId = execution.Id;
                execution.Restart(command.ActorUserId, command.CorrelationId, startedAtUtc);
            }

            IReadOnlyDictionary<string, string> passwordHashes =
                await dataSeeder.CapturePasswordHashesAsync(cancellationToken);
            await dataSeeder.DeleteDemoOwnedRowsAsync(cancellationToken);
            await phaseObserver.ObserveAsync(DemoResetPhase.RowsDeleted, cancellationToken);
            await dataSeeder.SeedAsync(passwordHashes, cancellationToken);
            await phaseObserver.ObserveAsync(DemoResetPhase.DataSeeded, cancellationToken);

            DateTime completedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            long durationMilliseconds = stopwatch.ElapsedMilliseconds;
            string successOutcome = AuditOutcome("Completed", durationMilliseconds, command.CorrelationId);
            dbContext.AuditEntries.Add(new AuditEntry(
                "DemoReset",
                execution.Id,
                null,
                "ResetStarted",
                AuditOutcome("Started", 0, command.CorrelationId),
                string.Empty,
                startedAtUtc,
                command.ActorUserId));
            dbContext.AuditEntries.Add(new AuditEntry(
                "DemoReset",
                execution.Id,
                null,
                "ResetCompleted",
                successOutcome,
                string.Empty,
                completedAtUtc,
                command.ActorUserId));
            execution.Complete(completedAtUtc, durationMilliseconds);

            await dbContext.SaveChangesAsync(cancellationToken);
            await phaseObserver.ObserveAsync(DemoResetPhase.BeforeCommit, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await transaction.DisposeAsync();
            transaction = null;

            logger.LogInformation(
                "Demo reset completed with {Outcome}; correlation {CorrelationId}; duration {DurationMilliseconds} ms",
                "Completed",
                command.CorrelationId,
                durationMilliseconds);
            return new DemoResetResult(
                command.IdempotencyKey,
                command.CorrelationId,
                durationMilliseconds,
                false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await transaction.DisposeAsync();
                transaction = null;
            }

            dbContext.ChangeTracker.Clear();
            long durationMilliseconds = stopwatch.ElapsedMilliseconds;
            await PersistFailureEvidenceAsync(
                executionId,
                command,
                startedAtUtc,
                durationMilliseconds,
                CancellationToken.None);
            logger.LogError(
                "Demo reset failed with {Outcome}; correlation {CorrelationId}; duration {DurationMilliseconds} ms; safe type {ExceptionType}",
                "Failed",
                command.CorrelationId,
                durationMilliseconds,
                exception.GetType().Name);
            throw new DemoResetFailedException(command.CorrelationId, exception);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task PersistFailureEvidenceAsync(
        Guid executionId,
        DemoResetCommand command,
        DateTime startedAtUtc,
        long durationMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await using IDbContextTransaction failureTransaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_xact_lock({MutationExecutor.CoordinationLockKey})",
                cancellationToken);
            DemoResetExecution? execution = await dbContext.DemoResetExecutions
                .SingleOrDefaultAsync(
                    item => item.IdempotencyKey == command.IdempotencyKey,
                    cancellationToken);
            if (execution?.State == DemoResetState.Completed)
            {
                await failureTransaction.CommitAsync(cancellationToken);
                return;
            }

            DateTime failedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            if (execution is null)
            {
                execution = DemoResetExecution.Start(
                    executionId,
                    command.IdempotencyKey,
                    command.ActorUserId,
                    command.CorrelationId,
                    startedAtUtc);
                dbContext.DemoResetExecutions.Add(execution);
            }
            else
            {
                execution.Restart(command.ActorUserId, command.CorrelationId, startedAtUtc);
            }

            execution.Fail(failedAtUtc, durationMilliseconds);
            dbContext.AuditEntries.Add(new AuditEntry(
                "DemoReset",
                execution.Id,
                null,
                "ResetFailed",
                AuditOutcome("Failed", durationMilliseconds, command.CorrelationId),
                string.Empty,
                failedAtUtc,
                command.ActorUserId));
            await dbContext.SaveChangesAsync(cancellationToken);
            await failureTransaction.CommitAsync(cancellationToken);
        }
        catch (Exception evidenceException)
        {
            dbContext.ChangeTracker.Clear();
            logger.LogError(
                "Demo reset failure evidence could not be persisted; correlation {CorrelationId}; safe type {ExceptionType}",
                command.CorrelationId,
                evidenceException.GetType().Name);
        }
    }

    private static DemoResetResult StoredResult(DemoResetExecution execution) =>
        new(
            execution.IdempotencyKey,
            execution.CorrelationId,
            execution.DurationMilliseconds ?? 0,
            true);

    private static string AuditOutcome(string state, long durationMilliseconds, string correlationId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{state};durationMs={durationMilliseconds};correlationId={correlationId}");

    private static void Validate(DemoResetCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 64)
        {
            throw new ArgumentException("The idempotency key must contain 1 to 64 characters.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.ActorUserId) || command.ActorUserId.Length > 450)
        {
            throw new ArgumentException("The actor user identifier is invalid.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.CorrelationId) ||
            command.CorrelationId.Length > 128 ||
            command.CorrelationId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException("The correlation identifier is invalid.", nameof(command));
        }
    }
}

public sealed class DemoResetFailedException(string correlationId, Exception innerException)
    : Exception("The demo reset failed. Use the correlation identifier when retrying or requesting support.", innerException)
{
    public string CorrelationId { get; } = correlationId;
}