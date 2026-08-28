using System.Diagnostics;

using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FieldOps.Features.Work;

public sealed class WorkHistorySearch(
    IFieldOpsDbContext dbContext,
    IFieldOpsUserDirectory userDirectory,
    ICurrentUser currentUser,
    ILogger<WorkHistorySearch> logger)
{
    private const string FieldTechnicianRole = "Field Technician";
    private const string SystemAdministratorRole = "System Administrator";
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;

    public async Task<WorkHistorySearchResult> SearchAsync(
        WorkHistorySearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        int page = Math.Max(1, criteria.Page);
        int pageSize = criteria.PageSize <= 0
            ? DefaultPageSize
            : Math.Min(criteria.PageSize, MaximumPageSize);
        long offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(criteria.Page));
        }
        if (!criteria.BranchId.HasValue && currentUser.Role != SystemAdministratorRole)
        {
            throw new UnauthorizedAccessException("A branch scope is required.");
        }

        long started = Stopwatch.GetTimestamp();
        IQueryable<WorkOrder> query = dbContext.WorkOrders
            .AsNoTracking()
            .Where(workOrder => !criteria.BranchId.HasValue || workOrder.BranchId == criteria.BranchId.Value);

        if (currentUser.Role == FieldTechnicianRole)
        {
            query = query.Where(workOrder => workOrder.AssignedUserId == currentUser.UserId);
        }

        if (criteria.CustomerId.HasValue)
        {
            query = query.Where(workOrder => workOrder.PartyId == criteria.CustomerId.Value &&
                dbContext.Parties.Any(party => party.Id == workOrder.PartyId &&
                    party.Roles.Any(role => role.RoleType == PartyRoleType.Customer)));
        }
        if (criteria.BusinessPartnerId.HasValue)
        {
            query = query.Where(workOrder => workOrder.BusinessPartnerId == criteria.BusinessPartnerId.Value);
        }
        if (criteria.SiteId.HasValue)
        {
            query = query.Where(workOrder => workOrder.SiteId == criteria.SiteId.Value);
        }
        if (criteria.WorkStatus.HasValue)
        {
            query = query.Where(workOrder => workOrder.Status == criteria.WorkStatus.Value);
        }
        if (criteria.EventType.HasValue)
        {
            query = query.Where(workOrder => workOrder.Events.Any(workEvent =>
                workEvent.EventType == criteria.EventType.Value));
        }
        if (criteria.TechnicianId is not null)
        {
            query = query.Where(workOrder => workOrder.AssignedUserId == criteria.TechnicianId);
        }
        if (criteria.ScheduledFrom.HasValue)
        {
            DateTime scheduledFromUtc = criteria.ScheduledFrom.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(workOrder => workOrder.ScheduledStartUtc >= scheduledFromUtc);
        }
        if (criteria.ScheduledTo.HasValue)
        {
            if (criteria.ScheduledTo.Value == DateOnly.MaxValue)
            {
                DateTime scheduledToUtc = criteria.ScheduledTo.Value.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
                query = query.Where(workOrder => workOrder.ScheduledStartUtc <= scheduledToUtc);
            }
            else
            {
                DateTime scheduledBeforeUtc = criteria.ScheduledTo.Value.AddDays(1)
                    .ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                query = query.Where(workOrder => workOrder.ScheduledStartUtc < scheduledBeforeUtc);
            }
        }
        if (criteria.CompletedFrom.HasValue || criteria.CompletedTo.HasValue)
        {
            DateTime? completedFromUtc = criteria.CompletedFrom?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            DateTime? completedBeforeUtc = criteria.CompletedTo is { } completedTo && completedTo != DateOnly.MaxValue
                ? completedTo.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                : null;
            DateTime? completedThroughUtc = criteria.CompletedTo == DateOnly.MaxValue
                ? DateOnly.MaxValue.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc)
                : null;
            query = query.Where(workOrder => workOrder.Events.Any(workEvent =>
                workEvent.EventType == WorkEventType.Completion &&
                (!completedFromUtc.HasValue || workEvent.OccurredAtUtc >= completedFromUtc.Value) &&
                (!completedBeforeUtc.HasValue || workEvent.OccurredAtUtc < completedBeforeUtc.Value) &&
                (!completedThroughUtc.HasValue || workEvent.OccurredAtUtc <= completedThroughUtc.Value)));
        }

        if (criteria.Keyword is not null)
        {
            string keywordPattern = $"%{EscapeLikePattern(criteria.Keyword)}%";
            query = query.Where(workOrder =>
                dbContext.Parties.Any(party => party.Id == workOrder.PartyId &&
                    EF.Functions.ILike(
                        EF.Property<string>(party, SearchTextNormalization.PropertyName),
                        keywordPattern,
                        "\\")) ||
                dbContext.Parties.SelectMany(party => party.Sites).Any(site =>
                    site.Id == workOrder.SiteId &&
                    EF.Functions.ILike(
                        EF.Property<string>(site, SearchTextNormalization.PropertyName),
                        keywordPattern,
                        "\\")) ||
                workOrder.Events.Any(workEvent => EF.Functions.ILike(
                    EF.Property<string>(workEvent, SearchTextNormalization.PropertyName),
                    keywordPattern,
                    "\\")));
        }

        int totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(workOrder => workOrder.ScheduledStartUtc)
            .ThenBy(workOrder => workOrder.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .Select(workOrder => new
            {
                workOrder.Id,
                workOrder.BranchId,
                CustomerName = dbContext.Parties
                    .Where(party => party.Id == workOrder.PartyId)
                    .Select(party => party.OrganizationName ?? party.LastName + ", " + party.FirstName)
                    .Single(),
                SiteName = dbContext.Parties
                    .SelectMany(party => party.Sites)
                    .Where(site => site.Id == workOrder.SiteId)
                    .Select(site => site.Name)
                    .Single(),
                BranchName = dbContext.Branches
                    .Where(branch => branch.Id == workOrder.BranchId)
                    .Select(branch => branch.Name)
                    .Single(),
                workOrder.AssignedUserId,
                workOrder.Status,
                workOrder.ScheduledStartUtc,
                CompletionDateUtc = workOrder.Events
                    .Where(workEvent => workEvent.EventType == WorkEventType.Completion)
                    .Select(workEvent => (DateTime?)workEvent.OccurredAtUtc)
                    .Max()
            })
            .ToListAsync(cancellationToken);

        IReadOnlyList<FieldOpsUserOption> technicians = await userDirectory.GetUsersInRoleAsync(
            criteria.BranchId,
            FieldTechnicianRole,
            cancellationToken);
        Dictionary<string, string> technicianNames = technicians.ToDictionary(user => user.Id, user => user.DisplayName);
        WorkHistoryListItem[] items = rows.Select(row => new WorkHistoryListItem(
            row.Id,
            row.BranchId,
            row.CustomerName,
            row.SiteName,
            row.BranchName,
            row.AssignedUserId,
            row.AssignedUserId is null ? null : technicianNames.GetValueOrDefault(row.AssignedUserId, "未登録の担当者"),
            row.Status,
            row.ScheduledStartUtc,
            row.CompletionDateUtc)).ToArray();

        logger.LogInformation(
            "Work history search completed in {DurationMilliseconds} ms with {ResultCount} results on page {Page}",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            items.Length,
            page);

        return new WorkHistorySearchResult(criteria, page, pageSize, totalCount, items);
    }

    public async Task<WorkHistoryFilterOptions> GetFilterOptionsAsync(
        Guid? branchId,
        bool canSelectBranch,
        CancellationToken cancellationToken = default)
    {
        WorkHistoryNamedOption[] branches = canSelectBranch
            ? await dbContext.Branches.AsNoTracking()
                .OrderBy(branch => branch.Name)
                .Select(branch => new WorkHistoryNamedOption(branch.Id, branch.Name))
                .ToArrayAsync(cancellationToken)
            : [];
        WorkHistoryNamedOption[] customers = await dbContext.Parties.AsNoTracking()
            .Where(party => party.Roles.Any(role => role.RoleType == PartyRoleType.Customer) &&
                (!branchId.HasValue || party.BranchAssignments.Any(assignment => assignment.BranchId == branchId.Value)))
            .OrderBy(party => party.OrganizationName ?? party.LastName)
            .ThenBy(party => party.FirstName)
            .ThenBy(party => party.Id)
            .Select(party => new WorkHistoryNamedOption(
                party.Id,
                party.OrganizationName ?? party.LastName + ", " + party.FirstName))
            .ToArrayAsync(cancellationToken);
        WorkHistoryNamedOption[] businessPartners = await dbContext.Parties.AsNoTracking()
            .Where(party => party.Roles.Any(role => role.RoleType == PartyRoleType.BusinessPartner) &&
                (!branchId.HasValue || party.BranchAssignments.Any(assignment => assignment.BranchId == branchId.Value)))
            .OrderBy(party => party.OrganizationName ?? party.LastName)
            .ThenBy(party => party.FirstName)
            .ThenBy(party => party.Id)
            .Select(party => new WorkHistoryNamedOption(
                party.Id,
                party.OrganizationName ?? party.LastName + ", " + party.FirstName))
            .ToArrayAsync(cancellationToken);
        WorkHistoryNamedOption[] sites = await dbContext.Parties.AsNoTracking()
            .SelectMany(party => party.Sites)
            .Where(site => !branchId.HasValue || site.BranchId == branchId.Value)
            .OrderBy(site => site.Name)
            .ThenBy(site => site.Id)
            .Select(site => new WorkHistoryNamedOption(site.Id, site.Name))
            .ToArrayAsync(cancellationToken);
        IReadOnlyList<FieldOpsUserOption> technicians = await userDirectory.GetUsersInRoleAsync(
            branchId,
            FieldTechnicianRole,
            cancellationToken);
        return new WorkHistoryFilterOptions(branches, customers, businessPartners, sites, technicians);
    }

    public static string? NormalizeKeyword(string? keyword) => SearchTextNormalization.Normalize(keyword);

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}

public sealed record WorkHistorySearchCriteria(
    Guid? BranchId,
    Guid? CustomerId,
    Guid? BusinessPartnerId,
    Guid? SiteId,
    WorkOrderStatus? WorkStatus,
    WorkEventType? EventType,
    string? TechnicianId,
    DateOnly? ScheduledFrom,
    DateOnly? ScheduledTo,
    DateOnly? CompletedFrom,
    DateOnly? CompletedTo,
    string? Keyword,
    int Page = 1,
    int PageSize = WorkHistorySearch.DefaultPageSize);

public sealed record WorkHistoryListItem(
    Guid Id,
    Guid BranchId,
    string CustomerName,
    string SiteName,
    string BranchName,
    string? AssignedUserId,
    string? AssignedUserName,
    WorkOrderStatus Status,
    DateTime? ScheduledStartUtc,
    DateTime? CompletionDateUtc);

public sealed record WorkHistorySearchResult(
    WorkHistorySearchCriteria Criteria,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<WorkHistoryListItem> Items)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
}

public sealed record WorkHistoryNamedOption(Guid Id, string Name);

public sealed record WorkHistoryFilterOptions(
    IReadOnlyList<WorkHistoryNamedOption> Branches,
    IReadOnlyList<WorkHistoryNamedOption> Customers,
    IReadOnlyList<WorkHistoryNamedOption> BusinessPartners,
    IReadOnlyList<WorkHistoryNamedOption> Sites,
    IReadOnlyList<FieldOpsUserOption> Technicians);