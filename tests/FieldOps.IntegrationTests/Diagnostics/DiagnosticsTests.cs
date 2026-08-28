using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;
using FieldOps.Web.Logging;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldOps.IntegrationTests.Diagnostics;

[Collection(DatabaseCollection.Name)]
public sealed class DiagnosticsTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("request.ABC-123_valid")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task ValidCorrelationIdIsReturnedUnchanged(string correlationId)
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-ID", correlationId);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
    }

    [Theory]
    [InlineData("contains space")]
    [InlineData("contains/slash")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task InvalidCorrelationIdIsReplacedWithGeneratedSafeValue(string invalidCorrelationId)
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", invalidCorrelationId);

        using HttpResponseMessage response = await client.SendAsync(request);
        string returned = Assert.Single(response.Headers.GetValues("X-Correlation-ID"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(invalidCorrelationId, returned);
        Assert.Matches("^[A-Za-z0-9._-]{1,64}$", returned);
    }

    [Fact]
    public async Task RequestLogContainsOperationalFieldsAndExcludesSensitiveInputs()
    {
        const string password = "Password-should-never-be-logged";
        const string email = "private.person@example.test";
        const string telephone = "+81-90-1111-2222";
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        CapturingLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(connectionString, configureLogging: logging => logging.AddProvider(logs));
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        string cookieValue = await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"/diagnostics-probe/ok?email={Uri.EscapeDataString(email)}&telephone={Uri.EscapeDataString(telephone)}");
        request.Headers.Add("X-Correlation-ID", "structured-log-test");
        request.Headers.TryAddWithoutValidation("X-Test-Password", password);

        using HttpResponseMessage response = await client.SendAsync(request);
        CapturedLog log = logs.Entries.Single(entry =>
            entry.Category == "FieldOps.Web.Middleware.RequestLoggingMiddleware" &&
            entry.Properties.TryGetValue("Route", out object? route) &&
            Equals(route, "diagnostics-probe/ok"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("structured-log-test", log.Properties["CorrelationId"]);
        Assert.Matches("^[0-9a-f-]{32,36}$", Assert.IsType<string>(log.Properties["UserId"]));
        Assert.Equal(DemoRoleNames.SystemAdministrator, log.Properties["Role"]);
        Assert.Equal(200, log.Properties["StatusCode"]);
        Assert.IsType<long>(log.Properties["ElapsedMs"]);
        Assert.Equal("http.request", log.Properties["Operation"]);
        Assert.Equal("success", log.Properties["Outcome"]);

        string serializedLog = JsonSerializer.Serialize(logs.Entries);
        Assert.DoesNotContain(password, serializedLog, StringComparison.Ordinal);
        Assert.DoesNotContain(cookieValue, serializedLog, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, serializedLog, StringComparison.Ordinal);
        Assert.DoesNotContain(email, serializedLog, StringComparison.Ordinal);
        Assert.DoesNotContain(telephone, serializedLog, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.EscapeDataString(email), serializedLog, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.EscapeDataString(telephone), serializedLog, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/diagnostics-probe/path/private.person%40example.test", "diagnostics-probe/path/{value}")]
    [InlineData("/private.person%40example.test/%2B81-90-1111-2222/742-Evergreen-Avenue", "unmatched")]
    public async Task RequestLogUsesBoundedRouteIdentifierWithoutPathPii(string path, string expectedRoute)
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        CapturingLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(connectionString, configureLogging: logging => logging.AddProvider(logs));
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Add("X-Correlation-ID", "path-redaction-test");

        using HttpResponseMessage response = await client.SendAsync(request);
        List<CapturedLog> requestLogs = logs.Entries.Where(entry =>
            entry.Category == "FieldOps.Web.Middleware.RequestLoggingMiddleware" &&
            entry.Properties.TryGetValue("CorrelationId", out object? correlationId) &&
            Equals(correlationId, "path-redaction-test")).ToList();

        Assert.NotEmpty(requestLogs);
        Assert.All(requestLogs, requestLog => Assert.Equal(expectedRoute, requestLog.Properties["Route"]));
        Assert.DoesNotContain("private.person", JsonSerializer.Serialize(logs.Entries), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1111-2222", JsonSerializer.Serialize(logs.Entries), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Evergreen", JsonSerializer.Serialize(logs.Entries), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DatabasePhaseLogUsesTruthfulTimingsAndRequestScope()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        CapturingLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(connectionString, configureLogging: logging => logging.AddProvider(logs));
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        _ = await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
        using HttpRequestMessage request = new(HttpMethod.Get, "/diagnostics-probe/mutation");
        request.Headers.Add("X-Correlation-ID", "db-scope-test");

        using HttpResponseMessage response = await client.SendAsync(request);
        CapturedLog databaseLog = logs.Entries.Single(entry =>
            entry.Category == "FieldOps.Infrastructure.Persistence.MutationExecutor" &&
            entry.Properties.TryGetValue("Operation", out object? operation) &&
            Equals(operation, "diagnostics.request-mutation"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("DbElapsedMs", databaseLog.Properties.Keys);
        Assert.IsType<long>(databaseLog.Properties["MutationElapsedMs"]);
        Assert.IsType<long>(databaseLog.Properties["LockWaitElapsedMs"]);
        Assert.IsType<long>(databaseLog.Properties["SaveChangesElapsedMs"]);
        Assert.IsType<long>(databaseLog.Properties["CommitElapsedMs"]);
        Assert.Equal("db-scope-test", databaseLog.ScopeProperties["CorrelationId"]);
        Assert.Matches("^[0-9a-f-]{32,36}$", Assert.IsType<string>(databaseLog.ScopeProperties["UserId"]));
        Assert.Equal(DemoRoleNames.SystemAdministrator, databaseLog.ScopeProperties["Role"]);
        Assert.Equal("diagnostics-probe/mutation", databaseLog.ScopeProperties["Route"]);
    }

    [Fact]
    public void JsonFormatterIncludesSafeRequestScopeWithoutAmbientPathOrExceptionSecrets()
    {
        const string privatePath = "/private.person@example.test/742-Evergreen-Avenue";
        LoggerExternalScopeProvider scopes = new();
        using IDisposable ambientScope = scopes.Push(new Dictionary<string, object?>
        {
            ["RequestId"] = "request-42",
            ["RequestPath"] = privatePath
        });
        using IDisposable safeScope = scopes.Push(new Dictionary<string, object?>
        {
            ["CorrelationId"] = "safe-json-test",
            ["UserId"] = "user-42",
            ["Role"] = "System Administrator",
            ["Route"] = "diagnostics-probe/path/{value}"
        });
        IReadOnlyList<KeyValuePair<string, object?>> state =
        [
            new("Operation", "http.request"),
            new("Outcome", "success"),
            new("RequestPath", privatePath)
        ];
        LogEntry<IReadOnlyList<KeyValuePair<string, object?>>> entry = new(
            LogLevel.Information,
            "FieldOps.Web.Middleware.RequestLoggingMiddleware",
            new EventId(42),
            state,
            new InvalidOperationException("formatter-exception-secret"),
            static (_, _) => "Safe structured request event");
        RedactedJsonConsoleFormatter formatter = new();
        using StringWriter output = new();

        formatter.Write(in entry, scopes, output);
        using JsonDocument json = JsonDocument.Parse(output.ToString());

        Assert.Equal("http.request", json.RootElement.GetProperty("Operation").GetString());
        Assert.Equal("safe-json-test", json.RootElement.GetProperty("Scopes").GetProperty("CorrelationId").GetString());
        Assert.Equal("diagnostics-probe/path/{value}", json.RootElement.GetProperty("Scopes").GetProperty("Route").GetString());
        Assert.DoesNotContain(privatePath, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("RequestPath", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("formatter-exception-secret", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorPathsUseExactStatusCodesAndGenericFailureReturnsOnlyCorrelationId()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        CapturingLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(connectionString, configureLogging: logging => logging.AddProvider(logs));
        using HttpClient client = application.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/diagnostics-probe/domain-error")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/diagnostics-probe/concurrency-error")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/diagnostics-probe/forbidden")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/diagnostics-probe/authorization-error")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/diagnostics-probe/not-found")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/diagnostics-probe/missing-error")).StatusCode);

        using HttpRequestMessage request = new(HttpMethod.Get, "/diagnostics-probe/unhandled");
        request.Headers.Add("X-Correlation-ID", "generic-error-test");
        using HttpResponseMessage response = await client.SendAsync(request);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonProperty property = Assert.Single(body.RootElement.EnumerateObject());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("correlationId", property.Name);
        Assert.Equal("generic-error-test", property.Value.GetString());
        Assert.DoesNotContain("server-only-diagnostic-secret", body.RootElement.GetRawText(), StringComparison.Ordinal);
        CapturedLog failureLog = logs.Entries.Single(entry =>
            entry.Category == "FieldOps.Web.Middleware.RequestLoggingMiddleware" &&
            entry.Properties.TryGetValue("Route", out object? route) &&
            Equals(route, "diagnostics-probe/unhandled"));
        Assert.Equal("generic-error-test", failureLog.Properties["CorrelationId"]);
        Assert.Equal(500, failureLog.Properties["StatusCode"]);
        Assert.Equal("failure", failureLog.Properties["Outcome"]);
        Assert.DoesNotContain(
            "server-only-diagnostic-secret",
            JsonSerializer.Serialize(logs.Entries),
            StringComparison.Ordinal);
        CapturedLog exceptionLog = logs.Entries.Single(entry =>
            entry.Category == "FieldOps.Web.Diagnostics.SafeException" &&
            entry.Properties.TryGetValue("ExceptionCategory", out object? category) &&
            Equals(category, "unexpected"));
        Assert.Equal("UnhandledException", exceptionLog.Properties["ExceptionType"]);
        Assert.Equal("generic-error-test", exceptionLog.ScopeProperties["CorrelationId"]);
        Assert.Equal("diagnostics-probe/unhandled", exceptionLog.ScopeProperties["Route"]);
        Assert.DoesNotContain("server-only-diagnostic-secret", exceptionLog.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveRemainsHealthyWhileReadyDetectsPendingMigrations()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        await using (AsyncServiceScope scope = application.Services.CreateAsyncScope())
        {
            FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
            string latestMigration = (await dbContext.Database.GetAppliedMigrationsAsync()).Last();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = {latestMigration}");
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }

    private static async Task<string> LoginAsAsync(HttpClient client, string role)
    {
        string html = await client.GetStringAsync("/demo-login");
        string requestToken = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        string roleToken = Regex.Match(
            html,
            $"data-role=\"{Regex.Escape(role)}\".*?name=\"roleToken\" value=\"([^\"]+)\"",
            RegexOptions.Singleline).Groups[1].Value;

        using HttpResponseMessage response = await client.PostAsync(
            "/demo-login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = roleToken,
                ["__RequestVerificationToken"] = requestToken
            }));
        string setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal) &&
            !value.StartsWith(".AspNetCore.Identity.Application=;", StringComparison.Ordinal));
        return setCookie[(setCookie.IndexOf('=') + 1)..setCookie.IndexOf(';')];
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

        public ConcurrentQueue<CapturedLog> Entries { get; } = new();

        public IExternalScopeProvider ScopeProvider => _scopeProvider;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

        private sealed class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
                {
                    return;
                }

                provider.Entries.Enqueue(new CapturedLog(
                    category,
                    formatter(state, exception),
                    properties.Where(item => item.Key != "{OriginalFormat}")
                        .ToDictionary(item => item.Key, item => item.Value),
                    CaptureScopeProperties(provider.ScopeProvider)));
            }

            private static IReadOnlyDictionary<string, object?> CaptureScopeProperties(IExternalScopeProvider scopeProvider)
            {
                Dictionary<string, object?> captured = [];
                scopeProvider.ForEachScope((scope, state) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> properties)
                    {
                        foreach (KeyValuePair<string, object?> property in properties)
                        {
                            if (property.Key is "CorrelationId" or "UserId" or "Role" or "Route")
                            {
                                state[property.Key] = property.Value;
                            }
                        }
                    }
                }, captured);
                return captured;
            }
        }
    }

    private sealed record CapturedLog(
        string Category,
        string Message,
        IReadOnlyDictionary<string, object?> Properties,
        IReadOnlyDictionary<string, object?> ScopeProperties);
}