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
    DemoResetFailureEvidenceWriter failureEvidenceWriter,
    IDemoModeVerifier demoModeVerifier,
    IDemoResetPhaseObserver phaseObserver,
    TimeProvider timeProvider,
    ILogger<DemoResetService> logger) : IDemoResetService
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    public async Task<DemoResetResult> ResetAsync(
        DemoResetCommand command,
        CancellationToken cancellationToken = default)
    {
        Validate(command);
        await demoModeVerifier.EnsureApprovedAsync(cancellationToken);
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
            await demoModeVerifier.EnsureApprovedAndLockMarkerAsync(cancellationToken);
            await phaseObserver.ObserveAsync(DemoResetPhase.MarkerLocked, cancellationToken);

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

            if (execution?.State == DemoResetState.Failed)
            {
                await transaction.CommitAsync(cancellationToken);
                await transaction.DisposeAsync();
                transaction = null;
                throw DemoResetFailedException.PreviouslyRecorded(execution);
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
        catch (DemoResetFailedException exception) when (exception.WasPreviouslyRecorded)
        {
            throw;
        }
        catch (Exception exception)
        {
            long durationMilliseconds = stopwatch.ElapsedMilliseconds;
            logger.LogError(
                "Demo reset failed; correlation {CorrelationId}; duration {DurationMilliseconds} ms; category {FailureCategory}",
                command.CorrelationId,
                durationMilliseconds,
                DemoResetFailureClassifier.Classify(exception));

            IDbContextTransaction? failedTransaction = transaction;
            transaction = null;
            if (failedTransaction is not null)
            {
                await CleanupFailedTransactionAsync(failedTransaction, command.CorrelationId);
            }

            try
            {
                dbContext.ChangeTracker.Clear();
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(
                    "Demo reset change-tracker cleanup failed; correlation {CorrelationId}; category {FailureCategory}",
                    command.CorrelationId,
                    DemoResetFailureClassifier.Classify(cleanupException));
            }

            await failureEvidenceWriter.TryPersistAsync(
                executionId,
                command.IdempotencyKey,
                command.ActorUserId,
                command.CorrelationId,
                startedAtUtc,
                durationMilliseconds);
            throw new DemoResetFailedException(command.CorrelationId, exception);
        }
        finally
        {
            if (transaction is not null)
            {
                await DisposeTransactionAsync(transaction, command.CorrelationId);
            }
        }
    }

    private async Task CleanupFailedTransactionAsync(
        IDbContextTransaction transaction,
        string correlationId)
    {
        using CancellationTokenSource cleanupTimeout = new(CleanupTimeout);
        try
        {
            await transaction.RollbackAsync(cleanupTimeout.Token).WaitAsync(cleanupTimeout.Token);
        }
        catch (Exception rollbackException)
        {
            logger.LogWarning(
                "Demo reset transaction rollback cleanup failed; correlation {CorrelationId}; category {FailureCategory}",
                correlationId,
                DemoResetFailureClassifier.Classify(rollbackException));
        }

        await DisposeTransactionAsync(transaction, correlationId, cleanupTimeout.Token);
    }

    private async Task DisposeTransactionAsync(
        IDbContextTransaction transaction,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource? ownedTimeout = cancellationToken.CanBeCanceled
            ? null
            : new CancellationTokenSource(CleanupTimeout);
        CancellationToken effectiveToken = ownedTimeout?.Token ?? cancellationToken;
        try
        {
            await transaction.DisposeAsync().AsTask().WaitAsync(effectiveToken);
        }
        catch (Exception disposeException)
        {
            logger.LogWarning(
                "Demo reset transaction dispose cleanup failed; correlation {CorrelationId}; category {FailureCategory}",
                correlationId,
                DemoResetFailureClassifier.Classify(disposeException));
        }
    }

    private static DemoResetResult StoredResult(DemoResetExecution execution)
    {
        return new(
            execution.IdempotencyKey,
            execution.CorrelationId,
            execution.DurationMilliseconds ?? 0,
            true);
    }

    private static string AuditOutcome(string state, long durationMilliseconds, string correlationId)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{state};durationMs={durationMilliseconds};correlationId={correlationId}");
    }

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

public sealed class DemoResetFailedException : Exception
{
    public DemoResetFailedException(string correlationId, Exception innerException)
        : base("The demo reset failed. Use the correlation identifier when retrying or requesting support.", innerException)
    {
        CorrelationId = correlationId;
    }

    private DemoResetFailedException(string correlationId, long durationMilliseconds)
        : base("This idempotency key already has an immutable failed reset outcome. Open a new confirmation page to retry.")
    {
        CorrelationId = correlationId;
        DurationMilliseconds = durationMilliseconds;
        WasPreviouslyRecorded = true;
    }

    public string CorrelationId { get; }

    public long? DurationMilliseconds { get; }

    public bool WasPreviouslyRecorded { get; }

    internal static DemoResetFailedException PreviouslyRecorded(DemoResetExecution execution)
    {
        return new(execution.CorrelationId, execution.DurationMilliseconds ?? 0);
    }
}