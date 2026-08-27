using FieldOps.Domain.Common;

namespace FieldOps.Domain.Entities;

public sealed class PartyBranchAssignment : Entity
{
    internal PartyBranchAssignment(Guid partyId, Guid branchId)
    {
        PartyId = partyId;
        BranchId = branchId;
    }

    public Guid PartyId { get; }

    public Guid BranchId { get; }
}