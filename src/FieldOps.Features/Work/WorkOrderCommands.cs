using FieldOps.Domain.Entities;
using FieldOps.Features.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace FieldOps.Features.Work;

public sealed class WorkOrderAlreadyExistsException : Exception
{
    public WorkOrderAlreadyExistsException() : base("A work order already exists for this sales opportunity.") { }
}

public sealed class WorkOrderConcurrencyException : Exception
{
    public WorkOrderConcurrencyException() : base("The work order was changed by another user.") { }
}

public sealed class WorkOrderCommands(
    IFieldOpsDbContext dbContext,
    IMutationExecutor mutationExecutor,
    IAuditWriter auditWriter,
    IFieldOpsUserDirectory userDirectory,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    private const string FieldTechnicianRole = "Field Technician";
    private const string SystemAdministratorRole = "System Administrator";

    public async Task<Guid> CreateFromOpportunityAsync(
        Guid salesOpportunityId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await mutationExecutor.ExecuteAsync(
                "work-order-create-from-opportunity",
                async token =>
                {
                    if (await dbContext.WorkOrders.AnyAsync(
                        workOrder => workOrder.SalesOpportunityId == salesOpportunityId,
                        token))
                    {
                        throw new WorkOrderAlreadyExistsException();
                    }

                    SalesOpportunity opportunity = await dbContext.SalesOpportunities
                        .SingleOrDefaultAsync(item => item.Id == salesOpportunityId, token)
                        ?? throw new KeyNotFoundException("Sales opportunity not found.");
                    Branch branch = await dbContext.Branches
                        .SingleAsync(item => item.Id == opportunity.BranchId, token);
                    Party party = await dbContext.Parties
                        .Include(item => item.BranchAssignments)
                        .Include(item => item.Sites)
                        .SingleAsync(item => item.Id == opportunity.PartyId, token);
                    Site site = party.Sites.Single(item => item.Id == opportunity.SiteId);
                    WorkOrder workOrder = WorkOrder.CreateFromWon(opportunity, branch, party, site);
                    dbContext.WorkOrders.Add(workOrder);
                    auditWriter.Write(
                        nameof(WorkOrder),
                        workOrder.Id,
                        workOrder.BranchId,
                        "Created",
                        "Success",
                        [nameof(workOrder.SalesOpportunityId), nameof(workOrder.BranchId), nameof(workOrder.PartyId), nameof(workOrder.SiteId)]);
                    return workOrder.Id;
                },
                cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException?.Message.Contains(
                "UX_WorkOrders_SalesOpportunityId",
                StringComparison.Ordinal) == true)
        {
            throw new WorkOrderAlreadyExistsException();
        }
    }

    public async Task ScheduleAndAssignAsync(
        WorkOrderEditInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await mutationExecutor.ExecuteAsync(
                "work-order-schedule-and-assign",
                async token =>
                {
                    WorkOrder workOrder = await dbContext.WorkOrders
                        .Include(item => item.Events)
                        .SingleOrDefaultAsync(item => item.Id == input.Id, token)
                        ?? throw new KeyNotFoundException("Work order not found.");
                    EnsureCurrentVersion(workOrder, input.Version);
                    if (workOrder.Status is not Domain.Enums.WorkOrderStatus.Planned)
                    {
                        throw new Domain.Common.DomainException("Only a planned work order can be scheduled and assigned.");
                    }
                    if (input.ScheduledStartUtc is not DateTime scheduledStartUtc)
                    {
                        throw new Domain.Common.DomainException("A scheduled start is required.");
                    }
                    IReadOnlyList<FieldOpsUserOption> technicians = await userDirectory.GetUsersInRoleAsync(
                        workOrder.BranchId,
                        FieldTechnicianRole,
                        token);
                    if (!technicians.Any(user => user.Id == input.AssignedUserId))
                    {
                        throw new Domain.Common.DomainException("Select a technician in this branch.");
                    }

                    workOrder.AssignToUser(input.AssignedUserId);
                    workOrder.Schedule(scheduledStartUtc, timeProvider.GetUtcNow().UtcDateTime);
                    auditWriter.Write(
                        nameof(WorkOrder),
                        workOrder.Id,
                        workOrder.BranchId,
                        "ScheduledAndAssigned",
                        "Success",
                        [nameof(input.AssignedUserId), nameof(input.ScheduledStartUtc), nameof(workOrder.Status)]);
                    return true;
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new WorkOrderConcurrencyException();
        }
    }

    public async Task TransitionAsync(
        Guid id,
        WorkOrderTransitionInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await mutationExecutor.ExecuteAsync(
                "work-order-transition",
                async token =>
                {
                    WorkOrder workOrder = await dbContext.WorkOrders.Include(item => item.Events)
                        .SingleOrDefaultAsync(item => item.Id == id, token)
                        ?? throw new KeyNotFoundException("Work order not found.");
                    EnsureCurrentVersion(workOrder, input.Version);
                    EnsureUpdateScope(workOrder);
                    workOrder.MoveTo(input.NextStatus, timeProvider.GetUtcNow().UtcDateTime);
                    auditWriter.Write(
                        nameof(WorkOrder), workOrder.Id, workOrder.BranchId,
                        "StatusChanged", "Success", [nameof(input.NextStatus)]);
                    return true;
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new WorkOrderConcurrencyException();
        }
    }

    public async Task AddEventAsync(
        Guid id,
        WorkEventInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await mutationExecutor.ExecuteAsync(
                "work-order-add-event",
                async token =>
                {
                    WorkOrder workOrder = await dbContext.WorkOrders.Include(item => item.Events)
                        .SingleOrDefaultAsync(item => item.Id == id, token)
                        ?? throw new KeyNotFoundException("Work order not found.");
                    EnsureCurrentVersion(workOrder, input.Version);
                    EnsureUpdateScope(workOrder);
                    bool isCorrection = input.EventType == Domain.Enums.WorkEventType.Correction;
                    if (workOrder.Status == Domain.Enums.WorkOrderStatus.Completed)
                    {
                        if (!isCorrection || currentUser.Role != SystemAdministratorRole)
                        {
                            throw new Domain.Common.DomainException("Completed work is read-only except for an administrator correction event.");
                        }
                    }
                    else if (workOrder.Status == Domain.Enums.WorkOrderStatus.Cancelled || isCorrection)
                    {
                        throw new Domain.Common.DomainException("This work order does not accept the requested event.");
                    }
                    if (input.OccurredAtUtc is not DateTime occurredAtUtc)
                    {
                        throw new Domain.Common.DomainException("A work event timestamp is required.");
                    }
                    if (occurredAtUtc > timeProvider.GetUtcNow().UtcDateTime)
                    {
                        throw new Domain.Common.DomainException("A work event timestamp cannot be in the future.");
                    }

                    workOrder.AddEvent(input.EventType, occurredAtUtc, input.Summary, currentUser.UserId);
                    auditWriter.Write(
                        nameof(WorkOrder), workOrder.Id, workOrder.BranchId,
                        isCorrection ? "CorrectionAdded" : "WorkEventAdded",
                        "Success",
                        [nameof(input.EventType), nameof(input.OccurredAtUtc), nameof(input.Summary)]);
                    return true;
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new WorkOrderConcurrencyException();
        }
    }

    private static void EnsureCurrentVersion(WorkOrder workOrder, uint expectedVersion)
    {
        if (workOrder.Version != expectedVersion) throw new WorkOrderConcurrencyException();
    }

    private void EnsureUpdateScope(WorkOrder workOrder)
    {
        if (currentUser.Role == FieldTechnicianRole && workOrder.AssignedUserId != currentUser.UserId)
        {
            throw new UnauthorizedAccessException("Technicians can update only work currently assigned to them.");
        }
    }
}
