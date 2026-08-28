using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace FieldOps.IntegrationTests.Failures;

[Collection(DatabaseCollection.Name)]
public sealed class FailurePathTests(PostgresFixture postgres)
{
    private const string ExpectedContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; style-src-attr 'unsafe-inline'; " +
        "img-src 'self' data:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    [Fact]
    public async Task WebFactoryPreservesPoolingAndDisposalDrainsOnlyItsDatabasePool()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        string databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        FieldOpsWebApplicationFactory application = new(connectionString);
        using (HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        }))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
            string effectiveConnectionString = scope.ServiceProvider
                .GetRequiredService<FieldOpsDbContext>()
                .Database.GetConnectionString()!;
            Assert.True(new NpgsqlConnectionStringBuilder(effectiveConnectionString).Pooling);
        }

        await application.DisposeAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (await postgres.CountDatabaseConnectionsAsync(databaseName, timeout.Token) != 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    [Fact]
    public async Task ArbitraryApplicationTimeoutReturnsSafe500RatherThanDatabaseUnavailable503()
    {
        const string secret = "application-timeout-private-detail";
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        FailureOutcomeLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configureLogging: logging => logging.AddProvider(logs));
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        using HttpRequestMessage request = new(HttpMethod.Get, $"/failure-probe/timeout?detail={secret}");
        request.Headers.Add("X-Correlation-ID", "application-timeout-test");
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("{\"correlationId\":\"application-timeout-test\"}", body);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        AssertSecurityHeaders(response);
        AssertRequestOutcome(logs, "application-timeout-test", 500, secret);
    }

    [Fact]
    public async Task HtmlFailuresReturnJapaneseRecoveryPageWithoutLeakingExceptionDetails()
    {
        const string secret = "html-private-stack-secret";
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateHttpsClient(application);

        using HttpRequestMessage request = new(HttpMethod.Get, $"/failure-probe/timeout?detail={secret}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Add("X-Correlation-ID", "html-correlation-test");
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("<html lang=\"ja\">", body, StringComparison.Ordinal);
        Assert.Contains("処理を完了できませんでした", body, StringComparison.Ordinal);
        Assert.Contains("html-correlation-test", body, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeoutException", body, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task StatusCodePagesRenderSafeJapaneseRecoveryOnlyForForbiddenAndNotFound()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateHttpsClient(application);
        await LoginAsAdministratorAsync(client);
        using HttpResponseMessage missing = await client.GetAsync("/definitely-missing-page");
        string missingHtml = await missing.Content.ReadAsStringAsync();
        string decodedMissingHtml = WebUtility.HtmlDecode(missingHtml);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);
        using HttpResponseMessage forbidden = await client.GetAsync("/administration/reset");
        string forbiddenHtml = await forbidden.Content.ReadAsStringAsync();
        string decodedForbiddenHtml = WebUtility.HtmlDecode(forbiddenHtml);
        using HttpResponseMessage unexpected = await client.GetAsync("/status/500");
        string unexpectedBody = await unexpected.Content.ReadAsStringAsync();
        using HttpResponseMessage accidentalOk = await client.GetAsync("/status/200");
        using HttpResponseMessage accidentalRedirect = await client.GetAsync("/status/302");
        using HttpResponseMessage bareServiceUnavailable = await client.GetAsync("/failure-probe/status/503");
        string bareServiceUnavailableBody = await bareServiceUnavailable.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("指定された情報が見つかりません", decodedMissingHtml, StringComparison.Ordinal);
        Assert.Contains("前の画面へ戻る", decodedMissingHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"/\"", missingHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"javascript:", missingHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains("この操作を行う権限がありません", decodedForbiddenHtml, StringComparison.Ordinal);
        Assert.Contains("前の画面へ戻る", decodedForbiddenHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"/\"", forbiddenHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"javascript:", forbiddenHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.InternalServerError, unexpected.StatusCode);
        Assert.Empty(unexpectedBody);
        Assert.Equal(HttpStatusCode.BadRequest, accidentalOk.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, accidentalRedirect.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, bareServiceUnavailable.StatusCode);
        Assert.Empty(bareServiceUnavailableBody);
    }

    [Fact]
    public async Task AuthorizationHandlerExceptionIsCaughtBySafeOuterHandlerAndLoggedOnce()
    {
        const string secret = "authorization-handler-private-detail";
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        ArmedAuthorizationHandler failure = new();
        FailureOutcomeLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configureServices: services => services.AddSingleton<IAuthorizationHandler>(failure),
            configureLogging: logging => logging.AddProvider(logs));
        using HttpClient client = CreateHttpsClient(application);
        await LoginAsAdministratorAsync(client);

        failure.Arm(new InvalidOperationException(secret));
        using HttpRequestMessage request = new(HttpMethod.Get, "/administration/reset");
        request.Headers.Add("X-Correlation-ID", "authorization-handler-exception-test");
        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertSafe500Async(response, "authorization-handler-exception-test", secret);
        AssertRequestOutcome(logs, "authorization-handler-exception-test", 500, secret);
    }

    [Fact]
    public async Task RateLimiterRejectionHandlerExceptionIsCaughtBySafeOuterHandlerAndLoggedOnce()
    {
        const string secret = "rate-limiter-private-detail";
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        FailureOutcomeLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configureServices: services => services.PostConfigure<RateLimiterOptions>(options =>
                options.OnRejected = (_, _) => ValueTask.FromException(new InvalidOperationException(secret))),
            configureLogging: logging => logging.AddProvider(logs));
        using HttpClient client = CreateHttpsClient(application);
        string html = await client.GetStringAsync("/demo-login");
        string antiforgeryToken = GetInputValue(html, "__RequestVerificationToken");
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using HttpResponseMessage counted = await client.PostAsync(
                "/demo-login",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["roleToken"] = $"invalid-{attempt}",
                    ["__RequestVerificationToken"] = antiforgeryToken
                }));
            Assert.Equal(HttpStatusCode.BadRequest, counted.StatusCode);
        }

        using HttpRequestMessage request = new(HttpMethod.Post, "/demo-login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = "still-invalid",
                ["__RequestVerificationToken"] = antiforgeryToken
            })
        };
        request.Headers.Add("X-Correlation-ID", "rate-limiter-exception-test");
        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertSafe500Async(response, "rate-limiter-exception-test", secret);
        AssertRequestOutcome(logs, "rate-limiter-exception-test", 500, secret);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DatabaseTimeoutAndUnavailableReturnSafe503WithoutChangingHealthSemantics(bool timeout)
    {
        const string secret = "Host=raw-private-host;Password=raw-private-password";
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        ArmedCommandFailureInterceptor failure = new();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configureServices: services => services.AddDbContext<FieldOpsDbContext>(options => options.AddInterceptors(failure)));
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        failure.Arm(timeout
            ? new NpgsqlException("Database command timed out.", new TimeoutException(secret))
            : new NpgsqlException(secret));
        using HttpRequestMessage request = new(HttpMethod.Get, "/failure-probe/database");
        request.Headers.Add("X-Correlation-ID", timeout ? "database-timeout-test" : "database-unavailable-test");
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        JsonProperty property = Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal("correlationId", property.Name);
        Assert.Equal(timeout ? "database-timeout-test" : "database-unavailable-test", property.Value.GetString());
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        AssertSecurityHeaders(response);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal(ExpectedContentSecurityPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal(
            "camera=(), microphone=(), geolocation=()",
            Assert.Single(response.Headers.GetValues("Permissions-Policy")));
    }

    private static HttpClient CreateHttpsClient(FieldOpsWebApplicationFactory application) =>
        application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task AssertSafe500Async(
        HttpResponseMessage response,
        string correlationId,
        string secret)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal($"{{\"correlationId\":\"{correlationId}\"}}", body);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        AssertSecurityHeaders(response);
    }

    private static void AssertRequestOutcome(
        FailureOutcomeLoggerProvider logs,
        string correlationId,
        int statusCode,
        string secret)
    {
        FailureRequestOutcomeLog outcome = Assert.Single(logs.Entries, entry => entry.CorrelationId == correlationId);
        Assert.Equal(statusCode, outcome.StatusCode);
        Assert.Equal("failure", outcome.Outcome);
        Assert.DoesNotContain(secret, outcome.Message, StringComparison.Ordinal);
    }

    private static async Task LoginAsAdministratorAsync(HttpClient client)
    {
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
    }

    private static async Task LoginAsAsync(HttpClient client, string role)
    {
        string html = await client.GetStringAsync("/demo-login");
        string roleToken = Regex.Match(
            html,
            $"data-role=\"{Regex.Escape(role)}\".*?name=\"roleToken\" value=\"([^\"]+)\"",
            RegexOptions.Singleline).Groups[1].Value;
        using HttpResponseMessage response = await client.PostAsync(
            "/demo-login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = roleToken,
                ["__RequestVerificationToken"] = GetInputValue(html, "__RequestVerificationToken")
            }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static string GetInputValue(string html, string name)
    {
        string value = Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(value);
        return value;
    }
}

[ApiController]
[AllowAnonymous]
[Route("failure-probe")]
public sealed class FailureProbeController : ControllerBase
{
    [HttpGet("timeout")]
    public IActionResult Timeout([FromQuery] string detail) => throw new TimeoutException(detail);

    [HttpGet("status/{code:int}")]
    public IActionResult BareStatus(int code)
    {
        Response.StatusCode = code;
        return new EmptyResult();
    }

    [HttpGet("database")]
    public async Task<IActionResult> Database(
        [FromServices] FieldOpsDbContext dbContext,
        CancellationToken cancellationToken)
    {
        _ = await dbContext.Branches.AsNoTracking().AnyAsync(cancellationToken);
        return Ok();
    }
}

public sealed class ArmedCommandFailureInterceptor : DbCommandInterceptor
{
    private Exception? _exception;

    public void Arm(Exception exception) => Interlocked.Exchange(ref _exception, exception);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Exception? exception = Interlocked.Exchange(ref _exception, null);
        return exception is null
            ? base.ReaderExecutingAsync(command, eventData, result, cancellationToken)
            : ValueTask.FromException<InterceptionResult<DbDataReader>>(exception);
    }
}

public sealed class ArmedAuthorizationHandler : IAuthorizationHandler
{
    private Exception? _exception;

    public void Arm(Exception exception) => Interlocked.Exchange(ref _exception, exception);

    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        Exception? exception = Interlocked.Exchange(ref _exception, null);
        return exception is null ? Task.CompletedTask : Task.FromException(exception);
    }
}

public sealed class FailureOutcomeLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<FailureRequestOutcomeLog> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(FailureOutcomeLoggerProvider provider, string category) : ILogger
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
            if (category != "FieldOps.Web.Middleware.RequestLoggingMiddleware" ||
                state is not IEnumerable<KeyValuePair<string, object?>> properties)
            {
                return;
            }

            Dictionary<string, object?> values = properties.ToDictionary(item => item.Key, item => item.Value);
            if (values.TryGetValue("CorrelationId", out object? correlationId) &&
                values.TryGetValue("StatusCode", out object? statusCode) &&
                values.TryGetValue("Outcome", out object? outcome))
            {
                provider.Entries.Enqueue(new(
                    correlationId?.ToString() ?? string.Empty,
                    Convert.ToInt32(statusCode, System.Globalization.CultureInfo.InvariantCulture),
                    outcome?.ToString() ?? string.Empty,
                    formatter(state, exception)));
            }
        }
    }
}

public sealed record FailureRequestOutcomeLog(
    string CorrelationId,
    int StatusCode,
    string Outcome,
    string Message);
