using Microsoft.Playwright;

namespace FieldOps.E2ETests.Pages;

public sealed class WorkOrderPage(IPage page)
{
    public async Task ScheduleAsync(string partyName)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = partyName, Exact = true }).First.ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "日程と担当者を決める", Exact = true }).ClickAsync();
        await page.GetByLabel("担当者", new() { Exact = true }).SelectOptionAsync("60000000-0000-4000-8000-000000000004");
        await page.GetByLabel("作業日", new() { Exact = true }).FillAsync("2026-12-10");
        await page.GetByLabel("開始時刻", new() { Exact = true }).FillAsync("10:30");
        await page.GetByRole(AriaRole.Button, new() { Name = "日程と担当者を保存する", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("予定あり", new() { Exact = true })).ToBeVisibleAsync();
    }

    public async Task AddEventAndCompleteAsync(string partyName)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = partyName, Exact = true }).First.ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "作業記録を追加する", Exact = true }).ClickAsync();
        await page.GetByLabel("作業内容", new() { Exact = true }).SelectOptionAsync("Note");
        await page.GetByLabel("記録日", new() { Exact = true }).FillAsync("2026-08-01");
        await page.GetByLabel("記録時刻", new() { Exact = true }).FillAsync("11:15");
        await page.GetByLabel("記録内容", new() { Exact = true }).FillAsync("E2E technician inspection note");
        await page.GetByRole(AriaRole.Button, new() { Name = "作業記録を保存する", Exact = true }).ClickAsync();
        await ConfirmTransitionAsync("作業を開始する");
        await page.GetByRole(AriaRole.Link, new() { Name = "作業記録を追加する", Exact = true }).ClickAsync();
        await page.GetByLabel("作業内容", new() { Exact = true }).SelectOptionAsync("Completion");
        await page.GetByLabel("記録日", new() { Exact = true }).FillAsync("2026-08-01");
        await page.GetByLabel("記録時刻", new() { Exact = true }).FillAsync("12:15");
        await page.GetByLabel("記録内容", new() { Exact = true }).FillAsync("E2E technician completion evidence");
        await page.GetByRole(AriaRole.Button, new() { Name = "作業記録を保存する", Exact = true }).ClickAsync();
        await ConfirmTransitionAsync("作業を完了する");
        await Assertions.Expect(page.GetByText("完了", new() { Exact = true })).ToBeVisibleAsync();
    }

    private async Task ConfirmTransitionAsync(string buttonName)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = buttonName, Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "実行する", Exact = true }).ClickAsync();
    }
}
