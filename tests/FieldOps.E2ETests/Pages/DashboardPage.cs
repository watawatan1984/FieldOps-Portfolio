using Microsoft.Playwright;

namespace FieldOps.E2ETests.Pages;

public sealed class DashboardPage(IPage page)
{
    public ILocator Metric(string name) => page.Locator($"[data-metric='{name}']");

    public ILocator TodayActionCards => page.Locator("[aria-labelledby='today-heading'] [data-action-card]");

    public ILocator FirstTodayActionTitle => TodayActionCards.First.Locator("h3");

    public Task ExpectFirstTodayActionAsync(string title) =>
        Assertions.Expect(FirstTodayActionTitle).ToHaveTextAsync(title);
}