using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Features.Dashboard;

public sealed class BranchProgressQueries(
    IFieldOpsDbContext dbContext,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    DashboardQueries dashboardQueries)
{
    private const string SystemAdministratorRole = "System Administrator";
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    public async Task<IReadOnlyList<BranchProgressItem>> GetNationalAsync(
        CancellationToken cancellationToken = default)
    {
        if (currentUser.Role != SystemAdministratorRole)
        {
            throw new UnauthorizedAccessException("National branch comparison is administrator-only.");
        }

        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        DateTime utcToday = utcNow.Date;
        DateOnly todayInJapan = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, JapanTimeZone));
        DateTime japanTodayStartUtc = ToUtcStartOfJapanDay(todayInJapan);
        DateTime japanTomorrowStartUtc = ToUtcStartOfJapanDay(todayInJapan.AddDays(1));
        DateTime utcMonthStart = new(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime utcNextMonthStart = utcMonthStart.AddMonths(1);

        var salesCounts = await dbContext.SalesOpportunities.AsNoTracking()
            .GroupBy(opportunity => opportunity.BranchId)
            .Select(group => new
            {
                BranchId = group.Key,
                OpenOpportunities = group.Count(opportunity =>
                    opportunity.Status != SalesOpportunityStatus.Won &&
                    opportunity.Status != SalesOpportunityStatus.Lost),
                ProposalsDue = group.Count(opportunity =>
                    opportunity.Status == SalesOpportunityStatus.Proposed &&
                    opportunity.ExpectedCloseDate.HasValue &&
                    opportunity.ExpectedCloseDate.Value <= utcToday)
            })
            .ToDictionaryAsync(row => row.BranchId, cancellationToken);

        var workCounts = await dbContext.WorkOrders.AsNoTracking()
            .GroupBy(workOrder => workOrder.BranchId)
            .Select(group => new
            {
                BranchId = group.Key,
                ScheduledWork = group.Count(workOrder => workOrder.Status == WorkOrderStatus.Scheduled),
                TodayScheduledWork = group.Count(workOrder =>
                    workOrder.Status == WorkOrderStatus.Scheduled &&
                    workOrder.ScheduledStartUtc.HasValue &&
                    workOrder.ScheduledStartUtc.Value >= japanTodayStartUtc &&
                    workOrder.ScheduledStartUtc.Value < japanTomorrowStartUtc),
                UnassignedScheduledWork = group.Count(workOrder =>
                    workOrder.Status == WorkOrderStatus.Scheduled &&
                    workOrder.AssignedUserId == null),
                WorkInProgress = group.Count(workOrder => workOrder.Status == WorkOrderStatus.InProgress),
                OverdueWork = group.Count(workOrder =>
                    (workOrder.Status == WorkOrderStatus.Scheduled || workOrder.Status == WorkOrderStatus.InProgress) &&
                    workOrder.ScheduledStartUtc.HasValue &&
                    workOrder.ScheduledStartUtc.Value < utcNow),
                MissingCompletionRecords = group.Count(workOrder =>
                    workOrder.Status == WorkOrderStatus.InProgress &&
                    !workOrder.Events.Any(workEvent => workEvent.EventType == WorkEventType.Completion)),
                CompletionsThisMonth = group.Count(workOrder =>
                    workOrder.Status == WorkOrderStatus.Completed &&
                    workOrder.Events.Any(workEvent =>
                        workEvent.EventType == WorkEventType.Completion &&
                        workEvent.OccurredAtUtc >= utcMonthStart &&
                        workEvent.OccurredAtUtc < utcNextMonthStart))
            })
            .ToDictionaryAsync(row => row.BranchId, cancellationToken);

        var branches = await dbContext.Branches.AsNoTracking()
            .OrderBy(branch => branch.Name)
            .ThenBy(branch => branch.Id)
            .Select(branch => new { branch.Id, branch.Name })
            .ToArrayAsync(cancellationToken);

        return branches.Select(branch =>
        {
            salesCounts.TryGetValue(branch.Id, out var sales);
            workCounts.TryGetValue(branch.Id, out var work);
            return new BranchProgressItem(
                branch.Id,
                branch.Name,
                new DashboardMetrics(
                    sales?.OpenOpportunities ?? 0,
                    sales?.ProposalsDue ?? 0,
                    work?.ScheduledWork ?? 0,
                    work?.TodayScheduledWork ?? 0,
                    work?.UnassignedScheduledWork ?? 0,
                    work?.WorkInProgress ?? 0,
                    work?.OverdueWork ?? 0,
                    work?.MissingCompletionRecords ?? 0,
                    work?.CompletionsThisMonth ?? 0,
                    utcNow));
        }).ToArray();
    }

    public async Task<BranchProgressItem?> GetDetailsAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        string? branchName = await dbContext.Branches.AsNoTracking()
            .Where(branch => branch.Id == branchId)
            .Select(branch => branch.Name)
            .SingleOrDefaultAsync(cancellationToken);
        if (branchName is null)
        {
            return null;
        }

        DashboardMetrics metrics = await dashboardQueries.GetAsync(branchId, cancellationToken);
        return new BranchProgressItem(branchId, branchName, metrics);
    }

    private static DateTime ToUtcStartOfJapanDay(DateOnly japanDate)
    {
        DateTime unspecifiedJapanMidnight = japanDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecifiedJapanMidnight, JapanTimeZone);
    }
}

public sealed record BranchProgressItem(Guid BranchId, string BranchName, DashboardMetrics Metrics);