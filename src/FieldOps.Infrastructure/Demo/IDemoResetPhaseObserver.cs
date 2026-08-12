namespace FieldOps.Infrastructure.Demo;

public enum DemoResetPhase
{
    LockAcquired,
    MarkerLocked,
    RowsDeleted,
    DataSeeded,
    BeforeCommit
}

public interface IDemoResetPhaseObserver
{
    Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken);
}

internal sealed class NullDemoResetPhaseObserver : IDemoResetPhaseObserver
{
    public Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public enum DemoResetTransactionDisposal
{
    CommittedSuccess,
    StoredCompleted,
    StoredFailed,
    FailedCleanup
}

public interface IDemoResetTransactionDisposer
{
    Task DisposeAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        DemoResetTransactionDisposal disposal,
        CancellationToken cancellationToken);
}

internal sealed class DemoResetTransactionDisposer : IDemoResetTransactionDisposer
{
    public Task DisposeAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        DemoResetTransactionDisposal disposal,
        CancellationToken cancellationToken)
    {
        return transaction.DisposeAsync().AsTask();
    }
}