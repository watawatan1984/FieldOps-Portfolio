using FieldOps.E2ETests.Infrastructure;
using FieldOps.E2ETests.Pages;
using FieldOps.Infrastructure.Identity;

using Microsoft.Playwright;

namespace FieldOps.E2ETests.Views;

[Collection(FieldOpsWebCollection.Name)]
public sealed class SharedLayoutTests(FieldOpsWebFixture fixture)
{
    [Fact]
    public void Shared_layout_displays_the_FieldOps_Portal_name()
    {
        string layout = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "FieldOps.Web", "Views", "Shared", "_Layout.cshtml"));

        Assert.Contains("<html lang=\"ja\">", layout, StringComparison.Ordinal);
        Assert.Contains("<title>@ViewData[\"Title\"] - FieldOps 業務ポータル</title>", layout, StringComparison.Ordinal);
        Assert.Contains(">FieldOps 業務ポータル</a>", layout, StringComparison.Ordinal);
        Assert.Contains("&copy; 2026 - FieldOps Portal -", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/work-history\"", layout, StringComparison.Ordinal);
    }

    [Fact]
    public Task Shared_layout_exposes_Japanese_landmarks_and_large_controls() => fixture.RunAsync(
        nameof(Shared_layout_exposes_Japanese_landmarks_and_large_controls),
        async (page, _) =>
        {
            await page.GotoAsync("/demo-login");
            float buttonHeight = await page.Locator(".btn-primary").First.EvaluateAsync<float>("el => el.getBoundingClientRect().height");
            float bodyFont = await page.Locator("body").EvaluateAsync<float>("el => parseFloat(getComputedStyle(el).fontSize)");
            Assert.True(buttonHeight >= 48);
            Assert.True(bodyFont >= 18);

            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.SystemAdministrator);

            await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "ja");
            await Assertions.Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "主なメニュー" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "メニューを開く" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "終了する" })).ToBeVisibleAsync();
        });

    [Fact]
    public Task Confirm_action_modal_preserves_submitter_and_restores_focus() => fixture.RunAsync(
        nameof(Confirm_action_modal_preserves_submitter_and_restores_focus),
        async (page, _) =>
        {
            await new DemoLoginPage(page).LoginAsAsync(DemoRoleNames.SystemAdministrator);
            await page.EvaluateAsync(
                """
                () => {
                  const form = document.createElement('form');
                  form.method = 'post';
                  form.action = '/confirmed-action';
                  form.innerHTML = `
                    <input required name="target" value="A-001" />
                    <button type="submit" name="decision" value="approve"
                            data-confirm-action
                            data-confirm-title="承認しますか"
                            data-confirm-message="A-001を承認します">
                      承認する
                    </button>`;
                  form.addEventListener('submit', event => {
                    event.preventDefault();
                    window.__submitted = Array.from(new FormData(form, event.submitter).entries());
                  });
                  document.querySelector('main').appendChild(form);
                }
                """);

            ILocator submit = page.GetByRole(AriaRole.Button, new() { Name = "承認する", Exact = true });
            await submit.ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "承認しますか", Exact = true }))
                .ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "やめる", Exact = true }).ClickAsync();
            await Assertions.Expect(submit).ToBeFocusedAsync();

            await submit.ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "実行する", Exact = true }).ClickAsync();
            string[][] submitted = await page.EvaluateAsync<string[][]>("() => window.__submitted");
            Assert.Contains(submitted, entry => entry is ["decision", "approve"]);
        });

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
