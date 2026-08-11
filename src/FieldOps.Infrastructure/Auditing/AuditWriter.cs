using FieldOps.Domain.Entities;
using FieldOps.Features.Abstractions;

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
}