using Microsoft.Playwright;

namespace FieldOps.E2ETests.Pages;

public sealed class DashboardPage(IPage page)
{
    public ILocator Metric(string name) => page.Locator($"[data-metric='{name}']");

    public ILocator TodayActionCards => page.Locator("[aria-labelledby='today-heading'] [data-action-card]");

    public ILocator FirstTodayActionTitle => TodayActionCards.First.Locator("h3");

    public Task ExpectFirstTodayActionAsync(string title) =>
        Assertions.Expect(FirstTodayActionTitle).ToHaveTextAsync(title);

    public async Task ExpectWorkOrderCardMatchesListAsync(
        string key,
        string title,
        string expectedQuery)
    {
        ILocator card = page.Locator($"[data-action-card='{key}']");
        await Assertions.Expect(card.Locator("h3")).ToHaveTextAsync(title);
        string count = (await card.Locator(".fs-3").TextContentAsync())?.Trim() ?? string.Empty;
        string? href = await card.Locator("a").GetAttributeAsync("href");
        Assert.NotNull(href);
        Assert.Contains(expectedQuery, href, StringComparison.Ordinal);

        await card.Locator("a").ClickAsync();

        Assert.Contains(expectedQuery, page.Url, StringComparison.Ordinal);
        await Assertions.Expect(page.GetByText($"全{count}件")).ToBeVisibleAsync();
    }
}