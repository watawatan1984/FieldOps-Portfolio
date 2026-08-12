using Microsoft.Playwright;

namespace FieldOps.E2ETests.Pages;

public sealed class PartyPage(IPage page)
{
    public async Task EditAsync(string currentName, string newName)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = currentName, Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Edit party", Exact = true }).ClickAsync();
        await page.GetByLabel("Organization name", new() { Exact = true }).FillAsync(newName);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = newName, Exact = true }))
            .ToBeVisibleAsync();
    }
}