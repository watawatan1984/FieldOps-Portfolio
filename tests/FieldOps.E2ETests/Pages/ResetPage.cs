using Microsoft.Playwright;

namespace FieldOps.E2ETests.Pages;

public sealed class ResetPage(IPage page)
{
    public async Task ExecuteOnceAsync()
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "初期化", Exact = true }).ClickAsync();
        await page.GetByLabel("確認のため RESET と入力してください", new() { Exact = true }).FillAsync("RESET");
        ILocator form = page.Locator("[data-demo-reset-form]");
        ILocator button = page.GetByRole(AriaRole.Button, new() { Name = "初期化を実行", Exact = true });
        await button.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "デモデータを初期状態に戻しますか" }))
            .ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "実行する", Exact = true }).ClickAsync();
        await Assertions.Expect(button).ToBeDisabledAsync();
        await Assertions.Expect(form).ToHaveAttributeAsync("aria-busy", "true");
        await Assertions.Expect(page.GetByText("初期化しています…", new() { Exact = true })).ToBeVisibleAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => button.ClickAsync(new LocatorClickOptions { Timeout = 250 }));
        await Assertions.Expect(page.GetByText("初期化が完了しました。", new() { Exact = false }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
    }
}