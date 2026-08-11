using FieldOps.Domain.Common;
using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;

namespace FieldOps.Domain.Tests.Sales;

public sealed class SalesOpportunityTests
{
    public static TheoryData<SalesOpportunityStatus, SalesOpportunityStatus> AllowedTransitions =>
        new()
        {
            { SalesOpportunityStatus.New, SalesOpportunityStatus.Contacted },
            { SalesOpportunityStatus.Contacted, SalesOpportunityStatus.SurveyScheduled },
            { SalesOpportunityStatus.SurveyScheduled, SalesOpportunityStatus.Quoting },
            { SalesOpportunityStatus.Quoting, SalesOpportunityStatus.Proposed },
            { SalesOpportunityStatus.Proposed, SalesOpportunityStatus.Won },
            { SalesOpportunityStatus.New, SalesOpportunityStatus.Lost },
            { SalesOpportunityStatus.Contacted, SalesOpportunityStatus.Lost },
            { SalesOpportunityStatus.SurveyScheduled, SalesOpportunityStatus.Lost },
            { SalesOpportunityStatus.Quoting, SalesOpportunityStatus.Lost },
            { SalesOpportunityStatus.Proposed, SalesOpportunityStatus.Lost },
            { SalesOpportunityStatus.New, SalesOpportunityStatus.OnHold },
            { SalesOpportunityStatus.Contacted, SalesOpportunityStatus.OnHold },
            { SalesOpportunityStatus.SurveyScheduled, SalesOpportunityStatus.OnHold },
            { SalesOpportunityStatus.Quoting, SalesOpportunityStatus.OnHold },
            { SalesOpportunityStatus.Proposed, SalesOpportunityStatus.OnHold },
            { SalesOpportunityStatus.OnHold, SalesOpportunityStatus.Contacted },
            { SalesOpportunityStatus.OnHold, SalesOpportunityStatus.SurveyScheduled },
            { SalesOpportunityStatus.OnHold, SalesOpportunityStatus.Quoting },
            { SalesOpportunityStatus.OnHold, SalesOpportunityStatus.Proposed },
            { SalesOpportunityStatus.OnHold, SalesOpportunityStatus.Lost }
        };

    public static TheoryData<SalesOpportunityStatus, SalesOpportunityStatus> RejectedTransitions =>
        new()
        {
            { SalesOpportunityStatus.New, SalesOpportunityStatus.Proposed },
            { SalesOpportunityStatus.Contacted, SalesOpportunityStatus.Won },
            { SalesOpportunityStatus.Proposed, SalesOpportunityStatus.Contacted },
            { SalesOpportunityStatus.OnHold, SalesOpportunityStatus.Won },
            { SalesOpportunityStatus.Won, SalesOpportunityStatus.Lost },
            { SalesOpportunityStatus.Lost, SalesOpportunityStatus.Contacted }
        };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void MoveTo_AllowsDocumentedTransitions(SalesOpportunityStatus current, SalesOpportunityStatus next)
    {
        SalesOpportunity opportunity = CreateAt(current, includeWinRequirements: next == SalesOpportunityStatus.Won);

        opportunity.MoveTo(next, Utc(12));

        Assert.Equal(next, opportunity.Status);
    }

    [Theory]
    [MemberData(nameof(RejectedTransitions))]
    public void MoveTo_RejectsUndocumentedOrTerminalTransitions(SalesOpportunityStatus current, SalesOpportunityStatus next)
    {
        SalesOpportunity opportunity = CreateAt(current, includeWinRequirements: true);

        DomainException exception = Assert.Throws<DomainException>(() => opportunity.MoveTo(next, Utc(12)));

        Assert.Contains(nameof(SalesOpportunity), exception.Message);
        Assert.Contains(current.ToString(), exception.Message);
        Assert.Contains(next.ToString(), exception.Message);
    }

    [Fact]
    public void MoveTo_WonRequiresAmountAndExpectedCloseDate()
    {
        SalesOpportunity opportunity = CreateAt(SalesOpportunityStatus.Proposed);

        DomainException exception = Assert.Throws<DomainException>(() => opportunity.MoveTo(SalesOpportunityStatus.Won, Utc(12)));

        Assert.Contains(nameof(SalesOpportunity), exception.Message);
        Assert.Contains(SalesOpportunityStatus.Proposed.ToString(), exception.Message);
        Assert.Contains(SalesOpportunityStatus.Won.ToString(), exception.Message);

        opportunity.SetProposal(12500m, Utc(30));
        opportunity.MoveTo(SalesOpportunityStatus.Won, Utc(12));

        Assert.Equal(SalesOpportunityStatus.Won, opportunity.Status);
    }

    [Fact]
    public void MoveTo_RequiresUtcTimestamp()
    {
        SalesOpportunity opportunity = CreateAt(SalesOpportunityStatus.New);

        Assert.Throws<DomainException>(() => opportunity.MoveTo(SalesOpportunityStatus.Contacted, new DateTime(2026, 8, 11)));
    }

    [Fact]
    public void SetProposal_RequiresPositiveAmountAndCloseDate()
    {
        SalesOpportunity opportunity = CreateAt(SalesOpportunityStatus.New);

        Assert.Throws<DomainException>(() => opportunity.SetProposal(0m, Utc(30)));
        Assert.Throws<DomainException>(() => opportunity.SetProposal(12500m, default));
    }

    [Fact]
    public void Create_RequiresPartyAndSiteToBelongToTheBranch()
    {
        Branch branch = Branch.Create("Harbor Office");
        Party unassignedParty = Party.CreateOrganization("Northwind Service Works");
        Branch unassignedBranch = Branch.Create("Remote Office");
        unassignedParty.AssignToBranch(unassignedBranch);
        unassignedParty.AddSite(unassignedBranch, "Pier 8 Workshop");

        Assert.Throws<DomainException>(() => SalesOpportunity.Create(branch, unassignedParty, unassignedParty.Sites.Single()));

        Party assignedParty = Party.CreateOrganization("Northwind Service Works");
        assignedParty.AssignToBranch(branch);
        Party otherParty = Party.CreateOrganization("Contoso Facilities");
        otherParty.AssignToBranch(branch);
        otherParty.AddSite(branch, "Pier 8 Workshop");

        Assert.Throws<DomainException>(() => SalesOpportunity.Create(branch, assignedParty, otherParty.Sites.Single()));

        Branch otherBranch = Branch.Create("Remote Office");
        assignedParty.AssignToBranch(otherBranch);
        assignedParty.AddSite(otherBranch, "Remote Workshop");

        Assert.Throws<DomainException>(() => SalesOpportunity.Create(branch, assignedParty, assignedParty.Sites.Single(site => site.BranchId == otherBranch.Id)));
    }

    private static SalesOpportunity CreateAt(SalesOpportunityStatus status, bool includeWinRequirements = false)
    {
        Branch branch = Branch.Create("Harbor Office");
        Party party = Party.CreateOrganization("Northwind Service Works");
        party.AssignToBranch(branch);
        Site site = CreateSite(party, branch);
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, site);

        foreach (SalesOpportunityStatus next in PathTo(status))
        {
            if (next == SalesOpportunityStatus.Won || includeWinRequirements)
            {
                opportunity.SetProposal(12500m, Utc(30));
            }

            opportunity.MoveTo(next, Utc(10));
        }

        return opportunity;
    }

    private static Site CreateSite(Party party, Branch branch)
    {
        party.AddSite(branch, "Pier 8 Workshop");
        return party.Sites.Single();
    }

    private static IEnumerable<SalesOpportunityStatus> PathTo(SalesOpportunityStatus status) => status switch
    {
        SalesOpportunityStatus.New => [],
        SalesOpportunityStatus.Contacted => [SalesOpportunityStatus.Contacted],
        SalesOpportunityStatus.SurveyScheduled => [SalesOpportunityStatus.Contacted, SalesOpportunityStatus.SurveyScheduled],
        SalesOpportunityStatus.Quoting => [SalesOpportunityStatus.Contacted, SalesOpportunityStatus.SurveyScheduled, SalesOpportunityStatus.Quoting],
        SalesOpportunityStatus.Proposed => [SalesOpportunityStatus.Contacted, SalesOpportunityStatus.SurveyScheduled, SalesOpportunityStatus.Quoting, SalesOpportunityStatus.Proposed],
        SalesOpportunityStatus.Won => [SalesOpportunityStatus.Contacted, SalesOpportunityStatus.SurveyScheduled, SalesOpportunityStatus.Quoting, SalesOpportunityStatus.Proposed, SalesOpportunityStatus.Won],
        SalesOpportunityStatus.Lost => [SalesOpportunityStatus.Lost],
        SalesOpportunityStatus.OnHold => [SalesOpportunityStatus.OnHold],
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static DateTime Utc(int day) => new(2026, 8, day, 9, 0, 0, DateTimeKind.Utc);
}
