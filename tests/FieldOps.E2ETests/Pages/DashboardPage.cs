using Microsoft.Playwright;

namespace FieldOps.E2ETests.Pages;

public sealed class DashboardPage(IPage page)
{
    public ILocator Metric(string name) => page.Locator($"[data-metric='{name}']");
}