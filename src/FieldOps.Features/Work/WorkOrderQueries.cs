using FieldOps.Features.Abstractions;
using FieldOps.Domain.Enums;
using FieldOps.Domain.Entities;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Features.Work;

public sealed class WorkOrderQueries(
    IFieldOpsDbContext dbContext,
    IFieldOpsUserDirectory userDirectory,
    ICurrentUser currentUser)
{
    private const string FieldTechnicianRole = "Field Technician";
    private const string SystemAdministratorRole = "System Administrator";
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;

    public async Task<WorkOrderIndexViewModel> SearchAsync(
        WorkOrderSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        int page = Math.Max(1, request.Page);
        int pageSize = request.PageSize <= 0 ? DefaultPageSize : Math.Min(request.PageSize, MaximumPageSize);
        long offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(request.Page));
        bool canSelectBranch = currentUser.Role == SystemAdministratorRole;
        IQueryable<WorkOrder> workOrders = dbContext.WorkOrders.AsNoTracking();
        if (request.BranchId != Guid.Empty) workOrders = workOrders.Where(workOrder => workOrder.BranchId == request.BranchId);
        else if (!canSelectBranch) throw new UnauthorizedAccessException("A branch scope is required.");
        if (currentUser.Role == FieldTechnicianRole)
        {
            workOrders = workOrders.Where(workOrder => workOrder.AssignedUserId == currentUser.UserId);
        }

        int totalCount = await workOrders.CountAsync(cancellationToken);
        var rows = await workOrders.OrderBy(workOrder => workOrder.ScheduledStartUtc == null)
            .ThenBy(workOrder => workOrder.ScheduledStartUtc)
            .ThenBy(workOrder => workOrder.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .Select(workOrder => new
            {
                workOrder.Id,
                PartyName = dbContext.Parties.Where(party => party.Id == workOrder.PartyId)
                    .Select(party => party.OrganizationName ?? party.LastName + ", " + party.FirstName).Single(),
                SiteName = dbContext.Parties.SelectMany(party => party.Sites)
                    .Where(site => site.Id == workOrder.SiteId).Select(site => site.Name).Single(),
                BranchName = dbContext.Branches.Where(branch => branch.Id == workOrder.BranchId)
                    .Select(branch => branch.Name).Single(),
                workOrder.AssignedUserId,
                workOrder.Status,
                workOrder.ScheduledStartUtc
            })
            .ToListAsync(cancellationToken);
        IReadOnlyList<FieldOpsUserOption> technicians = await userDirectory.GetUsersInRoleAsync(
            request.BranchId == Guid.Empty ? null : request.BranchId,
            FieldTechnicianRole,
            cancellationToken);
        Dictionary<string, string> names = technicians.ToDictionary(user => user.Id, user => user.DisplayName);
        WorkOrderListItem[] items = rows.Select(row => new WorkOrderListItem(
            row.Id, row.PartyName, row.SiteName, row.BranchName,
            row.AssignedUserId is null ? null : names.GetValueOrDefault(row.AssignedUserId, "Assigned technician"),
            row.Status, row.ScheduledStartUtc)).ToArray();
        IReadOnlyList<WorkOrderBranchOption> branches = canSelectBranch
            ? await dbContext.Branches.AsNoTracking().OrderBy(branch => branch.Name)
                .Select(branch => new WorkOrderBranchOption(branch.Id, branch.Name)).ToListAsync(cancellationToken)
            : [];
        return new(request, page, pageSize, totalCount, items, branches, canSelectBranch);
    }

    public Task<Guid> GetDefaultBranchIdAsync(CancellationToken cancellationToken = default) =>
        dbContext.Branches.AsNoTracking().OrderBy(branch => branch.Name)
            .Select(branch => branch.Id).FirstAsync(cancellationToken);

    public async Task<WorkOrderDetailsViewModel?> GetDetailsAsync(
        Guid id,
        bool canManage,
        bool canUpdate,
        bool canCorrect,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.WorkOrders.AsNoTracking()
            .Include(workOrder => workOrder.Events)
            .Where(workOrder => workOrder.Id == id)
            .Select(workOrder => new
            {
                WorkOrder = workOrder,
                PartyName = dbContext.Parties.Where(party => party.Id == workOrder.PartyId)
                    .Select(party => party.OrganizationName ?? party.LastName + ", " + party.FirstName).Single(),
                SiteName = dbContext.Parties.SelectMany(party => party.Sites)
                    .Where(site => site.Id == workOrder.SiteId).Select(site => site.Name).Single(),
                BranchName = dbContext.Branches.Where(branch => branch.Id == workOrder.BranchId)
                    .Select(branch => branch.Name).Single(),
                Events = workOrder.Events.OrderBy(workEvent => workEvent.OccurredAtUtc).ThenBy(workEvent => workEvent.Id)
                    .Select(workEvent => new WorkEventSummary(workEvent.EventType, workEvent.OccurredAtUtc, workEvent.Summary)).ToList()
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;

        IReadOnlyList<FieldOpsUserOption> technicians = await userDirectory.GetUsersInRoleAsync(
            row.WorkOrder.BranchId,
            FieldTechnicianRole,
            cancellationToken);
        string? assignedName = row.WorkOrder.AssignedUserId is null
            ? null
            : technicians.FirstOrDefault(user => user.Id == row.WorkOrder.AssignedUserId)?.DisplayName ?? "Assigned technician";
        IReadOnlyList<WorkOrderStatus> allowedTransitions = canUpdate
            ? row.WorkOrder.GetAllowedTransitions()
            : [];
        return new(
            row.WorkOrder.Id,
            row.WorkOrder.BranchId,
            row.PartyName,
            row.SiteName,
            row.BranchName,
            assignedName,
            row.WorkOrder.Status,
            row.WorkOrder.ScheduledStartUtc,
            row.WorkOrder.Version,
            canManage,
            canUpdate,
            canCorrect,
            allowedTransitions,
            row.Events);
    }

    public Task<WorkOrderEditInput?> GetEditAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.Id == id)
            .Select(workOrder => new WorkOrderEditInput
            {
                Id = workOrder.Id,
                Version = workOrder.Version,
                Status = workOrder.Status,
                AssignedUserId = workOrder.AssignedUserId ?? string.Empty,
                ScheduledStartUtc = workOrder.ScheduledStartUtc
            })
            .SingleOrDefaultAsync(cancellationToken);

    public Task<WorkOrderStatus?> GetStatusAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.WorkOrders.AsNoTracking().Where(workOrder => workOrder.Id == id)
            .Select(workOrder => (WorkOrderStatus?)workOrder.Status)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<WorkOrderEditorOptions> GetEditorOptionsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.Id == id)
            .Select(workOrder => new
            {
                workOrder.BranchId,
                PartyName = dbContext.Parties.Where(party => party.Id == workOrder.PartyId)
                    .Select(party => party.OrganizationName ?? party.LastName + ", " + party.FirstName).Single(),
                SiteName = dbContext.Parties.SelectMany(party => party.Sites)
                    .Where(site => site.Id == workOrder.SiteId).Select(site => site.Name).Single(),
                BranchName = dbContext.Branches.Where(branch => branch.Id == workOrder.BranchId)
                    .Select(branch => branch.Name).Single()
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Work order not found.");
        IReadOnlyList<FieldOpsUserOption> technicians = await userDirectory.GetUsersInRoleAsync(
            row.BranchId,
            FieldTechnicianRole,
            cancellationToken);
        return new(row.PartyName, row.SiteName, row.BranchName, technicians);
    }
}
