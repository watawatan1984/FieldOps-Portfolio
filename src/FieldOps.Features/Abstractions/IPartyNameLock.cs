namespace FieldOps.Features.Abstractions;

public interface IPartyNameLock
{
    Task<string> NormalizeAndAcquireAsync(string partyName, CancellationToken cancellationToken = default);
}