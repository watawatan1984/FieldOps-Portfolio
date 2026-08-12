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