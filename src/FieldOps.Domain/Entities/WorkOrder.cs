using FieldOps.Domain.Common;
using FieldOps.Domain.Enums;

namespace FieldOps.Domain.Entities;

public sealed class WorkOrder : Entity
{
    private readonly List<WorkEvent> _events = [];

    private WorkOrder()
    {
    }

    private WorkOrder(Branch branch, Party party, Site site, Guid? salesOpportunityId)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(site);
        EnsurePartyAndSiteBelongToBranch(branch, party, site);

        BranchId = branch.Id;
        PartyId = party.Id;
        SiteId = site.Id;
        SalesOpportunityId = salesOpportunityId;
        Status = WorkOrderStatus.Planned;
    }

    public Guid BranchId { get; }

    public Guid PartyId { get; }

    public Guid SiteId { get; }

    public Guid? SalesOpportunityId { get; }

    public string? AssignedUserId { get; private set; }

    public WorkOrderStatus Status { get; private set; }

    public DateTime? ScheduledStartUtc { get; private set; }

    public IReadOnlyList<WorkEvent> Events => _events.AsReadOnly();

    public static WorkOrder Create(Branch branch, Party party, Site site) => new(branch, party, site, null);

    public static WorkOrder CreateFromOpportunity(SalesOpportunity opportunity, Branch branch, Party party, Site site)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        if (opportunity.Status != SalesOpportunityStatus.Won)
        {
            throw new DomainException("A work order can be created only from a Won sales opportunity.");
        }
        if (opportunity.BranchId != branch.Id || opportunity.PartyId != party.Id || opportunity.SiteId != site.Id)
        {
            throw new DomainException("A work order must preserve its sales opportunity branch, party, and site.");
        }

        return new WorkOrder(branch, party, site, opportunity.Id);
    }

    public void AssignToUser(string applicationUserId)
    {
        AssignedUserId = RequiredText(applicationUserId, nameof(applicationUserId));
        Touch();
    }

    public void AddEvent(WorkEventType eventType, DateTime occurredAtUtc, string summary, string actorUserId)
    {
        _events.Add(new WorkEvent(Id, eventType, occurredAtUtc, BranchId, summary, actorUserId));
        Touch();
    }

    public void Schedule(DateTime scheduledStartUtc, DateTime occurredAtUtc)
    {
        RequireUtc(scheduledStartUtc, "work order scheduled start timestamp");
        RequireUtc(occurredAtUtc, "work order transition timestamp");

        if (!IsAllowedTransition(Status, WorkOrderStatus.Scheduled))
        {
            throw InvalidTransition(Status, WorkOrderStatus.Scheduled);
        }

        ScheduledStartUtc = scheduledStartUtc;
        Status = WorkOrderStatus.Scheduled;
        Touch();
    }

    public void MoveTo(WorkOrderStatus next, DateTime occurredAtUtc)
    {
        RequireUtc(occurredAtUtc, "work order transition timestamp");

        if (next == WorkOrderStatus.Scheduled && Status == WorkOrderStatus.Planned)
        {
            throw new DomainException("A WorkOrder scheduled transition requires a scheduled start timestamp. Use Schedule instead.");
        }

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

    public IReadOnlyList<WorkOrderStatus> GetAllowedTransitions() =>
        Enum.GetValues<WorkOrderStatus>()
            .Where(next => next != WorkOrderStatus.Scheduled && IsAllowedTransition(Status, next) &&
                (next != WorkOrderStatus.Completed || _events.Any(workEvent => workEvent.EventType == WorkEventType.Completion)))
            .ToArray();

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
