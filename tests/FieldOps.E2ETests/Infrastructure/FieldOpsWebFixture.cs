using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
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
    private static readonly TimeSpan ArtifactDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(10);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("fieldops_e2e")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private readonly StringBuilder _applicationOutput = new();
    private IPlaywright? _playwright;
    private Process? _webProcess;
    private int _disposed;

    public string BaseUrl { get; private set; } = string.Empty;

    public string ConnectionString => _postgres.GetConnectionString();

    public string ArtifactRoot { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "TestResults", "playwright"));

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ArtifactRoot);
        ClearLegacyFlatFailureArtifacts();
        await _postgres.StartAsync();
        await StartApplicationWithRetryAsync();
        await ResetDatabaseAsync("fixture-initial-seed");

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
    }

    public async Task DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        BoundedCleanupRunner cleanup = new(CleanupDeadline);
        IReadOnlyList<string> diagnostics = await cleanup.RunAsync(
        [
            new("browser", async token =>
            {
                if (Browser is not null)
                {
                    await Browser.DisposeAsync().AsTask().WaitAsync(token);
                }
            }),
            new("playwright", token => Task.Run(() => _playwright?.Dispose(), token)),
            new("application-process", async token =>
            {
                if (_webProcess is null)
                {
                    return;
                }

                if (!_webProcess.HasExited)
                {
                    _webProcess.Kill(entireProcessTree: true);
                    await _webProcess.WaitForExitAsync(token);
                }

                _webProcess.Dispose();
            }),
            new("application-log", async token =>
            {
                string output;
                lock (_applicationOutput)
                {
                    output = _applicationOutput.ToString();
                }

                await File.WriteAllTextAsync(
                    Path.Combine(ArtifactRoot, "application.log"),
                    output,
                    token);
            }),
            new("npgsql-pool", token => Task.Run(() =>
            {
                using NpgsqlConnection ownedPool = new(ConnectionString);
                NpgsqlConnection.ClearPool(ownedPool);
            }, token)),
            new("postgres-container", token => _postgres.DisposeAsync().AsTask().WaitAsync(token), TimeSpan.FromSeconds(30))
        ]);

        if (diagnostics.Count > 0)
        {
            try
            {
                using CancellationTokenSource deadline = new(ArtifactDeadline);
                await File.WriteAllLinesAsync(
                    Path.Combine(ArtifactRoot, "cleanup-diagnostics.log"),
                    diagnostics,
                    deadline.Token);
            }
            catch
            {
            }
        }
        else
        {
            try
            {
                string staleDiagnostics = Path.Combine(ArtifactRoot, "cleanup-diagnostics.log");
                if (File.Exists(staleDiagnostics))
                {
                    File.Delete(staleDiagnostics);
                }
            }
            catch
            {
            }
        }
    }

    public async Task RunAsync(
        string testName,
        Func<IPage, BrowserErrorCollector, Task> test,
        ViewportSize? viewport = null,
        bool resetDatabase = true,
        FailureArtifactHooks? failureArtifactHooks = null)
    {
        if (resetDatabase)
        {
            await ResetDatabaseAsync($"e2e-{Guid.NewGuid():N}");
        }

        string runDirectory = Path.Combine(
            ArtifactRoot,
            "failures",
            Sanitize(testName),
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);

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
        }
        catch (Exception original)
        {
            await CaptureFailureArtifactsAsync(
                page,
                context,
                runDirectory,
                failureArtifactHooks ?? FailureArtifactHooks.Default);
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }

        await context.Tracing.StopAsync().WaitAsync(ArtifactDeadline);
        Directory.Delete(runDirectory, recursive: true);
    }

    public IReadOnlyList<string> GetFailureRunDirectories(string testName)
    {
        string testDirectory = Path.Combine(ArtifactRoot, "failures", Sanitize(testName));
        return Directory.Exists(testDirectory)
            ? Directory.GetDirectories(testDirectory).OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : [];
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

    public async Task<T> QuerySingleAsync<T>(string sql, Func<NpgsqlDataReader, T> projector)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The E2E database query did not return a row.");
        T result = projector(reader);
        Assert.False(await reader.ReadAsync(), "The E2E database query returned more than one row.");
        return result;
    }

    private async Task CaptureFailureArtifactsAsync(
        IPage page,
        IBrowserContext context,
        string runDirectory,
        FailureArtifactHooks hooks)
    {
        ConcurrentQueue<string> diagnostics = [];
        if (page.IsClosed)
        {
            diagnostics.Enqueue("screenshot:page-closed");
        }
        else
        {
            await CaptureArtifactStageAsync(
                "screenshot",
                token => hooks.ScreenshotAsync(page, Path.Combine(runDirectory, "screenshot.png"), token),
                diagnostics);
        }

        await CaptureArtifactStageAsync(
            "trace",
            token => hooks.TraceAsync(context, Path.Combine(runDirectory, "trace.zip"), token),
            diagnostics);

        if (!diagnostics.IsEmpty)
        {
            try
            {
                using CancellationTokenSource deadline = new(ArtifactDeadline);
                await File.WriteAllLinesAsync(
                    Path.Combine(runDirectory, "artifact-diagnostics.txt"),
                    diagnostics,
                    deadline.Token);
            }
            catch
            {
            }
        }
    }

    private static async Task CaptureArtifactStageAsync(
        string stage,
        Func<CancellationToken, Task> capture,
        ConcurrentQueue<string> diagnostics)
    {
        using CancellationTokenSource deadline = new(ArtifactDeadline);
        try
        {
            await capture(deadline.Token).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            diagnostics.Enqueue($"{stage}:timeout");
        }
        catch (Exception exception)
        {
            diagnostics.Enqueue($"{stage}:{exception.GetType().Name}");
        }
    }

    private async Task StartApplicationWithRetryAsync()
    {
        FieldOpsStartupRetryPolicy retryPolicy = new(StartupDeadline);
        _ = await retryPolicy.RunAsync(async (_, token) =>
        {
            lock (_applicationOutput)
            {
                _applicationOutput.Clear();
            }

            int port = ReserveLoopbackPort();
            BaseUrl = $"http://127.0.0.1:{port}";
            StartApplication(port);
            try
            {
                await WaitForReadyAsync(token);
                return FieldOpsStartupAttempt.Ready;
            }
            catch (ApplicationExitedBeforeReadyException exception)
            {
                _webProcess?.Dispose();
                _webProcess = null;
                return FieldOpsStartupAttempt.Exited(exception.SafeOutput);
            }
        });
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

    private async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        using HttpClient client = new() { BaseAddress = new Uri(BaseUrl) };
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_webProcess?.HasExited == true)
            {
                await _webProcess.WaitForExitAsync(cancellationToken);
                string output;
                lock (_applicationOutput)
                {
                    output = _applicationOutput.ToString();
                }

                throw new ApplicationExitedBeforeReadyException(output);
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync("/health/ready", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private void CaptureApplicationOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is null)
        {
            return;
        }

        lock (_applicationOutput)
        {
            _applicationOutput.AppendLine(args.Data);
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

    private void ClearLegacyFlatFailureArtifacts()
    {
        string resolvedRoot = Path.GetFullPath(ArtifactRoot);
        foreach (string extension in new[] { "*.png", "*.zip" })
        {
            foreach (string path in Directory.GetFiles(resolvedRoot, extension, SearchOption.TopDirectoryOnly))
            {
                string resolvedPath = Path.GetFullPath(path);
                if (!resolvedPath.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("A legacy artifact resolved outside the E2E artifact root.");
                }

                File.Delete(resolvedPath);
            }
        }
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '-'));

    private sealed class ApplicationExitedBeforeReadyException(string safeOutput) : Exception
    {
        public string SafeOutput { get; } = safeOutput;
    }
}

public sealed class FieldOpsStartupRetryPolicy(TimeSpan startupDeadline, int maxAttempts = 3)
{
    public Task<int> RunAsync(Func<int, Task<FieldOpsStartupAttempt>> attemptAsync) =>
        RunAsync((attempt, _) => attemptAsync(attempt));

    public async Task<int> RunAsync(Func<int, CancellationToken, Task<FieldOpsStartupAttempt>> attemptAsync)
    {
        using CancellationTokenSource deadline = new(startupDeadline);
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            FieldOpsStartupAttempt result = await attemptAsync(attempt, deadline.Token);
            if (result.IsReady)
            {
                return attempt;
            }

            if (attempt < maxAttempts && IsAddressInUse(result.SafeOutput))
            {
                continue;
            }

            break;
        }

        string attemptDescription = maxAttempts == 3
            ? "three startup attempts"
            : $"{maxAttempts} startup attempts";
        throw new InvalidOperationException($"FieldOps application did not become ready after {attemptDescription}.");
    }

    private static bool IsAddressInUse(string output) =>
        output.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase);
}

public sealed record FieldOpsStartupAttempt(bool IsReady, string SafeOutput)
{
    public static FieldOpsStartupAttempt Ready { get; } = new(true, string.Empty);

    public static FieldOpsStartupAttempt AddressInUseExit { get; } = Exited("address already in use");

    public static FieldOpsStartupAttempt Exited(string safeOutput) => new(false, safeOutput);
}

public sealed class FailureArtifactHooks
{
    public FailureArtifactHooks(
        Func<IPage, string, CancellationToken, Task>? screenshotAsync = null,
        Func<IBrowserContext, string, CancellationToken, Task>? traceAsync = null)
    {
        ScreenshotAsync = screenshotAsync ?? CaptureScreenshotAsync;
        TraceAsync = traceAsync ?? CaptureTraceAsync;
    }

    public static FailureArtifactHooks Default { get; } = new();

    public Func<IPage, string, CancellationToken, Task> ScreenshotAsync { get; }

    public Func<IBrowserContext, string, CancellationToken, Task> TraceAsync { get; }

    private static Task CaptureScreenshotAsync(IPage page, string path, CancellationToken _) =>
        page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            FullPage = true
        });

    private static Task CaptureTraceAsync(IBrowserContext context, string path, CancellationToken _) =>
        context.Tracing.StopAsync(new TracingStopOptions { Path = path });
}

public sealed record CleanupStage(
    string Name,
    Func<CancellationToken, Task> Action,
    TimeSpan? Deadline = null);

public sealed class BoundedCleanupRunner(TimeSpan defaultDeadline)
{
    public async Task<IReadOnlyList<string>> RunAsync(IEnumerable<CleanupStage> stages)
    {
        List<string> diagnostics = [];
        foreach (CleanupStage stage in stages)
        {
            using CancellationTokenSource deadline = new(stage.Deadline ?? defaultDeadline);
            try
            {
                await stage.Action(deadline.Token).WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                diagnostics.Add($"{stage.Name}:timeout");
            }
            catch (Exception exception)
            {
                diagnostics.Add($"{stage.Name}:{exception.GetType().Name}");
            }
        }

        return diagnostics;
    }
}

public sealed class BrowserErrorCollector
{
    private readonly ConcurrentQueue<string> _errors = [];
    private readonly object _expectationLock = new();
    private ForbiddenExpectation? _forbiddenExpectation;
    private int _genericForbiddenConsoleErrors;

    public BrowserErrorCollector(IPage page)
    {
        page.PageError += (_, error) => _errors.Enqueue($"pageerror: {error}");
        page.Response += (_, response) => CaptureForbiddenResponse(response);
        page.Console += (_, message) =>
        {
            if (!string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(
                message.Text,
                "Failed to load resource: the server responded with a status of 403 (Forbidden)",
                StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _genericForbiddenConsoleErrors);
            }
            else
            {
                _errors.Enqueue($"console: {message.Text}");
            }
        };
    }

    public void ExpectForbiddenNavigation(string pathAndQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathAndQuery);
        if (!pathAndQuery.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("The expected forbidden navigation must be an absolute application path.", nameof(pathAndQuery));
        }

        lock (_expectationLock)
        {
            if (_forbiddenExpectation is not null)
            {
                throw new InvalidOperationException("Only one forbidden navigation expectation may be active.");
            }

            _forbiddenExpectation = new ForbiddenExpectation(pathAndQuery);
        }
    }

    public void AssertEmpty()
    {
        int matched;
        lock (_expectationLock)
        {
            matched = _forbiddenExpectation?.Matched ?? 0;
            if (_forbiddenExpectation is not null && matched != 1)
            {
                _errors.Enqueue($"expected-http-403-not-consumed:{_forbiddenExpectation.PathAndQuery}:matched={matched}");
            }
        }

        int forbiddenConsoleErrors = Volatile.Read(ref _genericForbiddenConsoleErrors);
        if (forbiddenConsoleErrors > matched)
        {
            _errors.Enqueue($"unexpected-generic-403-console-errors:{forbiddenConsoleErrors - matched}");
        }

        Assert.True(
            _errors.IsEmpty,
            $"Unexpected browser errors: {string.Join(" | ", _errors)}");
    }

    private void CaptureForbiddenResponse(IResponse response)
    {
        if (response.Status != 403)
        {
            return;
        }

        Uri uri = new(response.Url);
        string pathAndQuery = uri.PathAndQuery;
        string correlation = response.Headers
            .FirstOrDefault(header => string.Equals(header.Key, "x-correlation-id", StringComparison.OrdinalIgnoreCase))
            .Value ?? string.Empty;
        string resourceType = response.Request.ResourceType;
        lock (_expectationLock)
        {
            if (_forbiddenExpectation is not null &&
                string.Equals(resourceType, "document", StringComparison.Ordinal) &&
                string.Equals(pathAndQuery, _forbiddenExpectation.PathAndQuery, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(correlation))
            {
                _forbiddenExpectation.Matched++;
                return;
            }
        }

        _errors.Enqueue($"unexpected-http-403:{pathAndQuery}:{resourceType}");
    }

    private sealed class ForbiddenExpectation(string pathAndQuery)
    {
        public string PathAndQuery { get; } = pathAndQuery;

        public int Matched { get; set; }
    }
}