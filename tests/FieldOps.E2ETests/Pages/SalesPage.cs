using Microsoft.Playwright;

namespace FieldOps.E2ETests.Pages;

public sealed class SalesPage(IPage page)
{
    public async Task CreateAndAdvanceAsync()
    {
        await page.Locator("[data-nav='sales']").ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "新しい営業案件を登録する", Exact = true }).ClickAsync();
        await page.GetByLabel("顧客", new() { Exact = true }).SelectOptionAsync("10000000-0000-4000-8000-000000000001");
        await page.GetByLabel("現場", new() { Exact = true }).SelectOptionAsync("12000000-0000-4000-8000-000000000001");
        await page.GetByLabel("営業担当者", new() { Exact = true }).SelectOptionAsync("60000000-0000-4000-8000-000000000003");
        await page.GetByLabel("提案金額", new() { Exact = true }).FillAsync("765432");
        await page.GetByLabel("予定日", new() { Exact = true }).FillAsync("2026-12-20");
        await page.GetByRole(AriaRole.Button, new() { Name = "営業案件を登録する", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "この案件を連絡済みにする", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "この案件を現地確認予定にする", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("現地確認予定", new() { Exact = true })).ToBeVisibleAsync();
    }
}
