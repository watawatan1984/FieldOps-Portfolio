namespace FieldOps.Infrastructure.Demo;

public enum DemoResetState
{
    Running = 1,
    Completed = 2,
    Failed = 3
}

public sealed class DemoResetExecution
{
    private DemoResetExecution()
    {
    }

    private DemoResetExecution(
        Guid id,
        string idempotencyKey,
        string actorUserId,
        string correlationId,
        DateTime startedAtUtc)
    {
        Id = id;
        IdempotencyKey = idempotencyKey;
        ActorUserId = actorUserId;
        CorrelationId = correlationId;
        StartedAtUtc = startedAtUtc;
        State = DemoResetState.Running;
        Outcome = "Running";
    }

    public Guid Id { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    public DemoResetState State { get; private set; }

    public DateTime StartedAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    public long? DurationMilliseconds { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public string ActorUserId { get; private set; } = string.Empty;

    public string Outcome { get; private set; } = string.Empty;

    public static DemoResetExecution Start(
        Guid id,
        string idempotencyKey,
        string actorUserId,
        string correlationId,
        DateTime startedAtUtc) =>
        new(id, idempotencyKey, actorUserId, correlationId, startedAtUtc);

    public void Complete(DateTime completedAtUtc, long durationMilliseconds)
    {
        CompletedAtUtc = completedAtUtc;
        DurationMilliseconds = durationMilliseconds;
        State = DemoResetState.Completed;
        Outcome = "Completed";
    }

    public void Fail(DateTime completedAtUtc, long durationMilliseconds)
    {
        CompletedAtUtc = completedAtUtc;
        DurationMilliseconds = durationMilliseconds;
        State = DemoResetState.Failed;
        Outcome = "Failed";
    }
}