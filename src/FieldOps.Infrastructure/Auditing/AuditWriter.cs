using FieldOps.Domain.Entities;
using FieldOps.Features.Abstractions;
using FieldOps.Features.Administration;

namespace FieldOps.Infrastructure.Auditing;

public sealed class AuditWriter(
    IFieldOpsDbContext dbContext,
    ICurrentUser currentUser,
    TimeProvider timeProvider) : IAuditWriter
{
    public void Write(string aggregateType, Guid aggregateId, string action)
    {
        dbContext.AuditEntries.Add(new AuditEntry(
            aggregateType,
            aggregateId,
            action,
            timeProvider.GetUtcNow().UtcDateTime,
            currentUser.UserId));
    }

    public void Write(
        string aggregateType,
        Guid aggregateId,
        Guid branchId,
        string action,
        string outcome,
        IEnumerable<string> changedFields)
    {
        if (branchId == Guid.Empty)
        {
            throw new ArgumentException("A branch identifier is required.", nameof(branchId));
        }

        dbContext.AuditEntries.Add(new AuditEntry(
            aggregateType,
            aggregateId,
            branchId,
            action,
            outcome,
            AuditFieldContract.NormalizeForStorage(changedFields),
            timeProvider.GetUtcNow().UtcDateTime,
            currentUser.UserId));
    }
}