using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using FieldOps.IntegrationTests.Administration;
using FieldOps.IntegrationTests.Infrastructure;

namespace FieldOps.IntegrationTests.Load;

[Collection(DatabaseCollection.Name)]
public sealed partial class LoadTestSurfaceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly Task12Postgres _postgres = new(fixture);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _postgres.AssertNoDatabaseActivityAsync();

    [Fact]
    public async Task LoadDiagnosticsAreNotMappedInProduction()
    {
        string connectionString = await _postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString, environment: "Production");
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        using HttpResponseMessage response = await client.GetAsync("/__load-test/postflight");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("LoadTest")]
    public async Task LoadDiagnosticsReturnReadinessCountsAndIntegrityOnlyInLocalTestEnvironments(string environment)
    {
        string connectionString = await _postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString, environment: environment);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        using HttpResponseMessage preflight = await client.PostAsync("/__load-test/preflight?vus=2", null);
        string preflightJson = await preflight.Content.ReadAsStringAsync();
        using HttpResponseMessage postflight = await client.GetAsync("/__load-test/postflight");
        string postflightJson = await postflight.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, preflight.StatusCode);
        Assert.Equal(HttpStatusCode.OK, postflight.StatusCode);

        using JsonDocument preflightDocument = JsonDocument.Parse(preflightJson);
        Assert.True(preflightDocument.RootElement.GetProperty("ready").GetBoolean());
        Assert.True(preflightDocument.RootElement.GetProperty("roleLoginReady").GetBoolean());
        Assert.Equal(0, preflightDocument.RootElement.GetProperty("activeResetCount").GetInt32());

        using JsonDocument postflightDocument = JsonDocument.Parse(postflightJson);
        Assert.True(postflightDocument.RootElement.GetProperty("integrity").GetProperty("passed").GetBoolean());
        Assert.True(postflightDocument.RootElement.GetProperty("counts").GetProperty("branches").GetInt32() > 0);
        Assert.True(postflightDocument.RootElement.GetProperty("counts").GetProperty("parties").GetInt32() >= 42);
    }

    [Fact]
    public async Task LoadRunnerRejectsNonLoopbackTargetsBeforeDockerOrK6Work()
    {
        string scriptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "scripts",
            "run-load-tests.ps1"));

        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell" : "pwsh",
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                scriptPath,
                "-Profile",
                "baseline",
                "-TargetUrl",
                "https://fieldops.example.invalid"
            },
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("PowerShell did not start.");

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("loopback", output + error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadDiagnosticsDoNotEmitCookiesConnectionStringsOrSecrets()
    {
        string connectionString = await _postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString, environment: "LoadTest");
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        using HttpRequestMessage request = new(HttpMethod.Get, "/__load-test/postflight");
        request.Headers.Add("Cookie", ".AspNetCore.Identity.Application=sensitive-cookie-value");
        using HttpResponseMessage response = await client.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("sensitive-cookie-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(SecretLikeTokenRegex(), json);
    }

    [GeneratedRegex("[A-Za-z0-9_=-]{64,}")]
    private static partial Regex SecretLikeTokenRegex();
}