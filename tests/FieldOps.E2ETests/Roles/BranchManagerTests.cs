using FieldOps.E2ETests.Infrastructure;
using FieldOps.E2ETests.Pages;
using FieldOps.Infrastructure.Identity;

using Microsoft.Playwright;

namespace FieldOps.E2ETests.Roles;

[Collection(FieldOpsWebCollection.Name)]
public sealed class BranchManagerTests(FieldOpsWebFixture fixture)
{
    [Fact]
    public Task BranchManagerEditsPartySchedulesWorkAndCannotReset() => fixture.RunAsync(
        nameof(BranchManagerEditsPartySchedulesWorkAndCannotReset),
        async (page, errors) =>
        {
            await fixture.QueryScalarAsync<int>(
                "UPDATE \"AspNetUsers\" SET \"BranchId\" = '00000000-0000-4000-8000-000000000001' WHERE \"Id\" = '60000000-0000-4000-8000-000000000004' RETURNING 1");
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.BranchManager);
            await Assertions.Expect(page.Locator("[data-user-branch='Fictional Central Service Branch']")).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Customers", Exact = true }).ClickAsync();
            await new PartyPage(page).EditAsync("Fictional Service Customer 01", "Fictional Service Customer 01 Manager");
            await page.GetByRole(AriaRole.Link, new() { Name = "Work orders", Exact = true }).ClickAsync();
            await new WorkOrderPage(page).ScheduleAsync("Fictional Service Customer 01 Manager");
            Assert.Equal(1, await fixture.QueryScalarAsync<int>(
                "SELECT count(*) FROM \"AuditEntries\" WHERE \"AggregateId\" = '30000000-0000-4000-8000-000000000001' AND \"Action\" = 'ScheduledAndAssigned'"));
            errors.ExpectForbiddenNavigation();
            Assert.Equal(403, (await page.GotoAsync("/administration/reset"))!.Status);
        });
}