using FieldOps.Domain.Common;
using FieldOps.Domain.Enums;

namespace FieldOps.Domain.Entities;

public sealed class PartyRole : Entity
{
    internal PartyRole(Guid partyId, PartyRoleType roleType)
    {
        PartyId = partyId;
        RoleType = roleType;
    }

    public Guid PartyId { get; }

    public PartyRoleType RoleType { get; }
}