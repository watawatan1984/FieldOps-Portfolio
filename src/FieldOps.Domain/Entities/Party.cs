using FieldOps.Domain.Common;
using FieldOps.Domain.Enums;

namespace FieldOps.Domain.Entities;

public sealed class Party : Entity
{
    private readonly List<PartyRole> _roles = [];
    private readonly List<PartyBranchAssignment> _branchAssignments = [];
    private readonly List<Contact> _contacts = [];
    private readonly List<Site> _sites = [];

    private Party(string? organizationName, string? firstName, string? lastName)
    {
        OrganizationName = organizationName;
        FirstName = firstName;
        LastName = lastName;
    }

    public string? OrganizationName { get; }

    public string? FirstName { get; }

    public string? LastName { get; }

    public bool IsOrganization => OrganizationName is not null;

    public IReadOnlyList<PartyRole> Roles => _roles.AsReadOnly();

    public IReadOnlyList<PartyBranchAssignment> BranchAssignments => _branchAssignments.AsReadOnly();

    public IReadOnlyList<Contact> Contacts => _contacts.AsReadOnly();

    public IReadOnlyList<Site> Sites => _sites.AsReadOnly();

    public static Party CreateOrganization(string organizationName) =>
        new(RequiredText(organizationName, nameof(organizationName)), null, null);

    public static Party CreatePerson(string firstName, string lastName) =>
        new(null, RequiredText(firstName, nameof(firstName)), RequiredText(lastName, nameof(lastName)));

    public void AddRole(PartyRoleType roleType)
    {
        if (_roles.Any(role => role.RoleType == roleType))
        {
            throw new DomainException($"The party already has the {roleType} role.");
        }

        _roles.Add(new PartyRole(Id, roleType));
        Touch();
    }

    public void AssignToBranch(Branch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);

        if (_branchAssignments.Any(assignment => assignment.BranchId == branch.Id))
        {
            throw new DomainException("The party is already assigned to this branch.");
        }

        _branchAssignments.Add(new PartyBranchAssignment(Id, branch.Id));
        Touch();
    }

    public void AddContact(string firstName, string lastName, bool isPrimary)
    {
        if (isPrimary && _contacts.Any(contact => contact.IsPrimary))
        {
            throw new DomainException("A party can have only one primary contact.");
        }

        _contacts.Add(new Contact(Id, firstName, lastName, isPrimary));
        Touch();
    }

    public void AddSite(Branch branch, string name)
    {
        ArgumentNullException.ThrowIfNull(branch);

        if (!_branchAssignments.Any(assignment => assignment.BranchId == branch.Id))
        {
            throw new DomainException("A site must belong to a branch assigned to the party.");
        }

        _sites.Add(new Site(Id, branch.Id, name));
        Touch();
    }
}
