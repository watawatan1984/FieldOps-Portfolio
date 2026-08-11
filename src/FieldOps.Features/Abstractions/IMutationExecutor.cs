namespace FieldOps.Features.Abstractions;

public interface IMutationExecutor
{
    Task<TResult> ExecuteAsync<TResult>(
        string operation,
        Func<CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default);
}