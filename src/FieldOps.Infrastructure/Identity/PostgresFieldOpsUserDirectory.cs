using FieldOps.Features.Abstractions;
using FieldOps.Infrastructure.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Infrastructure.Identity;

public sealed class PostgresFieldOpsUserDirectory(FieldOpsDbContext dbContext) : IFieldOpsUserDirectory
{
    public async Task<IReadOnlyList<FieldOpsUserOption>> GetUsersInRoleAsync(
        Guid? branchId,
        string role,
        CancellationToken cancellationToken = default)
    {
        string? roleId = await dbContext.Roles
            .Where(item => item.Name == role)
            .Select(item => item.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (roleId is null)
        {
            return [];
        }

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => (!branchId.HasValue || user.BranchId == branchId.Value) &&
                dbContext.UserRoles.Any(userRole => userRole.UserId == user.Id && userRole.RoleId == roleId))
            .OrderBy(user => user.DisplayName)
            .ThenBy(user => user.Id)
            .Select(user => new FieldOpsUserOption(user.Id, user.DisplayName))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDisplayNamesAsync(
        IEnumerable<string> userIds,
        CancellationToken cancellationToken = default)
    {
        string[] ids = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return await dbContext.Users.AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, StringComparer.Ordinal, cancellationToken);
    }
}