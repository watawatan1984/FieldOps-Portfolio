using Microsoft.Playwright;

namespace FieldOps.E2ETests.Pages;

public sealed class DemoLoginPage(IPage page)
{
    public async Task LoginAsAsync(string role)
    {
        await page.GotoAsync("/demo-login");
        await page.GetByRole(AriaRole.Button, new() { Name = $"{RoleLabel(role)}として始める", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard", Exact = true }))
            .ToBeVisibleAsync();
    }

    private static string RoleLabel(string role) => role switch
    {
        "System Administrator" => "システム管理者",
        "Branch Manager" => "支店管理者",
        "Sales Representative" => "営業担当者",
        "Field Technician" => "現場担当者",
        _ => role
    };
}
