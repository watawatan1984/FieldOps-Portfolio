namespace FieldOps.Features.Abstractions;

public sealed record FieldOpsUserOption(string Id, string DisplayName);

public interface IFieldOpsUserDirectory
{
    Task<IReadOnlyList<FieldOpsUserOption>> GetUsersInRoleAsync(
        Guid? branchId,
        string role,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetDisplayNamesAsync(
        IEnumerable<string> userIds,
        CancellationToken cancellationToken = default);
}