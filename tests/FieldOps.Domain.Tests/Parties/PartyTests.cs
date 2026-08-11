using FieldOps.Domain.Common;
using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;

namespace FieldOps.Domain.Tests.Parties;

public sealed class PartyTests
{
    [Fact]
    public void AddRole_AllowsCustomerAndBusinessPartnerOnOneParty()
    {
        Party party = Party.CreateOrganization("Northwind Service Works");

        party.AddRole(PartyRoleType.Customer);
        party.AddRole(PartyRoleType.BusinessPartner);

        Assert.Equal(2, party.Roles.Count);
    }

    [Fact]
    public void AddRole_RejectsDuplicateRole()
    {
        Party party = Party.CreateOrganization("Northwind Service Works");
        party.AddRole(PartyRoleType.Customer);

        Assert.Throws<DomainException>(() => party.AddRole(PartyRoleType.Customer));
    }

    [Fact]
    public void CreateOrganization_TrimsNameAndRejectsBlankName()
    {
        Assert.Throws<DomainException>(() => Party.CreateOrganization("   "));

        Party party = Party.CreateOrganization(" Northwind Service Works ");

        Assert.Equal("Northwind Service Works", party.OrganizationName);
    }

    [Fact]
    public void AssignToBranch_RejectsDuplicateAssignment()
    {
        Party party = Party.CreateOrganization("Northwind Service Works");
        Branch branch = Branch.Create("Harbor Office");
        party.AssignToBranch(branch);

        Assert.Throws<DomainException>(() => party.AssignToBranch(branch));
    }

    [Fact]
    public void AddContact_RejectsSecondPrimaryContact()
    {
        Party party = Party.CreateOrganization("Northwind Service Works");
        party.AddContact("Avery", "Mori", isPrimary: true);

        Assert.Throws<DomainException>(() => party.AddContact("Robin", "Park", isPrimary: true));
    }

    [Fact]
    public void AddSite_RequiresAnAssignedBranch()
    {
        Party party = Party.CreateOrganization("Northwind Service Works");
        Branch branch = Branch.Create("Harbor Office");

        Assert.Throws<DomainException>(() => party.AddSite(branch, "Pier 8 Workshop"));
    }
}
