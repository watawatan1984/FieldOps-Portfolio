using FieldOps.Domain.Common;
using FieldOps.Domain.Enums;

namespace FieldOps.Domain.Entities;

public sealed class WorkOrder : Entity
{
    private readonly List<WorkEvent> _events = [];

    private WorkOrder()
    {
    }

    private WorkOrder(Branch branch, Party party, Site site)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(site);
        EnsurePartyAndSiteBelongToBranch(branch, party, site);

        BranchId = branch.Id;
        PartyId = party.Id;
        SiteId = site.Id;
        Status = WorkOrderStatus.Planned;
    }

    public Guid BranchId { get; }

    public Guid PartyId { get; }

    public Guid SiteId { get; }

    public WorkOrderStatus Status { get; private set; }

    public IReadOnlyList<WorkEvent> Events => _events.AsReadOnly();

    public static WorkOrder Create(Branch branch, Party party, Site site) => new(branch, party, site);

    public void AddEvent(WorkEventType eventType, DateTime occurredAtUtc, string summary, string actorUserId)
    {
        _events.Add(new WorkEvent(Id, eventType, occurredAtUtc, BranchId, summary, actorUserId));
        Touch();
    }

    public void MoveTo(WorkOrderStatus next, DateTime occurredAtUtc)
    {
        RequireUtc(occurredAtUtc, "work order transition timestamp");

        if (!Enum.IsDefined(next) || !IsAllowedTransition(Status, next))
        {
            throw InvalidTransition(Status, next);
        }

        if (next == WorkOrderStatus.Completed && !_events.Any(workEvent => workEvent.EventType == WorkEventType.Completion))
        {
            throw new DomainException($"WorkOrder transition from {Status} to {next} requires a completion event.");
        }

        Status = next;
        Touch();
    }

    private static bool IsAllowedTransition(WorkOrderStatus current, WorkOrderStatus next) =>
        (current, next) switch
        {
            (WorkOrderStatus.Planned, WorkOrderStatus.Scheduled or WorkOrderStatus.Cancelled) => true,
            (WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress or WorkOrderStatus.Cancelled) => true,
            (WorkOrderStatus.InProgress, WorkOrderStatus.Completed or WorkOrderStatus.Cancelled) => true,
            _ => false
        };

    private static void EnsurePartyAndSiteBelongToBranch(Branch branch, Party party, Site site)
    {
        if (!party.BranchAssignments.Any(assignment => assignment.BranchId == branch.Id) ||
            site.PartyId != party.Id ||
            site.BranchId != branch.Id)
        {
            throw new DomainException("A work order party and site must belong to its branch.");
        }
    }

    private static DomainException InvalidTransition(WorkOrderStatus current, WorkOrderStatus requested) =>
        new($"WorkOrder transition from {current} to {requested} is not allowed.");

    private static void RequireUtc(DateTime value, string fieldName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new DomainException($"The {fieldName} must use UTC.");
        }
    }
}