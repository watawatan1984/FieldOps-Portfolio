using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Features.Dashboard;

public sealed class DashboardQueries(
    IFieldOpsDbContext dbContext,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    private const string SystemAdministratorRole = "System Administrator";
    private const string BranchManagerRole = "Branch Manager";
    private const string SalesRepresentativeRole = "Sales Representative";
    private const string FieldTechnicianRole = "Field Technician";
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    public async Task<DashboardMetrics> GetAsync(
        Guid? branchId,
        CancellationToken cancellationToken = default)
    {
        if (currentUser.Role != SystemAdministratorRole && !branchId.HasValue)
        {
            throw new UnauthorizedAccessException("A branch scope is required for this dashboard.");
        }

        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        DateTime utcToday = utcNow.Date;
        DateOnly todayInJapan = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, JapanTimeZone));
        DateTime japanTodayStartUtc = ToUtcStartOfJapanDay(todayInJapan);
        DateTime japanTomorrowStartUtc = ToUtcStartOfJapanDay(todayInJapan.AddDays(1));
        DateTime utcMonthStart = new(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime utcNextMonthStart = utcMonthStart.AddMonths(1);

        IQueryable<SalesOpportunity> sales = ScopeSales(dbContext.SalesOpportunities.AsNoTracking(), branchId);
        var salesCounts = await sales
            .GroupBy(_ => 1)
            .Select(group => new
            {
                OpenOpportunities = group.Count(opportunity =>
                    opportunity.Status != SalesOpportunityStatus.Won &&
                    opportunity.Status != SalesOpportunityStatus.Lost),
                ProposalsDue = group.Count(opportunity =>
                    opportunity.Status == SalesOpportunityStatus.Proposed &&
                    opportunity.ExpectedCloseDate.HasValue &&
                    opportunity.ExpectedCloseDate.Value <= utcToday)
            })
            .SingleOrDefaultAsync(cancellationToken);

        IQueryable<WorkOrder> work = ScopeWork(dbContext.WorkOrders.AsNoTracking(), branchId);
        var workCounts = await work
            .GroupBy(_ => 1)
            .Select(group => new
            {
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
            .SingleOrDefaultAsync(cancellationToken);

        return new DashboardMetrics(
            salesCounts?.OpenOpportunities ?? 0,
            salesCounts?.ProposalsDue ?? 0,
            workCounts?.ScheduledWork ?? 0,
            workCounts?.TodayScheduledWork ?? 0,
            workCounts?.UnassignedScheduledWork ?? 0,
            workCounts?.WorkInProgress ?? 0,
            workCounts?.OverdueWork ?? 0,
            workCounts?.MissingCompletionRecords ?? 0,
            workCounts?.CompletionsThisMonth ?? 0,
            utcNow);
    }

    private IQueryable<SalesOpportunity> ScopeSales(IQueryable<SalesOpportunity> query, Guid? branchId)
    {
        if (branchId.HasValue)
        {
            query = query.Where(opportunity => opportunity.BranchId == branchId.Value);
        }

        return currentUser.Role switch
        {
            SystemAdministratorRole or BranchManagerRole or SalesRepresentativeRole => query,
            FieldTechnicianRole => query.Where(opportunity => opportunity.AssignedUserId == currentUser.UserId),
            _ => throw new UnauthorizedAccessException("The current role cannot view a dashboard.")
        };
    }

    private IQueryable<WorkOrder> ScopeWork(IQueryable<WorkOrder> query, Guid? branchId)
    {
        if (branchId.HasValue)
        {
            query = query.Where(workOrder => workOrder.BranchId == branchId.Value);
        }

        return currentUser.Role switch
        {
            SystemAdministratorRole or BranchManagerRole or SalesRepresentativeRole => query,
            FieldTechnicianRole => query.Where(workOrder => workOrder.AssignedUserId == currentUser.UserId),
            _ => throw new UnauthorizedAccessException("The current role cannot view a dashboard.")
        };
    }

    private static DateTime ToUtcStartOfJapanDay(DateOnly japanDate)
    {
        DateTime unspecifiedJapanMidnight = japanDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecifiedJapanMidnight, JapanTimeZone);
    }
}

public sealed record DashboardMetrics(
    int OpenOpportunities,
    int ProposalsDue,
    int ScheduledWork,
    int TodayScheduledWork,
    int UnassignedScheduledWork,
    int WorkInProgress,
    int OverdueWork,
    int MissingCompletionRecords,
    int CompletionsThisMonth,
    DateTime AsOfUtc)
{
    public bool IsEmpty =>
        OpenOpportunities == 0 &&
        ProposalsDue == 0 &&
        ScheduledWork == 0 &&
        TodayScheduledWork == 0 &&
        UnassignedScheduledWork == 0 &&
        WorkInProgress == 0 &&
        OverdueWork == 0 &&
        MissingCompletionRecords == 0 &&
        CompletionsThisMonth == 0;
}