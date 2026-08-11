namespace FieldOps.Features.Administration;

public interface IDemoResetService
{
    Task<DemoResetResult> ResetAsync(
        DemoResetCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record DemoResetCommand(
    string IdempotencyKey,
    string ActorUserId,
    string CorrelationId);

public sealed record DemoResetResult(
    string IdempotencyKey,
    string CorrelationId,
    long DurationMilliseconds,
    bool WasAlreadyCompleted);