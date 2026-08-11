using FieldOps.Domain.Entities;
using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Features.Administration;

public sealed class AuditQueries(
    IFieldOpsDbContext dbContext,
    IFieldOpsUserDirectory userDirectory)
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;

    public async Task<AuditPage> SearchAsync(
        Guid? branchId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        int effectivePage = Math.Max(1, page);
        int effectivePageSize = pageSize <= 0
            ? DefaultPageSize
            : Math.Min(pageSize, MaximumPageSize);
        long offset = ((long)effectivePage - 1) * effectivePageSize;
        if (offset > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        IQueryable<AuditEntry> query = dbContext.AuditEntries.AsNoTracking()
            .Where(entry => !branchId.HasValue || entry.BranchId == branchId.Value);
        int totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Skip((int)offset)
            .Take(effectivePageSize)
            .Select(entry => new
            {
                entry.AggregateType,
                entry.Action,
                entry.Outcome,
                entry.ChangeSummary,
                entry.OccurredAtUtc,
                entry.ActorUserId,
                BranchName = entry.BranchId.HasValue
                    ? dbContext.Branches
                        .Where(branch => branch.Id == entry.BranchId.Value)
                        .Select(branch => branch.Name)
                        .Single()
                    : "National"
            })
            .ToArrayAsync(cancellationToken);

        IReadOnlyDictionary<string, string> displayNames = await userDirectory.GetDisplayNamesAsync(
            rows.Select(row => row.ActorUserId),
            cancellationToken);
        AuditListItem[] items = rows.Select(row => new AuditListItem(
            row.AggregateType,
            row.Action,
            row.Outcome,
            SafeChangeSummary(row.ChangeSummary),
            row.OccurredAtUtc,
            row.BranchName,
            displayNames.GetValueOrDefault(row.ActorUserId, "Former demo user")))
            .ToArray();

        return new AuditPage(branchId, effectivePage, effectivePageSize, totalCount, items);
    }

    private static string SafeChangeSummary(string value)
    {
        string[] fields = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return fields.Length > 0 && fields.All(field =>
            field.All(character => char.IsLetterOrDigit(character) || character == '_'))
                ? string.Join(", ", fields)
                : "Details withheld";
    }
}

public sealed record AuditListItem(
    string AggregateType,
    string Action,
    string Outcome,
    string ChangedFields,
    DateTime OccurredAtUtc,
    string BranchName,
    string ActorDisplayName);

public sealed record AuditPage(
    Guid? BranchId,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<AuditListItem> Items)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}