using FieldOps.Domain.Common;
using FieldOps.Domain.Enums;

namespace FieldOps.Domain.Entities;

public sealed class WorkEvent : Entity
{
    internal WorkEvent(Guid workOrderId, WorkEventType eventType, DateTime occurredAtUtc, Guid branchId, string summary, string actorUserId)
    {
        if (!Enum.IsDefined(eventType))
        {
            throw new DomainException("The work event type is not supported.");
        }

        if (occurredAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new DomainException("The work event timestamp must use UTC.");
        }

        if (workOrderId == Guid.Empty || branchId == Guid.Empty)
        {
            throw new DomainException("A work event must be associated with a work order and branch.");
        }

        WorkOrderId = workOrderId;
        EventType = eventType;
        OccurredAtUtc = occurredAtUtc;
        BranchId = branchId;
        Summary = RequiredText(summary, nameof(summary));
        ActorUserId = RequiredText(actorUserId, nameof(actorUserId));
    }

    public Guid WorkOrderId { get; }

    public WorkEventType EventType { get; }

    public DateTime OccurredAtUtc { get; }

    public Guid BranchId { get; }

    public string Summary { get; }

    public string ActorUserId { get; }
}