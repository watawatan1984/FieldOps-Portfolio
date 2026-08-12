using Microsoft.Playwright;

namespace FieldOps.E2ETests.Pages;

public sealed class SalesPage(IPage page)
{
    public async Task CreateAndAdvanceAsync()
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Sales", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Create opportunity", Exact = true }).ClickAsync();
        await page.GetByLabel("Party", new() { Exact = true }).SelectOptionAsync("10000000-0000-4000-8000-000000000001");
        await page.GetByLabel("Site", new() { Exact = true }).SelectOptionAsync("12000000-0000-4000-8000-000000000001");
        await page.GetByLabel("Sales owner", new() { Exact = true }).SelectOptionAsync("60000000-0000-4000-8000-000000000003");
        await page.GetByLabel("Proposed amount", new() { Exact = true }).FillAsync("765432");
        await page.GetByLabel("Expected close date", new() { Exact = true }).FillAsync("2026-12-20");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create opportunity", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Move to Contacted", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Move to SurveyScheduled", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("SurveyScheduled", new() { Exact = true })).ToBeVisibleAsync();
    }
}