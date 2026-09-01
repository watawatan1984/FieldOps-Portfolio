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
            await new DashboardPage(page).ExpectFirstTodayActionAsync("期限が近い提案");
            await page.Locator("[data-nav='customers']").ClickAsync();
            await page.GetByLabel("顧客名・担当者名・現場名で検索", new() { Exact = true }).FillAsync("架空設備サービス 001");
            await page.GetByRole(AriaRole.Button, new() { Name = "この条件で探す", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "架空設備サービス 001", Exact = true }))
                .ToBeVisibleAsync();
            await new SalesPage(page).CreateAndAdvanceAsync();
            Assert.Equal(1, await fixture.QueryScalarAsync<int>(
                "SELECT count(*) FROM \"SalesOpportunities\" WHERE \"ProposedAmount\" = 765432 AND \"Status\" = 3"));
            errors.ExpectForbiddenNavigation("/audit");
            Assert.Equal(403, (await page.GotoAsync("/audit"))!.Status);
        });

    [Fact]
    public Task TabletLandscapeCustomerListUsesCards() => fixture.RunAsync(
        nameof(TabletLandscapeCustomerListUsesCards),
        async (page, errors) =>
        {
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.SalesRepresentative);
            await page.Locator("[data-nav='customers']").ClickAsync();

            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "顧客の情報を見る" }).First)
                .ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("table")).ToBeHiddenAsync();
        },
        new ViewportSize { Width = 1024, Height = 768 });
}