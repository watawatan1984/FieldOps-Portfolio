using Microsoft.Playwright;

namespace FieldOps.E2ETests.Pages;

public sealed class DemoLoginPage(IPage page)
{
    public async Task LoginAsAsync(string role)
    {
        await page.GotoAsync("/demo-login");
        await page.Locator($"form[data-role=\"{role}\"]").GetByRole(AriaRole.Button).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "ホーム", Exact = true }))
            .ToBeVisibleAsync();
    }
}