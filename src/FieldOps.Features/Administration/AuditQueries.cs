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
                    : "全支店"
            })
            .ToArrayAsync(cancellationToken);

        IReadOnlyDictionary<string, string> displayNames = await userDirectory.GetDisplayNamesAsync(
            rows.Select(row => row.ActorUserId),
            cancellationToken);
        AuditListItem[] items = rows.Select(row => new AuditListItem(
            row.AggregateType,
            row.Action,
            row.Outcome,
            AuditFieldContract.FormatForDisplay(row.ChangeSummary),
            row.OccurredAtUtc,
            row.BranchName,
            displayNames.GetValueOrDefault(row.ActorUserId, "未登録の利用者")))
            .ToArray();

        return new AuditPage(branchId, effectivePage, effectivePageSize, totalCount, items);
    }

}

public static class AuditFieldContract
{
    private const string Withheld = "詳細は非表示です";

    private static readonly HashSet<string> ApprovedFields = new(StringComparer.Ordinal)
    {
        "AssignedUserId",
        "BranchId",
        "ContactFirstName",
        "ContactLastName",
        "EventType",
        "ExpectedCloseDate",
        "IsBusinessPartner",
        "IsCustomer",
        "LineItems",
        "NextStatus",
        "Notes",
        "OccurredAtUtc",
        "OrganizationName",
        "OwnerUserId",
        "PartyId",
        "ProposedAmount",
        "RoleType",
        "SalesOpportunityId",
        "ScheduledStartUtc",
        "SiteId",
        "SiteName",
        "Status",
        "Summary",
        "TargetBranchId",
        "TaxRatePercent",
        "ValidUntil"
    };

    public static string NormalizeForStorage(IEnumerable<string> changedFields)
    {
        string[] fields = changedFields
            .Select(field => field?.Trim() ?? string.Empty)
            .Where(field => field.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(field => field, StringComparer.Ordinal)
            .ToArray();
        if (fields.Any(field => !ApprovedFields.Contains(field)))
        {
            throw new ArgumentException("Audit change summaries accept approved field names only.", nameof(changedFields));
        }

        return string.Join(',', fields);
    }

    public static string FormatForDisplay(string persistedSummary)
    {
        string[] fields = persistedSummary
            .Split(',', StringSplitOptions.TrimEntries);
        return fields.Length > 0 &&
            fields.All(field => field.Length > 0 && ApprovedFields.Contains(field))
                ? string.Join(", ", fields.Distinct(StringComparer.Ordinal))
                : Withheld;
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