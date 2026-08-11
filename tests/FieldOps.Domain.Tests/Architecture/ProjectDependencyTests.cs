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
            .Select(reference => Path.GetFileName(reference) ?? throw new InvalidOperationException("Project reference is missing a file name."))
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
