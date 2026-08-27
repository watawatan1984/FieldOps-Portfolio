using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;

namespace FieldOps.IntegrationTests.Infrastructure;

internal static class TestWorkOrderFactory
{
    public static (SalesOpportunity Opportunity, WorkOrder WorkOrder) CreateFromWon(
        Branch branch,
        Party party,
        Site site)
    {
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, site);
        opportunity.SetProposal(1000m, new DateTime(2026, 9, 1));
        foreach (SalesOpportunityStatus status in new[]
        {
            SalesOpportunityStatus.Contacted,
            SalesOpportunityStatus.SurveyScheduled,
            SalesOpportunityStatus.Quoting,
            SalesOpportunityStatus.Proposed,
            SalesOpportunityStatus.Won
        })
        {
            opportunity.MoveTo(status, new DateTime(2026, 8, 11, 1, 0, 0, DateTimeKind.Utc));
        }

        return (opportunity, WorkOrder.CreateFromWon(opportunity, branch, party, site));
    }
}