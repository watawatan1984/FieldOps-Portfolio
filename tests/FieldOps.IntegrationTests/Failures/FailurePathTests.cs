using System.Data.Common;
using System.Net;
using System.Text.Json;

using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace FieldOps.IntegrationTests.Failures;

[Collection(DatabaseCollection.Name)]
public sealed class FailurePathTests(PostgresFixture postgres)
{
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
            ? new TimeoutException(secret)
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

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}

[ApiController]
[AllowAnonymous]
[Route("failure-probe")]
public sealed class FailureProbeController : ControllerBase
{
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