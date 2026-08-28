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

    [Fact]
    public void ReachableBadRequestAndValidationAttributesDoNotUseDefaultEnglishMessages()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] controllerPaths =
        [
            "src/FieldOps.Web/Controllers/PartiesController.cs",
            "src/FieldOps.Web/Controllers/CustomersController.cs",
            "src/FieldOps.Web/Controllers/BusinessPartnersController.cs",
            "src/FieldOps.Web/Controllers/SalesController.cs",
            "src/FieldOps.Web/Controllers/WorkHistoryController.cs",
            "src/FieldOps.Web/Controllers/AuditController.cs"
        ];
        string[] dtoPaths =
        [
            "src/FieldOps.Features/Parties/PartyDtos.cs",
            "src/FieldOps.Features/Sales/SalesDtos.cs",
            "src/FieldOps.Features/Work/WorkOrderDtos.cs",
            "src/FieldOps.Web/Models/WorkHistorySearchViewModel.cs"
        ];
        List<string> failures = [];

        foreach (string relativePath in controllerPaths)
        {
            string content = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            if (content.Contains("BadRequest(ModelState)", StringComparison.Ordinal))
            {
                failures.Add($"{relativePath}: BadRequest(ModelState)");
            }
            if (content.Contains("BadRequest(exception.Message)", StringComparison.Ordinal))
            {
                failures.Add($"{relativePath}: BadRequest(exception.Message)");
            }
        }

        foreach (string relativePath in dtoPaths)
        {
            string content = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            string[] lines = content.Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if ((line.StartsWith("[Required", StringComparison.Ordinal) ||
                     line.StartsWith("[StringLength", StringComparison.Ordinal) ||
                     line.StartsWith("[Range", StringComparison.Ordinal) ||
                     line.StartsWith("[EnumDataType", StringComparison.Ordinal)) &&
                    !line.Contains("ErrorMessage", StringComparison.Ordinal))
                {
                    failures.Add($"{relativePath}:{index + 1}: {line}");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Reachable BadRequest or validation defaults can expose English text: " + string.Join("; ", failures));
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