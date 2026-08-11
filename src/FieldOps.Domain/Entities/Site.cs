using FieldOps.Domain.Common;

namespace FieldOps.Domain.Entities;

public sealed class Site : Entity
{
    internal Site(Guid partyId, Guid branchId, string name)
    {
        PartyId = partyId;
        BranchId = branchId;
        Name = RequiredText(name, nameof(name));
    }

    public Guid PartyId { get; }

    public Guid BranchId { get; }

    public string Name { get; }
}
