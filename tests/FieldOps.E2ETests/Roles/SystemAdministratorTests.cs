using FieldOps.E2ETests.Infrastructure;
using FieldOps.E2ETests.Pages;
using FieldOps.Infrastructure.Identity;

using Microsoft.Playwright;

namespace FieldOps.E2ETests.Roles;

[Collection(FieldOpsWebCollection.Name)]
public sealed class SystemAdministratorTests(FieldOpsWebFixture fixture)
{
    [Fact]
    public Task AdministratorCompletesPartyAuditAndResetJourney() => fixture.RunAsync(
        nameof(AdministratorCompletesPartyAuditAndResetJourney),
        async (page, errors) =>
        {
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.SystemAdministrator);
            DashboardPage dashboard = new(page);
            string openOpportunities = await dashboard.Metric("open-opportunities").GetAttributeAsync("data-value") ?? string.Empty;
            await page.GetByRole(AriaRole.Link, new() { Name = "Customers", Exact = true }).ClickAsync();
            await new PartyPage(page).EditAsync("架空設備サービス 01", "架空設備サービス 01 E2E");
            Assert.Equal(1, await fixture.QueryScalarAsync<int>(
                "SELECT count(*) FROM \"AuditEntries\" WHERE \"AggregateId\" = '10000000-0000-4000-8000-000000000001' AND \"Action\" = 'Updated'"));
            await page.GetByRole(AriaRole.Link, new() { Name = "Audit", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("Updated", new() { Exact = true }).First).ToBeVisibleAsync();
            await new ResetPage(page).ExecuteOnceAsync();
            Assert.Equal(40, await fixture.QueryScalarAsync<int>("SELECT count(*) FROM \"Parties\""));
            Assert.Equal(30, await fixture.QueryScalarAsync<int>("SELECT count(*) FROM \"SalesOpportunities\""));
            Assert.Equal(80, await fixture.QueryScalarAsync<int>("SELECT count(*) FROM \"WorkOrders\""));
            Assert.Equal(250, await fixture.QueryScalarAsync<int>("SELECT count(*) FROM \"WorkEvents\""));
            Assert.Equal("架空設備サービス 01", await fixture.QueryScalarAsync<string>(
                "SELECT \"OrganizationName\" FROM \"Parties\" WHERE \"Id\" = '10000000-0000-4000-8000-000000000001'"));
            Assert.Equal(openOpportunities, await dashboard.Metric("open-opportunities").GetAttributeAsync("data-value"));
            Assert.Equal(1, await fixture.QueryScalarAsync<int>(
                "UPDATE \"DemoDatasetMarkers\" SET \"DatasetVersion\" = 'unauthorized-e2e' RETURNING 1"));
            try
            {
                errors.ExpectForbiddenNavigation("/administration/reset");
                Assert.Equal(403, (await page.GotoAsync("/administration/reset"))!.Status);
            }
            finally
            {
                Assert.Equal(1, await fixture.QueryScalarAsync<int>(
                    "UPDATE \"DemoDatasetMarkers\" SET \"DatasetVersion\" = '1' RETURNING 1"));
            }
        });
}