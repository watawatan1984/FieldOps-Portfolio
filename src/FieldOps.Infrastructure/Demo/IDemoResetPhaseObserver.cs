namespace FieldOps.Infrastructure.Demo;

public enum DemoResetPhase
{
    LockAcquired,
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
    public Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}