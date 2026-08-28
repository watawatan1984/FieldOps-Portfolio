using FieldOps.E2ETests.Infrastructure;
using FieldOps.E2ETests.Pages;
using FieldOps.Infrastructure.Identity;

using Microsoft.Playwright;

namespace FieldOps.E2ETests.Roles;

[Collection(FieldOpsWebCollection.Name)]
public sealed class SalesRepresentativeTests(FieldOpsWebFixture fixture)
{
    [Fact]
    public Task SalesRepresentativeSearchesCreatesAdvancesAndCannotAudit() => fixture.RunAsync(
        nameof(SalesRepresentativeSearchesCreatesAdvancesAndCannotAudit),
        async (page, errors) =>
        {
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.SalesRepresentative);
            await page.GetByRole(AriaRole.Link, new() { Name = "Customers", Exact = true }).ClickAsync();
            await page.GetByLabel("Search name, contact, or site", new() { Exact = true }).FillAsync("架空設備サービス 01");
            await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "架空設備サービス 01", Exact = true }))
                .ToBeVisibleAsync();
            await new SalesPage(page).CreateAndAdvanceAsync();
            Assert.Equal(1, await fixture.QueryScalarAsync<int>(
                "SELECT count(*) FROM \"SalesOpportunities\" WHERE \"ProposedAmount\" = 765432 AND \"Status\" = 3"));
            errors.ExpectForbiddenNavigation("/audit");
            Assert.Equal(403, (await page.GotoAsync("/audit"))!.Status);
        });
}
