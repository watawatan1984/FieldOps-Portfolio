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

        string[] fieldNames = changedFields
            .Select(field => field?.Trim() ?? string.Empty)
            .Where(field => field.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(field => field, StringComparer.Ordinal)
            .ToArray();
        if (fieldNames.Any(field => field.Any(character => !char.IsLetterOrDigit(character) && character != '_')))
        {
            throw new ArgumentException("Audit change summaries accept field names only.", nameof(changedFields));
        }

        dbContext.AuditEntries.Add(new AuditEntry(
            aggregateType,
            aggregateId,
            branchId,
            action,
            outcome,
            string.Join(',', fieldNames),
            timeProvider.GetUtcNow().UtcDateTime,
            currentUser.UserId));
    }
}