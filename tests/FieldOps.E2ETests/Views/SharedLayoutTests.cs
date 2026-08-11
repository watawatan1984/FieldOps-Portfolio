namespace FieldOps.E2ETests.Views;

public sealed class SharedLayoutTests
{
    [Fact]
    public void Shared_layout_displays_the_FieldOps_Portal_name()
    {
        string layout = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "FieldOps.Web", "Views", "Shared", "_Layout.cshtml"));

        Assert.Contains("<title>@ViewData[\"Title\"] - FieldOps Portal</title>", layout, StringComparison.Ordinal);
        Assert.Contains(">FieldOps Portal</a>", layout, StringComparison.Ordinal);
        Assert.Contains("&copy; 2026 - FieldOps Portal -", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/work-history\"", layout, StringComparison.Ordinal);
    }

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