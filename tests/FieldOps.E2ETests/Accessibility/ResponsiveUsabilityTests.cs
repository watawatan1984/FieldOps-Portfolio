using FieldOps.E2ETests.Infrastructure;
using FieldOps.E2ETests.Pages;
using FieldOps.Infrastructure.Identity;

using Microsoft.Playwright;

namespace FieldOps.E2ETests.Accessibility;

[Collection(FieldOpsWebCollection.Name)]
public sealed class ResponsiveUsabilityTests(FieldOpsWebFixture fixture)
{
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(768, 1024)]
    public Task PrimaryJourneysRemainUsableAtSupportedViewports(int width, int height) => fixture.RunAsync(
        $"{nameof(PrimaryJourneysRemainUsableAtSupportedViewports)}-{width}x{height}",
        async (page, _) =>
        {
            await page.SetViewportSizeAsync(width, height);
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.FieldTechnician);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "今日やること", Exact = true }))
                .ToBeVisibleAsync();
            await AssertNoHorizontalViewportOverflowAsync(page, "field technician dashboard");
            await AssertVisibleInteractiveElementsFitHorizontallyAsync(page, "field technician dashboard");

            if (width < 992)
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "メニューを開く", Exact = true }).ClickAsync();
                await WaitForOpenedNavigationAsync(page);
                await AssertVisibleInteractiveElementsFitHorizontallyAsync(page, "field technician opened navigation");
            }

            await page.Locator("[data-nav='work-orders']").ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "作業予定", Exact = true }))
                .ToBeVisibleAsync();
            if (width < 1200)
            {
                await Assertions.Expect(page.Locator("table")).ToBeHiddenAsync();
            }

            await AssertNoHorizontalViewportOverflowAsync(page, "field technician work orders");
            await AssertVisibleInteractiveElementsFitHorizontallyAsync(page, "field technician work orders");
        },
        new ViewportSize { Width = width, Height = height });

    [Fact]
    public Task EquivalentTwoHundredPercentReflowKeepsContentAndActionsReachable() => fixture.RunAsync(
        nameof(EquivalentTwoHundredPercentReflowKeepsContentAndActionsReachable),
        async (page, _) =>
        {
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.BranchManager);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "今日やること", Exact = true }))
                .ToBeVisibleAsync();
            await AssertNoHorizontalViewportOverflowAsync(page, "200 percent dashboard reflow");
            await AssertVisibleInteractiveElementsFitHorizontallyAsync(page, "200 percent dashboard reflow");

            await page.GetByRole(AriaRole.Button, new() { Name = "メニューを開く", Exact = true }).ClickAsync();
            await WaitForOpenedNavigationAsync(page);
            await AssertVisibleInteractiveElementsFitHorizontallyAsync(page, "200 percent opened navigation");
            await page.Locator("[data-nav='customers']").ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "顧客を探す", Exact = true }))
                .ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("table")).ToBeHiddenAsync();
            await AssertNoHorizontalViewportOverflowAsync(page, "200 percent customer list reflow");
            await AssertVisibleInteractiveElementsFitHorizontallyAsync(page, "200 percent customer list reflow");
        },
        new ViewportSize { Width = 384, Height = 512 });

    [Fact]
    public Task LongJapaneseNamesWrapWithoutHorizontalOverflow() => fixture.RunAsync(
        nameof(LongJapaneseNamesWrapWithoutHorizontalOverflow),
        async (page, _) =>
        {
            const string longName = "とても長い日本語の架空設備保守サービス株式会社中央支店第一工場空調設備更新相談窓口";
            const string longSite = "とても長い日本語の中央第一工場地下機械室高効率空調設備更新予定現場";

            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.BranchManager);
            await page.GetByRole(AriaRole.Button, new() { Name = "メニューを開く", Exact = true }).ClickAsync();
            await WaitForOpenedNavigationAsync(page);
            await page.Locator("[data-nav='customers']").ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "新しい顧客を登録する", Exact = true }).ClickAsync();
            await page.GetByLabel("組織名", new() { Exact = true }).FillAsync(longName);
            await page.GetByLabel("登録区分", new() { Exact = true }).SelectOptionAsync("Customer");
            await page.GetByLabel("担当者の姓", new() { Exact = true }).FillAsync("長文");
            await page.GetByLabel("担当者の名", new() { Exact = true }).FillAsync("確認");
            await page.GetByLabel("現場名", new() { Exact = true }).FillAsync(longSite);
            await page.GetByRole(AriaRole.Button, new() { Name = "この内容で顧客を登録する", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = longName, Exact = true }))
                .ToBeVisibleAsync();
            await AssertNoHorizontalViewportOverflowAsync(page, "long Japanese details");
            await AssertVisibleInteractiveElementsFitHorizontallyAsync(page, "long Japanese details");
        },
        new ViewportSize { Width = 384, Height = 512 });

    [Fact]
    public Task KeyboardCanTabThroughPrimaryWorkOrderJourneyAndRecoverDialogFocus() => fixture.RunAsync(
        nameof(KeyboardCanTabThroughPrimaryWorkOrderJourneyAndRecoverDialogFocus),
        async (page, _) =>
        {
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.FieldTechnician);

            ILocator menuButton = page.GetByRole(AriaRole.Button, new() { Name = "メニューを開く", Exact = true });
            await PressTabUntilFocusedAsync(page, menuButton, "menu button");
            await AssertFocusedElementHasVisibleOutlineAsync(page, "menu button");

            ILocator workOrdersNavigation = page.Locator("[data-nav='work-orders']");
            await PressTabUntilFocusedAsync(page, workOrdersNavigation, "work orders navigation");
            await AssertFocusedElementHasVisibleOutlineAsync(page, "work orders navigation");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "作業予定", Exact = true }))
                .ToBeVisibleAsync();

            ILocator workOrderCard = page.GetByRole(AriaRole.Link, new() { Name = "架空設備サービス 02", Exact = true }).First;
            await PressTabUntilFocusedAsync(page, workOrderCard, "work order card");
            await AssertFocusedElementHasVisibleOutlineAsync(page, "work order card");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "架空設備サービス 02", Exact = true }))
                .ToBeVisibleAsync();

            ILocator addEventLink = page.GetByRole(AriaRole.Link, new() { Name = "作業記録を追加する", Exact = true });
            await PressTabUntilFocusedAsync(page, addEventLink, "add work event link");
            await AssertFocusedElementHasVisibleOutlineAsync(page, "add work event link");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "作業記録を追加する", Exact = true }))
                .ToBeVisibleAsync();

            ILocator backLink = page.GetByRole(AriaRole.Link, new() { Name = "前の画面へ戻る", Exact = true });
            await PressTabUntilFocusedAsync(page, backLink, "work event back link");
            await AssertFocusedElementHasVisibleOutlineAsync(page, "work event back link");
            ILocator saveButton = page.GetByRole(AriaRole.Button, new() { Name = "作業記録を保存する", Exact = true });
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(saveButton).ToBeFocusedAsync();
            await AssertFocusedElementHasVisibleOutlineAsync(page, "work event save button");

            await PressShiftTabUntilFocusedAsync(page, backLink, "work event back link");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "架空設備サービス 02", Exact = true }))
                .ToBeVisibleAsync();
            ILocator transitionButton = page.GetByRole(AriaRole.Button, new() { Name = "作業を開始する", Exact = true });
            await PressTabUntilFocusedAsync(page, transitionButton, "start work transition button");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "作業を開始しますか", Exact = true }))
                .ToBeVisibleAsync();
            ILocator cancelButton = page.GetByRole(AriaRole.Button, new() { Name = "やめる", Exact = true });
            await PressTabUntilFocusedAsync(page, cancelButton, "confirmation cancel button", 4);
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(transitionButton).ToBeFocusedAsync();
            await AssertFocusedElementHasVisibleOutlineAsync(page, "cancelled confirmation button");
        });

    [Fact]
    public Task MobileMenuRestoresFocusToMenuButtonAfterClosing() => fixture.RunAsync(
        nameof(MobileMenuRestoresFocusToMenuButtonAfterClosing),
        async (page, _) =>
        {
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.SystemAdministrator);
            ILocator toggle = page.GetByRole(AriaRole.Button, new() { Name = "メニューを開く", Exact = true });
            await toggle.ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "メニューを閉じる", Exact = true }).ClickAsync();
            await Assertions.Expect(toggle).ToBeFocusedAsync();
            await AssertFocusedElementHasVisibleOutlineAsync(page, "restored menu button");
        },
        new ViewportSize { Width = 768, Height = 1024 });

    [Fact]
    public Task ReducedMotionStopsDecorativeTransitions() => fixture.RunAsync(
        nameof(ReducedMotionStopsDecorativeTransitions),
        async (page, _) =>
        {
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.SystemAdministrator);
            string transitionDuration = await page.GetByRole(AriaRole.Button, new() { Name = "メニューを開く", Exact = true })
                .EvaluateAsync<string>("element => getComputedStyle(element).transitionDuration");
            string offcanvasTransitionDuration = await page.Locator("#primaryNavigation")
                .EvaluateAsync<string>("element => getComputedStyle(element).transitionDuration");
            Assert.Equal("0s", transitionDuration);
            Assert.Equal("0s", offcanvasTransitionDuration);
        },
        new ViewportSize { Width = 768, Height = 1024 },
        ReducedMotion.Reduce);

    private static async Task AssertNoHorizontalViewportOverflowAsync(IPage page, string context)
    {
        bool hasOverflow = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1");
        Assert.False(hasOverflow, $"{context} should not create document-level horizontal scrolling.");
    }

    private static async Task AssertVisibleInteractiveElementsFitHorizontallyAsync(IPage page, string context)
    {
        string[] offscreenActions = await page.Locator(
                "a[href], button, input:not([type=hidden]), select, textarea, summary, [tabindex]:not([tabindex=\"-1\"])")
            .EvaluateAllAsync<string[]>(
                """
                elements => {
                  const isVisible = element => {
                    const style = getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.visibility !== 'hidden' &&
                      style.display !== 'none' &&
                      rect.width > 0 &&
                      rect.height > 0;
                  };
                  return elements
                    .filter(isVisible)
                    .map(element => {
                      element.scrollIntoView({ block: 'nearest', inline: 'nearest' });
                      const rect = element.getBoundingClientRect();
                      const name = element.getAttribute('aria-label') || element.textContent?.trim() || element.labels?.[0]?.textContent?.trim() || element.value || element.tagName;
                      return { name, left: rect.left, right: rect.right };
                    })
                    .filter(item => item.right > window.innerWidth + 1 || item.left < -1)
                    .map(item => `${item.name} (${Math.round(item.left)},${Math.round(item.right)})`);
                }
                """);
        Assert.Empty(offscreenActions);
    }

    private static async Task AssertFocusedElementHasVisibleOutlineAsync(IPage page, string context)
    {
        string outlineStyle = await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.activeElement).outlineStyle");
        float outlineWidth = await page.EvaluateAsync<float>(
            "() => parseFloat(getComputedStyle(document.activeElement).outlineWidth)");
        Assert.NotEqual("none", outlineStyle);
        Assert.True(outlineWidth >= 2, $"{context} should expose a visible outline.");
    }

    private static async Task WaitForOpenedNavigationAsync(IPage page)
    {
        await Assertions.Expect(page.Locator("#primaryNavigation.show")).ToBeVisibleAsync();
    }

    private static async Task PressTabUntilFocusedAsync(IPage page, ILocator expected, string context, int maxTabs = 40)
    {
        for (int count = 0; count < maxTabs; count++)
        {
            await page.Keyboard.PressAsync("Tab");
            if (await expected.EvaluateAsync<bool>("element => element === document.activeElement"))
            {
                await Assertions.Expect(expected).ToBeFocusedAsync();
                return;
            }
        }

        string activeText = await page.EvaluateAsync<string>(
            "() => document.activeElement?.getAttribute('aria-label') || document.activeElement?.textContent?.trim() || document.activeElement?.tagName || ''");
        Assert.Fail($"Tab did not reach {context}. Active element: {activeText}");
    }

    private static async Task PressShiftTabUntilFocusedAsync(IPage page, ILocator expected, string context, int maxTabs = 12)
    {
        for (int count = 0; count < maxTabs; count++)
        {
            await page.Keyboard.PressAsync("Shift+Tab");
            if (await expected.EvaluateAsync<bool>("element => element === document.activeElement"))
            {
                await Assertions.Expect(expected).ToBeFocusedAsync();
                return;
            }
        }

        string activeText = await page.EvaluateAsync<string>(
            "() => document.activeElement?.getAttribute('aria-label') || document.activeElement?.textContent?.trim() || document.activeElement?.tagName || ''");
        Assert.Fail($"Shift+Tab did not reach {context}. Active element: {activeText}");
    }
}
