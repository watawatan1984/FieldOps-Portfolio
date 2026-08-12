using Microsoft.Playwright;

namespace FieldOps.E2ETests.Pages;

public sealed class WorkOrderPage(IPage page)
{
    public async Task ScheduleAsync(string partyName)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = partyName, Exact = true }).First.ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Schedule and assign", Exact = true }).ClickAsync();
        await page.GetByLabel("Assigned technician", new() { Exact = true }).SelectOptionAsync("60000000-0000-4000-8000-000000000004");
        await page.GetByLabel("Scheduled start (UTC)", new() { Exact = true }).FillAsync("2026-12-10T01:30:00Z");
        await page.GetByRole(AriaRole.Button, new() { Name = "Schedule and assign", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Scheduled", new() { Exact = true })).ToBeVisibleAsync();
    }

    public async Task AddEventAndCompleteAsync(string partyName)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = partyName, Exact = true }).First.ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Add work event", Exact = true }).ClickAsync();
        await page.GetByLabel("EventType", new() { Exact = true }).SelectOptionAsync("Note");
        await page.GetByLabel("OccurredAtUtc", new() { Exact = true }).FillAsync("2026-08-01T02:15:00Z");
        await page.GetByLabel("Summary", new() { Exact = true }).FillAsync("E2E technician inspection note");
        await page.GetByRole(AriaRole.Button, new() { Name = "Append event", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Move to InProgress", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Add work event", Exact = true }).ClickAsync();
        await page.GetByLabel("EventType", new() { Exact = true }).SelectOptionAsync("Completion");
        await page.GetByLabel("OccurredAtUtc", new() { Exact = true }).FillAsync("2026-08-01T03:15:00Z");
        await page.GetByLabel("Summary", new() { Exact = true }).FillAsync("E2E technician completion evidence");
        await page.GetByRole(AriaRole.Button, new() { Name = "Append event", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Move to Completed", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Completed", new() { Exact = true })).ToBeVisibleAsync();
    }
}