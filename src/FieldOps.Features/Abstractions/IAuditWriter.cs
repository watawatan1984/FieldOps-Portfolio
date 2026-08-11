namespace FieldOps.Features.Abstractions;

public interface IAuditWriter
{
    void Write(string aggregateType, Guid aggregateId, string action);

    void Write(
        string aggregateType,
        Guid aggregateId,
        Guid branchId,
        string action,
        string outcome,
        IEnumerable<string> changedFields);
}