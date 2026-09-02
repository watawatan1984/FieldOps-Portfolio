using FieldOps.E2ETests.Infrastructure;
using FieldOps.Infrastructure.Identity;

using Microsoft.Playwright;

namespace FieldOps.E2ETests.Manual;

/// <summary>
/// Regenerates the screenshots used by the in-app manual (Views/Manual/Index.cshtml) and by
/// docs/evidence/screenshots, referenced from README.md.
///
/// This test is skipped by default so it does not add to the normal Playwright E2E run. To
/// regenerate the images: temporarily delete the `Skip = ...` argument below, then run
///   dotnet test tests/FieldOps.E2ETests/FieldOps.E2ETests.csproj -c Release --filter FullyQualifiedName~ManualScreenshotCaptureTests
/// then restore the `Skip = ...` argument and run `dotnet build -c Release` again so the new
/// files under src/FieldOps.Web/wwwroot/images/manual are included in the static assets
/// manifest that FieldOps.Web serves.
/// </summary>
[Collection(FieldOpsWebCollection.Name)]
public sealed class ManualScreenshotCaptureTests(FieldOpsWebFixture fixture)
{
    private static readonly ViewportSize ManualViewport = new() { Width = 1280, Height = 800 };

    [Fact(Skip = "Screenshot regeneration utility, not a correctness check. See class remarks for how to run it on demand.")]
    public Task CapturesManualScreenshots() => fixture.RunAsync(
        nameof(CapturesManualScreenshots),
        async (page, _) =>
        {
            string repositoryRoot = FindRepositoryRoot();
            string wwwrootTarget = Path.Combine(repositoryRoot, "src", "FieldOps.Web", "wwwroot", "images", "manual");
            string evidenceTarget = Path.Combine(repositoryRoot, "docs", "evidence", "screenshots");
            Directory.CreateDirectory(wwwrootTarget);
            Directory.CreateDirectory(evidenceTarget);

            async Task SaveAsync(string fileName)
            {
                byte[] bytes = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = false });
                await File.WriteAllBytesAsync(Path.Combine(wwwrootTarget, fileName), bytes);
                await File.WriteAllBytesAsync(Path.Combine(evidenceTarget, fileName), bytes);
            }

            // 1. Demo login (unauthenticated entry point).
            await page.GotoAsync("/demo-login");
            await Assertions.Expect(page.Locator($"form[data-role=\"{DemoRoleNames.SystemAdministrator}\"]")).ToBeVisibleAsync();
            await SaveAsync("01-demo-login.png");

            // Sign in as System Administrator: this single role can see every screen below.
            await page.Locator($"form[data-role=\"{DemoRoleNames.SystemAdministrator}\"]").GetByRole(AriaRole.Button).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "ホーム", Exact = true })).ToBeVisibleAsync();

            // 2. Dashboard.
            await SaveAsync("02-dashboard.png");

            // 3. Customers.
            await page.GotoAsync("/customers");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "顧客を探す", Exact = true })).ToBeVisibleAsync();
            await SaveAsync("03-customers.png");

            // 4. Business partners.
            await page.GotoAsync("/business-partners");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "協力会社を探す", Exact = true })).ToBeVisibleAsync();
            await SaveAsync("04-business-partners.png");

            // 5. Sales opportunities: list.
            await page.GotoAsync("/sales");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "営業案件", Exact = true })).ToBeVisibleAsync();
            await SaveAsync("05-sales-list.png");

            // 6. Sales opportunities: detail.
            await page.Locator(".responsive-records table tbody a").First.ClickAsync();
            await Assertions.Expect(page.Locator("#sales-summary-heading")).ToBeVisibleAsync();
            await SaveAsync("06-sales-detail.png");

            // 7. Quotes: list.
            await page.GotoAsync("/quotes");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "見積", Exact = true })).ToBeVisibleAsync();
            await SaveAsync("07-quotes-list.png");

            // 8. Quotes: detail.
            await page.Locator(".responsive-records table tbody a").First.ClickAsync();
            await Assertions.Expect(page.Locator("#quote-summary-heading")).ToBeVisibleAsync();
            await SaveAsync("08-quotes-detail.png");

            // 9. Quotes: create form. Navigate via the breadcrumb so the branch stays selected
            // (System Administrator browsing all branches at once has no "create" button; the
            // quote we just viewed pins a single branch, which is enough to unlock it).
            await page.Locator(".breadcrumb a[href^='/quotes']").ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "新しい見積を登録する" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "見積を登録する", Exact = true })).ToBeVisibleAsync();
            await SaveAsync("09-quotes-create.png");

            // 10. Work orders.
            await page.GotoAsync("/work-orders");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "作業予定", Exact = true })).ToBeVisibleAsync();
            await SaveAsync("10-work-orders.png");

            // 11. Work history.
            await page.GotoAsync("/work-history");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "作業履歴", Exact = true })).ToBeVisibleAsync();
            await SaveAsync("11-work-history.png");

            // 12. Audit trail.
            await page.GotoAsync("/audit");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "変更履歴", Exact = true })).ToBeVisibleAsync();
            await SaveAsync("12-audit.png");

            // 13. Branch progress.
            await page.GotoAsync("/branches");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "支店状況", Exact = true })).ToBeVisibleAsync();
            await SaveAsync("13-branches.png");

            // 14. Demo reset confirmation (not submitted).
            await page.GotoAsync("/administration/reset");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "デモデータを初期状態に戻す", Exact = true })).ToBeVisibleAsync();
            await SaveAsync("14-reset.png");
        },
        viewport: ManualViewport);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FieldOps.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate FieldOps.sln.");
    }
}