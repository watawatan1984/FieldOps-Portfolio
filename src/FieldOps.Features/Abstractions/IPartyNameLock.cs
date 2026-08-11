namespace FieldOps.Features.Abstractions;

public interface IPartyNameLock
{
    Task AcquireAsync(string normalizedName, CancellationToken cancellationToken = default);
}