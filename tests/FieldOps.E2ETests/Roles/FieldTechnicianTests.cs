using FieldOps.E2ETests.Infrastructure;
using FieldOps.E2ETests.Pages;
using FieldOps.Infrastructure.Identity;

using Microsoft.Playwright;

namespace FieldOps.E2ETests.Roles;

[Collection(FieldOpsWebCollection.Name)]
public sealed class FieldTechnicianTests(FieldOpsWebFixture fixture)
{
    [Fact]
    public Task TechnicianAddsEventCompletesAssignedWorkAndCannotEditParty() => fixture.RunAsync(
        nameof(TechnicianAddsEventCompletesAssignedWorkAndCannotEditParty),
        async (page, errors) =>
        {
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.FieldTechnician);
            await page.Locator("[data-nav='work-orders']").ClickAsync();
            await new WorkOrderPage(page).AddEventAndCompleteAsync("架空設備サービス 02");
            Assert.Equal(4, await fixture.QueryScalarAsync<int>(
                "SELECT \"Status\" FROM \"WorkOrders\" WHERE \"Id\" = '30000000-0000-4000-8000-000000000002'"));
            Assert.Equal(2, await fixture.QueryScalarAsync<int>(
                "SELECT count(*) FROM \"AuditEntries\" WHERE \"AggregateId\" = '30000000-0000-4000-8000-000000000002' AND \"Action\" = 'WorkEventAdded'"));
            errors.ExpectForbiddenNavigation(
                "/parties/10000000-0000-4000-8000-000000000002/edit?branchId=00000000-0000-4000-8000-000000000002");
            Assert.Equal(403, (await page.GotoAsync(
                "/parties/10000000-0000-4000-8000-000000000002/edit?branchId=00000000-0000-4000-8000-000000000002"))!.Status);
        });
}
