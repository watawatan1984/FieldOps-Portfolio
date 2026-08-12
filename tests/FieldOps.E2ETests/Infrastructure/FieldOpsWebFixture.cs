using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

using FieldOps.Features.Administration;
using FieldOps.Infrastructure;
using FieldOps.Infrastructure.Demo;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

using Npgsql;

using Testcontainers.PostgreSql;

namespace FieldOps.E2ETests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FieldOpsWebCollection : ICollectionFixture<FieldOpsWebFixture>
{
    public const string Name = "FieldOps browser application";
}

public sealed class FieldOpsWebFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupDeadline = TimeSpan.FromSeconds(60);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("fieldops_e2e")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private readonly StringBuilder _applicationOutput = new();
    private IPlaywright? _playwright;
    private Process? _webProcess;

    public string BaseUrl { get; private set; } = string.Empty;

    public string ConnectionString => _postgres.GetConnectionString();

    public string ArtifactRoot { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "TestResults", "playwright"));

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ArtifactRoot);
        await _postgres.StartAsync();
        int port = ReserveLoopbackPort();
        BaseUrl = $"http://127.0.0.1:{port}";
        StartApplication(port);
        await WaitForReadyAsync();
        await ResetDatabaseAsync("fixture-initial-seed");

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
        if (_webProcess is { HasExited: false })
        {
            _webProcess.Kill(entireProcessTree: true);
            await _webProcess.WaitForExitAsync();
        }

        _webProcess?.Dispose();
        await File.WriteAllTextAsync(
            Path.Combine(ArtifactRoot, "application.log"),
            _applicationOutput.ToString());
        await _postgres.DisposeAsync();
    }

    public async Task RunAsync(
        string testName,
        Func<IPage, BrowserErrorCollector, Task> test,
        ViewportSize? viewport = null,
        bool resetDatabase = true)
    {
        if (resetDatabase)
        {
            await ResetDatabaseAsync($"e2e-{Guid.NewGuid():N}");
        }

        string artifactName = Sanitize(testName);
        await using IBrowserContext context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = viewport ?? new ViewportSize { Width = 1440, Height = 900 }
        });
        context.SetDefaultTimeout(10_000);
        await context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true
        });
        IPage page = await context.NewPageAsync();
        BrowserErrorCollector errors = new(page);
        try
        {
            await test(page, errors);
            errors.AssertEmpty();
            await context.Tracing.StopAsync();
        }
        catch
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(ArtifactRoot, $"{artifactName}.png"),
                FullPage = true
            });
            await context.Tracing.StopAsync(new TracingStopOptions
            {
                Path = Path.Combine(ArtifactRoot, $"{artifactName}.zip")
            });
            throw;
        }
    }

    public async Task ResetDatabaseAsync(string idempotencyKey)
    {
        ServiceCollection services = new();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddOptions<DemoModeOptions>().Configure(options =>
        {
            options.Enabled = true;
            options.DatasetIdentifier = DemoModeOptions.ApprovedDatasetIdentifier;
            options.DatasetVersion = DemoModeOptions.ApprovedDatasetVersion;
        });
        services.AddFieldOpsInfrastructure(ConnectionString);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IDemoModeVerifier verifier = scope.ServiceProvider.GetRequiredService<IDemoModeVerifier>();
        await verifier.InitializeAsync();
        IDemoResetService reset = scope.ServiceProvider.GetRequiredService<IDemoResetService>();
        _ = await reset.ResetAsync(new DemoResetCommand(
            idempotencyKey,
            DemoDataManifest.UsersByRole[FieldOps.Infrastructure.Identity.DemoRoleNames.SystemAdministrator].Id,
            $"e2e-{Guid.NewGuid():N}"));
    }

    public async Task<T> QueryScalarAsync<T>(string sql)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        object? result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void StartApplication(int port)
    {
        string repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        string webProject = Path.Combine(repositoryRoot, "src", "FieldOps.Web");
        string webAssembly = Path.Combine(webProject, "bin", "Release", "net10.0", "FieldOps.Web.dll");
        ProcessStartInfo startInfo = new("dotnet", $"\"{webAssembly}\"")
        {
            WorkingDirectory = webProject,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["ConnectionStrings__FieldOps"] = ConnectionString;
        startInfo.Environment["DemoMode__Enabled"] = "true";
        startInfo.Environment["DemoMode__DatasetIdentifier"] = DemoModeOptions.ApprovedDatasetIdentifier;
        startInfo.Environment["DemoMode__DatasetVersion"] = DemoModeOptions.ApprovedDatasetVersion;
        _webProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("FieldOps web process did not start.");
        _webProcess.OutputDataReceived += CaptureApplicationOutput;
        _webProcess.ErrorDataReceived += CaptureApplicationOutput;
        _webProcess.BeginOutputReadLine();
        _webProcess.BeginErrorReadLine();
    }

    private async Task WaitForReadyAsync()
    {
        using CancellationTokenSource deadline = new(StartupDeadline);
        using HttpClient client = new() { BaseAddress = new Uri(BaseUrl) };
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (_webProcess?.HasExited == true)
            {
                throw new InvalidOperationException(
                    $"FieldOps web process exited with code {_webProcess.ExitCode}. Output: {_applicationOutput}");
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync("/health/ready", deadline.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
        }
    }

    private void CaptureApplicationOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null)
        {
            lock (_applicationOutput)
            {
                _applicationOutput.AppendLine(args.Data);
            }
        }
    }

    private static int ReserveLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '-'));
}

public sealed class BrowserErrorCollector
{
    private readonly ConcurrentQueue<string> _errors = [];
    private int _expectedForbiddenConsoleErrors;

    public BrowserErrorCollector(IPage page)
    {
        page.PageError += (_, error) => _errors.Enqueue($"pageerror: {error}");
        page.Console += (_, message) =>
        {
            if (!string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (message.Text.Contains("the server responded with a status of 403", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.CompareExchange(ref _expectedForbiddenConsoleErrors, 0, 0) > 0)
            {
                Interlocked.Decrement(ref _expectedForbiddenConsoleErrors);
            }
            else
            {
                _errors.Enqueue($"console: {message.Text}");
            }
        };
    }

    public void ExpectForbiddenNavigation() => Interlocked.Increment(ref _expectedForbiddenConsoleErrors);

    public void AssertEmpty() => Assert.True(
        _errors.Count == 0,
        $"Unexpected browser errors: {string.Join(" | ", _errors)}");
}