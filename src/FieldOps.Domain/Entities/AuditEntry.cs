using FieldOps.Domain.Common;

namespace FieldOps.Domain.Entities;

public sealed class AuditEntry : Entity
{
    public AuditEntry(string aggregateType, Guid aggregateId, string action, DateTime occurredAtUtc, string actorUserId)
        : this(aggregateType, aggregateId, null, action, "Success", string.Empty, occurredAtUtc, actorUserId)
    {
    }

    public AuditEntry(
        string aggregateType,
        Guid aggregateId,
        Guid? branchId,
        string action,
        string outcome,
        string changeSummary,
        DateTime occurredAtUtc,
        string actorUserId)
    {
        if (aggregateId == Guid.Empty)
        {
            throw new DomainException("An audit entry aggregate identifier is required.");
        }

        if (occurredAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new DomainException("The audit entry timestamp must use UTC.");
        }

        if (branchId == Guid.Empty)
        {
            throw new DomainException("An audit entry branch identifier must not be empty.");
        }

        AggregateType = RequiredText(aggregateType, nameof(aggregateType));
        AggregateId = aggregateId;
        BranchId = branchId;
        Action = RequiredText(action, nameof(action));
        Outcome = RequiredText(outcome, nameof(outcome));
        ChangeSummary = changeSummary?.Trim() ?? string.Empty;
        OccurredAtUtc = occurredAtUtc;
        ActorUserId = RequiredText(actorUserId, nameof(actorUserId));
    }

    public string AggregateType { get; }

    public Guid AggregateId { get; }

    public Guid? BranchId { get; }

    public string Action { get; }

    public string Outcome { get; }

    public string ChangeSummary { get; }

    public DateTime OccurredAtUtc { get; }

    public string ActorUserId { get; }
}