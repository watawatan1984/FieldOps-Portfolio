using FieldOps.Domain.Common;

namespace FieldOps.Domain.Entities;

public sealed class Contact : Entity
{
    internal Contact(Guid partyId, string firstName, string lastName, bool isPrimary)
    {
        PartyId = partyId;
        FirstName = RequiredText(firstName, nameof(firstName));
        LastName = RequiredText(lastName, nameof(lastName));
        IsPrimary = isPrimary;
    }

    public Guid PartyId { get; }

    public string FirstName { get; }

    public string LastName { get; }

    public bool IsPrimary { get; }
}
