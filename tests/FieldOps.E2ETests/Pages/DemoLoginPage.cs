using Microsoft.Playwright;

namespace FieldOps.E2ETests.Pages;

public sealed class DemoLoginPage(IPage page)
{
    public async Task LoginAsAsync(string role)
    {
        await page.GotoAsync("/demo-login");
        await page.GetByRole(AriaRole.Button, new() { Name = $"Continue as {role}", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard", Exact = true }))
            .ToBeVisibleAsync();
    }
}