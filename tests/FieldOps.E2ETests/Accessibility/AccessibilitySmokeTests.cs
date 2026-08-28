using FieldOps.E2ETests.Infrastructure;
using FieldOps.E2ETests.Pages;
using FieldOps.Infrastructure.Identity;

using Microsoft.Playwright;

namespace FieldOps.E2ETests.Accessibility;

[Collection(FieldOpsWebCollection.Name)]
public sealed class AccessibilitySmokeTests(FieldOpsWebFixture fixture)
{
    private const string ExpectedCsp =
        "default-src 'self'; script-src 'self'; style-src 'self'; style-src-attr 'unsafe-inline'; " +
        "img-src 'self' data:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    [Fact]
    public Task DesktopHasPersistentNavigationLandmarksAndKeyboardFocus() => fixture.RunAsync(
        nameof(DesktopHasPersistentNavigationLandmarksAndKeyboardFocus),
        async (page, _) =>
        {
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.SystemAdministrator);
            await Assertions.Expect(page.GetByRole(AriaRole.Banner)).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Main)).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Contentinfo)).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Primary navigation", Exact = true }))
                .ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#primaryNavigation")).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Tab");
            string accessibleFocus = await page.EvaluateAsync<string>(
                "() => document.activeElement?.getAttribute('aria-label') || document.activeElement?.textContent?.trim() || ''");
            Assert.False(string.IsNullOrWhiteSpace(accessibleFocus));
        });

    [Fact]
    public Task MobileNavigationOpensClosesAndRestoresFocus() => fixture.RunAsync(
        nameof(MobileNavigationOpensClosesAndRestoresFocus),
        async (page, _) =>
        {
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.SystemAdministrator);
            ILocator toggle = page.GetByRole(AriaRole.Button, new() { Name = "Open primary navigation", Exact = true });
            ILocator navigation = page.Locator("#primaryNavigation");
            await Assertions.Expect(navigation).ToBeHiddenAsync();
            await toggle.ClickAsync();
            await Assertions.Expect(navigation).ToBeVisibleAsync();
            await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
            ILocator close = page.GetByRole(AriaRole.Button, new() { Name = "Close navigation", Exact = true });
            await Assertions.Expect(close).ToBeFocusedAsync();
            await close.ClickAsync();
            await Assertions.Expect(navigation).ToBeHiddenAsync();
            await Assertions.Expect(toggle).ToBeFocusedAsync();
            await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
        },
        new ViewportSize { Width = 390, Height = 844 });

    [Fact]
    public Task BootstrapPagesLoadWithStrictCompatibleCspAndNoBrowserErrors() => fixture.RunAsync(
        nameof(BootstrapPagesLoadWithStrictCompatibleCspAndNoBrowserErrors),
        async (page, _) =>
        {
            IResponse response = await page.GotoAsync("/demo-login") ?? throw new InvalidOperationException("Login response missing.");
            Assert.Equal(ExpectedCsp, response.Headers["content-security-policy"]);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "担当する仕事を選んでください", Exact = true }))
                .ToBeVisibleAsync();
            IReadOnlyList<string> unlabeledControls = await page.Locator("button:not([aria-label]), input:not([type=hidden]), select, textarea")
                .EvaluateAllAsync<string[]>(
                    "controls => controls.filter(c => !c.innerText?.trim() && !c.labels?.length && !c.getAttribute('aria-label')).map(c => c.outerHTML)");
            Assert.Empty(unlabeledControls);
        });
}
