using FieldOps.Domain.Common;
using FieldOps.Domain.Enums;

namespace FieldOps.Domain.Entities;

public sealed class SalesOpportunity : Entity
{
    private SalesOpportunity(Branch branch, Party party, Site site)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(site);
        EnsurePartyAndSiteBelongToBranch(branch, party, site);

        BranchId = branch.Id;
        PartyId = party.Id;
        SiteId = site.Id;
        Status = SalesOpportunityStatus.New;
    }

    public Guid BranchId { get; }

    public Guid PartyId { get; }

    public Guid SiteId { get; }

    public SalesOpportunityStatus Status { get; private set; }

    public decimal? ProposedAmount { get; private set; }

    public DateTime? ExpectedCloseDate { get; private set; }

    public static SalesOpportunity Create(Branch branch, Party party, Site site) => new(branch, party, site);

    public void SetProposal(decimal amount, DateTime expectedCloseDate)
    {
        if (amount <= 0)
        {
            throw new DomainException("A sales opportunity proposal amount must be greater than zero.");
        }

        if (expectedCloseDate == default)
        {
            throw new DomainException("A sales opportunity expected close date is required.");
        }

        ProposedAmount = amount;
        ExpectedCloseDate = expectedCloseDate.Date;
        Touch();
    }

    public void MoveTo(SalesOpportunityStatus next, DateTime occurredAtUtc)
    {
        RequireUtc(occurredAtUtc, "sales opportunity transition timestamp");

        if (!Enum.IsDefined(next) || !IsAllowedTransition(Status, next))
        {
            throw InvalidTransition(Status, next);
        }

        if (next == SalesOpportunityStatus.Won && (ProposedAmount is null || ExpectedCloseDate is null))
        {
            throw new DomainException($"SalesOpportunity transition from {Status} to {next} requires a proposal amount and expected close date.");
        }

        Status = next;
        Touch();
    }

    private static bool IsAllowedTransition(SalesOpportunityStatus current, SalesOpportunityStatus next) =>
        (current, next) switch
        {
            (SalesOpportunityStatus.New, SalesOpportunityStatus.Contacted) => true,
            (SalesOpportunityStatus.Contacted, SalesOpportunityStatus.SurveyScheduled) => true,
            (SalesOpportunityStatus.SurveyScheduled, SalesOpportunityStatus.Quoting) => true,
            (SalesOpportunityStatus.Quoting, SalesOpportunityStatus.Proposed) => true,
            (SalesOpportunityStatus.Proposed, SalesOpportunityStatus.Won) => true,
            (SalesOpportunityStatus.New or SalesOpportunityStatus.Contacted or SalesOpportunityStatus.SurveyScheduled or SalesOpportunityStatus.Quoting or SalesOpportunityStatus.Proposed, SalesOpportunityStatus.Lost or SalesOpportunityStatus.OnHold) => true,
            (SalesOpportunityStatus.OnHold, SalesOpportunityStatus.Contacted or SalesOpportunityStatus.SurveyScheduled or SalesOpportunityStatus.Quoting or SalesOpportunityStatus.Proposed or SalesOpportunityStatus.Lost) => true,
            _ => false
        };

    private static void EnsurePartyAndSiteBelongToBranch(Branch branch, Party party, Site site)
    {
        if (!party.BranchAssignments.Any(assignment => assignment.BranchId == branch.Id) ||
            site.PartyId != party.Id ||
            site.BranchId != branch.Id)
        {
            throw new DomainException("A sales opportunity party and site must belong to its branch.");
        }
    }

    private static DomainException InvalidTransition(SalesOpportunityStatus current, SalesOpportunityStatus requested) =>
        new($"SalesOpportunity transition from {current} to {requested} is not allowed.");

    private static void RequireUtc(DateTime value, string fieldName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new DomainException($"The {fieldName} must use UTC.");
        }
    }
}
