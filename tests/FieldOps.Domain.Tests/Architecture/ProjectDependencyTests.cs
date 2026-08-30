using System.Text.Json;
using System.Xml.Linq;

namespace FieldOps.Domain.Tests.Architecture;

public sealed class ProjectDependencyTests
{
    [Fact]
    public void Production_projects_follow_the_allowed_dependency_graph()
    {
        string repositoryRoot = FindRepositoryRoot();

        Assert.Empty(ProjectReferences(repositoryRoot, "src", "FieldOps.Domain", "FieldOps.Domain.csproj"));
        Assert.Equal(
            ["FieldOps.Domain.csproj"],
            ProjectReferences(repositoryRoot, "src", "FieldOps.Features", "FieldOps.Features.csproj"));
        Assert.DoesNotContain(
            PackageReferences(repositoryRoot, "src", "FieldOps.Web", "FieldOps.Web.csproj"),
            package => package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_checks_pass_deploys_require_manual_release_verification()
    {
        string repositoryRoot = FindRepositoryRoot();
        string renderYaml = File.ReadAllText(Path.Combine(repositoryRoot, "render.yaml"));
        string releaseWorkflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml"));

        Assert.Contains("autoDeployTrigger: checksPass", renderYaml, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", releaseWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow_run:", releaseWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Gas_health_monitor_uses_hourly_checks_and_explicit_minimum_scopes()
    {
        string repositoryRoot = FindRepositoryRoot();
        string monitorRoot = Path.Combine(
            repositoryRoot,
            "ops",
            "google-apps-script",
            "fieldops-health-monitor");
        string codePath = Path.Combine(monitorRoot, "Code.gs");
        string manifestPath = Path.Combine(monitorRoot, "appsscript.json");

        Assert.True(File.Exists(codePath), $"Missing GAS source: {codePath}");
        Assert.True(File.Exists(manifestPath), $"Missing GAS manifest: {manifestPath}");

        string code = File.ReadAllText(codePath);
        Assert.Contains("https://fieldops-portfolio.onrender.com", code, StringComparison.Ordinal);
        Assert.Contains("baseUrl !== CONFIG.defaultBaseUrl", code, StringComparison.Ordinal);
        Assert.Contains("/health/live", code, StringComparison.Ordinal);
        Assert.Contains("/health/ready", code, StringComparison.Ordinal);
        Assert.Contains("everyHours(1)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("everyMinutes(", code, StringComparison.Ordinal);
        Assert.Contains("getHandlerFunction() === CONFIG.triggerHandler", code, StringComparison.Ordinal);

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = manifest.RootElement;
        Assert.Equal("Asia/Tokyo", root.GetProperty("timeZone").GetString());
        Assert.Equal("V8", root.GetProperty("runtimeVersion").GetString());

        string[] scopes = root.GetProperty("oauthScopes")
            .EnumerateArray()
            .Select(scope => scope.GetString() ?? string.Empty)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();
        string[] expectedScopes =
        [
            "https://www.googleapis.com/auth/script.external_request",
            "https://www.googleapis.com/auth/script.scriptapp",
            "https://www.googleapis.com/auth/script.send_mail",
            "https://www.googleapis.com/auth/spreadsheets.currentonly",
            "https://www.googleapis.com/auth/userinfo.email"
        ];

        Assert.Equal(expectedScopes.OrderBy(scope => scope, StringComparer.Ordinal), scopes);
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

    private static string[] ProjectReferences(string root, params string[] projectPath)
    {
        return References(root, projectPath, "ProjectReference")
            .Select(reference => Path.GetFileName(
                reference
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar))
                ?? throw new InvalidOperationException("Project reference is missing a file name."))
            .ToArray();
    }

    private static string[] PackageReferences(string root, params string[] projectPath)
    {
        return References(root, projectPath, "PackageReference");
    }

    private static string[] References(string root, string[] projectPath, string elementName)
    {
        string projectFile = Path.Combine([root, .. projectPath]);
        return XDocument.Load(projectFile)
            .Descendants(elementName)
            .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
            .OrderBy(reference => reference, StringComparer.Ordinal)
            .ToArray();
    }
}