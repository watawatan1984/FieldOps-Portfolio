namespace FieldOps.Features.Abstractions;

public interface IAuditWriter
{
    void Write(string aggregateType, Guid aggregateId, string action);
}