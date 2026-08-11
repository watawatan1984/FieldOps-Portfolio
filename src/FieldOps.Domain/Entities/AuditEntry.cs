using FieldOps.Domain.Common;

namespace FieldOps.Domain.Entities;

public sealed class AuditEntry : Entity
{
    public AuditEntry(string aggregateType, Guid aggregateId, string action, DateTime occurredAtUtc, string actorUserId)
    {
        if (aggregateId == Guid.Empty)
        {
            throw new DomainException("An audit entry aggregate identifier is required.");
        }

        if (occurredAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new DomainException("The audit entry timestamp must use UTC.");
        }

        AggregateType = RequiredText(aggregateType, nameof(aggregateType));
        AggregateId = aggregateId;
        Action = RequiredText(action, nameof(action));
        OccurredAtUtc = occurredAtUtc;
        ActorUserId = RequiredText(actorUserId, nameof(actorUserId));
    }

    public string AggregateType { get; }

    public Guid AggregateId { get; }

    public string Action { get; }

    public DateTime OccurredAtUtc { get; }

    public string ActorUserId { get; }
}
