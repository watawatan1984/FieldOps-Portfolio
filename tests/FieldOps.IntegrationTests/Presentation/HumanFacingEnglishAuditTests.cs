namespace FieldOps.IntegrationTests.Presentation;

public sealed class HumanFacingEnglishAuditTests
{
    [Fact]
    public void Task10HumanFacingFallbackEnglishDoesNotRemainInReachableUiSources()
    {
        string repositoryRoot = FindRepositoryRoot();
        (string RelativePath, string[] ForbiddenPhrases)[] checks =
        [
            ("src/FieldOps.Web/Views/Home/Privacy.cshtml", ["Privacy Policy", "Use this page"]),
            ("src/FieldOps.Web/Views/Shared/_Layout.cshtml", ["Fictional user", "National", "Fictional demonstration data only"]),
            ("src/FieldOps.Web/Views/WorkOrders/Details.cshtml", ["Not scheduled"]),
            ("src/FieldOps.Features/Work/WorkOrderQueries.cs", ["Assigned technician"]),
            ("src/FieldOps.Features/Work/WorkHistorySearch.cs", ["Assigned technician"]),
            ("src/FieldOps.Features/Sales/SalesQueries.cs", ["Unassigned", "All branches"]),
            ("src/FieldOps.Features/Administration/AuditQueries.cs", ["Former demo user", "Details withheld", "National"])
        ];

        List<string> failures = [];
        foreach ((string relativePath, string[] forbiddenPhrases) in checks)
        {
            string content = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            foreach (string phrase in forbiddenPhrases)
            {
                if (content.Contains(phrase, StringComparison.Ordinal))
                {
                    failures.Add($"{relativePath}: {phrase}");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Task10 human-facing English audit found reachable fallback text: " + string.Join("; ", failures));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FieldOps.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root containing FieldOps.sln was not found.");
    }
}