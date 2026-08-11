using System.Net;
using System.Text.RegularExpressions;

using FieldOps.Features.Administration;
using FieldOps.Infrastructure.Demo;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

namespace FieldOps.IntegrationTests.Administration;

[Collection(DatabaseCollection.Name)]
public sealed partial class DemoResetTests(PostgresFixture fixture) : IAsyncLifetime
{
    private Task12Postgres postgres { get; } = new(fixture);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => postgres.AssertNoDatabaseActivityAsync();

    [Fact]
    public async Task SystemAdministratorSeesAndOpensTheResetConfirmation()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, DemoRoleNames.SystemAdministrator);

        using HttpResponseMessage dashboard = await client.GetAsync("/");
        string dashboardHtml = await dashboard.Content.ReadAsStringAsync();
        using HttpResponseMessage reset = await client.GetAsync("/administration/reset");
        string resetHtml = await reset.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("href=\"/administration/reset\"", dashboardHtml, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Contains("初期化", resetHtml, StringComparison.Ordinal);
        Assert.Contains("name=\"Confirmation\"", resetHtml, StringComparison.Ordinal);
        Assert.Contains("name=\"IdempotencyKey\"", resetHtml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DemoRoleNames.BranchManager)]
    [InlineData(DemoRoleNames.SalesRepresentative)]
    [InlineData(DemoRoleNames.FieldTechnician)]
    public async Task NonAdministratorsCannotSeeOpenOrPostReset(string role)
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, role);

        using HttpResponseMessage dashboard = await client.GetAsync("/");
        string dashboardHtml = await dashboard.Content.ReadAsStringAsync();
        using HttpResponseMessage getReset = await client.GetAsync("/administration/reset");
        using HttpResponseMessage postReset = await client.PostAsync(
            "/administration/reset",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Confirmation"] = "RESET",
                ["IdempotencyKey"] = $"denied-{role}"
            }));

        Assert.DoesNotContain("/administration/reset", dashboardHtml, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, getReset.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, postReset.StatusCode);
    }

    [Fact]
    public async Task FinalPostRequiresAntiforgeryToken()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, DemoRoleNames.SystemAdministrator);

        using HttpResponseMessage response = await client.PostAsync(
            "/administration/reset",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Confirmation"] = "RESET",
                ["IdempotencyKey"] = "missing-antiforgery"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData(" RESET")]
    [InlineData("RESET ")]
    [InlineData("")]
    public async Task ConfirmationMustBeExactlyReset(string confirmation)
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, DemoRoleNames.SystemAdministrator);
        (string token, _) = await GetResetFormAsync(client);

        using HttpResponseMessage response = await PostResetAsync(
            client,
            token,
            confirmation,
            "invalid-confirmation");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .DemoResetExecutions.ToListAsync());
    }

    [Fact]
    public async Task IdempotencyKeyLongerThan64CharactersIsRejectedServerSide()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, DemoRoleNames.SystemAdministrator);
        (string token, _) = await GetResetFormAsync(client);

        using HttpResponseMessage response = await PostResetAsync(
            client,
            token,
            "RESET",
            new string('k', 65));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ValidFinalPostResetsDataAndKeepsTheAdministratorSignedIn()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, DemoRoleNames.SystemAdministrator);
        (string token, string key) = await GetResetFormAsync(client);

        using HttpResponseMessage response = await PostResetAsync(client, token, "RESET", key);
        string responseHtml = await response.Content.ReadAsStringAsync();
        using HttpResponseMessage dashboard = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("初期化が完了しました", responseHtml, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.DoesNotContain("/demo-login", dashboard.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetPageAndScriptProvideBusyOverlayDoubleSubmitGuardAndRecoverableErrorUi()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, DemoRoleNames.SystemAdministrator);

        string html = await client.GetStringAsync("/administration/reset");
        string script = await client.GetStringAsync("/js/demo-reset.js");

        Assert.Contains("data-demo-reset-form aria-busy=\"false\"", html, StringComparison.Ordinal);
        Assert.Contains("data-demo-reset-submit", html, StringComparison.Ordinal);
        Assert.Contains("data-demo-reset-overlay", html, StringComparison.Ordinal);
        Assert.Contains("初期化しています…", html, StringComparison.Ordinal);
        Assert.Contains("data-demo-reset-error", html, StringComparison.Ordinal);
        Assert.Contains("相関 ID", html, StringComparison.Ordinal);
        Assert.Contains("if (submitting)", script, StringComparison.Ordinal);
        Assert.Contains("submitButton.disabled = true", script, StringComparison.Ordinal);
        Assert.Contains("form.setAttribute(\"aria-busy\", \"true\")", script, StringComparison.Ordinal);
        Assert.Contains("await fetch(form.action", script, StringComparison.Ordinal);
        Assert.Contains("submitButton.disabled = false", script, StringComparison.Ordinal);
        Assert.Contains("X-Correlation-ID", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetRestoresTheDeterministicManifestAndStableIdentifiers()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        _ = application.CreateClient();

        await using (AsyncServiceScope resetScope = application.Services.CreateAsyncScope())
        {
            IDemoResetService service = resetScope.ServiceProvider.GetRequiredService<IDemoResetService>();
            DemoResetResult result = await service.ResetAsync(new DemoResetCommand(
                "manifest-reset-1",
                DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                "test-manifest-reset-1"));

            Assert.False(result.WasAlreadyCompleted);
        }

        await using AsyncServiceScope assertScope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = assertScope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(DemoDataManifest.BranchCount, await dbContext.Branches.CountAsync());
        Assert.Equal(DemoDataManifest.PartyCount, await dbContext.Parties.CountAsync());
        Assert.Equal(DemoDataManifest.SalesOpportunityCount, await dbContext.SalesOpportunities.CountAsync());
        Assert.Equal(DemoDataManifest.WorkOrderCount, await dbContext.WorkOrders.CountAsync());
        Assert.Equal(DemoDataManifest.WorkEventCount, await dbContext.Set<FieldOps.Domain.Entities.WorkEvent>().CountAsync());
        Assert.Equal(DemoDataManifest.DemoUserCount, await dbContext.Users.CountAsync());
        Assert.Equal(DemoDataManifest.SeedAuditEntryCount + 2, await dbContext.AuditEntries.CountAsync());
        Assert.True(await dbContext.Branches.AnyAsync(branch => branch.Id == DemoDataManifest.Branches[0].Id));
        Assert.True(await dbContext.Parties.AnyAsync(party => party.Id == DemoDataManifest.PartyId(1)));
        Assert.True(await dbContext.SalesOpportunities.AnyAsync(item => item.Id == DemoDataManifest.SalesOpportunityId(1)));
        Assert.True(await dbContext.WorkOrders.AnyAsync(item => item.Id == DemoDataManifest.WorkOrderId(1)));
        Assert.True(await dbContext.Set<FieldOps.Domain.Entities.WorkEvent>().AnyAsync(item => item.Id == DemoDataManifest.WorkEventId(1)));
        Assert.True(await dbContext.Users.AnyAsync(user =>
            user.Id == DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id));
    }

    [Fact]
    public async Task DuplicateIdempotencyKeyExecutesOnlyOnceAndReturnsStoredResult()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        _ = application.CreateClient();
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        IDemoResetService service = scope.ServiceProvider.GetRequiredService<IDemoResetService>();
        DemoResetCommand command = new(
            "same-reset-key",
            DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
            "same-key-correlation");

        DemoResetResult first = await service.ResetAsync(command);
        DemoResetResult second = await service.ResetAsync(command with { CorrelationId = "ignored-second-correlation" });

        Assert.False(first.WasAlreadyCompleted);
        Assert.True(second.WasAlreadyCompleted);
        Assert.Equal(first.CorrelationId, second.CorrelationId);
        Assert.Equal(first.DurationMilliseconds, second.DurationMilliseconds);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .DemoResetExecutions.CountAsync(item => item.IdempotencyKey == command.IdempotencyKey));
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .AuditEntries.CountAsync(item => item.Action == "ResetCompleted"));
    }

    [Fact]
    public async Task DatabaseUniquelyConstrainsTheIdempotencyKey()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        _ = application.CreateClient();
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        DateTime now = DateTime.UtcNow;
        dbContext.DemoResetExecutions.AddRange(
            DemoResetExecution.Start(Guid.NewGuid(), "db-unique-key", "actor-1", "correlation-1", now),
            DemoResetExecution.Start(Guid.NewGuid(), "db-unique-key", "actor-2", "correlation-2", now));

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());

        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task InjectedFailureRollsBackEveryDemoRowThenPersistsSanitizedFailureEvidence()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (FieldOpsWebApplicationFactory seedApplication = new(connectionString))
        {
            _ = seedApplication.CreateClient();
            await using AsyncServiceScope seedScope = seedApplication.Services.CreateAsyncScope();
            await seedScope.ServiceProvider.GetRequiredService<IDemoResetService>().ResetAsync(new DemoResetCommand(
                "failure-baseline",
                DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                "failure-baseline-correlation"));
        }

        IReadOnlyDictionary<string, string> before = await ReadDemoFingerprintsAsync(connectionString);
        ThrowingPhaseObserver observer = new(DemoResetPhase.DataSeeded);
        await using FieldOpsWebApplicationFactory failingApplication = new(
            connectionString,
            services =>
            {
                services.RemoveAll<IDemoResetPhaseObserver>();
                services.AddSingleton<IDemoResetPhaseObserver>(observer);
            });
        _ = failingApplication.CreateClient();
        await using AsyncServiceScope failingScope = failingApplication.Services.CreateAsyncScope();

        DemoResetFailedException exception = await Assert.ThrowsAsync<DemoResetFailedException>(() =>
            failingScope.ServiceProvider.GetRequiredService<IDemoResetService>().ResetAsync(new DemoResetCommand(
                "forced-failure-key",
                DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                "forced-failure-correlation")));

        Assert.Equal("forced-failure-correlation", exception.CorrelationId);
        Assert.Equal(before, await ReadDemoFingerprintsAsync(connectionString));
        FieldOpsDbContext dbContext = failingScope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        DemoResetExecution failed = await dbContext.DemoResetExecutions
            .SingleAsync(item => item.IdempotencyKey == "forced-failure-key");
        Assert.Equal(DemoResetState.Failed, failed.State);
        Assert.Equal("Failed", failed.Outcome);
        FieldOps.Domain.Entities.AuditEntry failureAudit = await dbContext.AuditEntries
            .SingleAsync(item => item.AggregateId == failed.Id && item.Action == "ResetFailed");
        Assert.Equal(string.Empty, failureAudit.ChangeSummary);
        Assert.Contains("correlationId=forced-failure-correlation", failureAudit.Outcome, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(ThrowingPhaseObserver), failureAudit.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedKeyCanBeRetriedAndTransitionsTheSingleExecutionToCompleted()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        OneShotThrowingPhaseObserver observer = new(DemoResetPhase.RowsDeleted);
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.RemoveAll<IDemoResetPhaseObserver>();
                services.AddSingleton<IDemoResetPhaseObserver>(observer);
            });
        _ = application.CreateClient();
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        IDemoResetService service = scope.ServiceProvider.GetRequiredService<IDemoResetService>();
        DemoResetCommand failedCommand = new(
            "retry-failed-key",
            DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
            "retry-failed-first");
        await Assert.ThrowsAsync<DemoResetFailedException>(() => service.ResetAsync(failedCommand));

        DemoResetResult retried = await service.ResetAsync(
            failedCommand with { CorrelationId = "retry-failed-second" });

        Assert.False(retried.WasAlreadyCompleted);
        DemoResetExecution execution = await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .DemoResetExecutions.SingleAsync(item => item.IdempotencyKey == failedCommand.IdempotencyKey);
        Assert.Equal(DemoResetState.Completed, execution.State);
        Assert.Equal("retry-failed-second", execution.CorrelationId);
    }

    [Fact]
    public async Task TwoDifferentResetsKeepExactCountsStableIdsSchemaRolesAndAuditTraceability()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        _ = application.CreateClient();
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        IDemoResetService service = scope.ServiceProvider.GetRequiredService<IDemoResetService>();
        string[] rolesBefore = await dbContext.Roles
            .OrderBy(role => role.Name)
            .Select(role => role.Id + ":" + role.Name)
            .ToArrayAsync();

        await service.ResetAsync(new DemoResetCommand(
            "two-reset-first",
            DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
            "two-reset-first-correlation"));
        dbContext.ChangeTracker.Clear();
        Guid[] partyIdsAfterFirst = await dbContext.Parties.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync();
        Guid[] orderIdsAfterFirst = await dbContext.WorkOrders.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync();
        string[] userIdsAfterFirst = await dbContext.Users.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync();

        await service.ResetAsync(new DemoResetCommand(
            "two-reset-second",
            DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
            "two-reset-second-correlation"));
        dbContext.ChangeTracker.Clear();

        Assert.Equal(partyIdsAfterFirst, await dbContext.Parties.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync());
        Assert.Equal(orderIdsAfterFirst, await dbContext.WorkOrders.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync());
        Assert.Equal(userIdsAfterFirst, await dbContext.Users.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync());
        Assert.Equal(DemoDataManifest.BranchCount, await dbContext.Branches.CountAsync());
        Assert.Equal(DemoDataManifest.PartyCount, await dbContext.Parties.CountAsync());
        Assert.Equal(DemoDataManifest.SalesOpportunityCount, await dbContext.SalesOpportunities.CountAsync());
        Assert.Equal(DemoDataManifest.WorkOrderCount, await dbContext.WorkOrders.CountAsync());
        Assert.Equal(DemoDataManifest.WorkEventCount, await dbContext.Set<FieldOps.Domain.Entities.WorkEvent>().CountAsync());
        Assert.Equal(DemoDataManifest.DemoUserCount, await dbContext.Users.CountAsync());
        Assert.Equal(DemoDataManifest.SeedAuditEntryCount + 2, await dbContext.AuditEntries.CountAsync());
        Assert.Equal(2, await dbContext.DemoResetExecutions.CountAsync());
        Assert.Equal(20, await dbContext.Parties.CountAsync(party => party.Roles.Count == 2));
        Assert.Equal(8, await dbContext.SalesOpportunities.Select(item => item.Status).Distinct().CountAsync());
        Assert.Equal(5, await dbContext.WorkOrders.Select(item => item.Status).Distinct().CountAsync());
        Assert.Equal(DemoDataManifest.EpochUtc, await dbContext.Branches
            .Where(branch => branch.Id == DemoDataManifest.Branches[0].Id)
            .Select(branch => branch.CreatedAtUtc)
            .SingleAsync());
        Assert.Equal(rolesBefore, await dbContext.Roles
            .OrderBy(role => role.Name)
            .Select(role => role.Id + ":" + role.Name)
            .ToArrayAsync());
        Assert.Equal(DemoRoleNames.All.Order(), await dbContext.Roles.Select(role => role.Name!).Order().ToArrayAsync());

        FieldOps.Domain.Entities.AuditEntry started = await dbContext.AuditEntries.SingleAsync(item => item.Action == "ResetStarted");
        FieldOps.Domain.Entities.AuditEntry completed = await dbContext.AuditEntries.SingleAsync(item => item.Action == "ResetCompleted");
        Assert.Equal(DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id, started.ActorUserId);
        Assert.Equal(started.ActorUserId, completed.ActorUserId);
        Assert.Contains("correlationId=two-reset-second-correlation", started.Outcome, StringComparison.Ordinal);
        Assert.Contains("durationMs=", completed.Outcome, StringComparison.Ordinal);
        Assert.Contains("correlationId=two-reset-second-correlation", completed.Outcome, StringComparison.Ordinal);
        Assert.Empty(started.ChangeSummary);
        Assert.Empty(completed.ChangeSummary);

        await dbContext.Database.MigrateAsync();
        Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
        await using NpgsqlCommand schemaCheck = new(
            "SELECT to_regclass('\"DemoResetExecutions\"') IS NOT NULL",
            (NpgsqlConnection)dbContext.Database.GetDbConnection());
        await dbContext.Database.OpenConnectionAsync();
        Assert.True((bool)(await schemaCheck.ExecuteScalarAsync() ?? false));
    }

    private static async Task LoginAsync(HttpClient client, string role)
    {
        string html = await client.GetStringAsync("/demo-login");
        string requestToken = RequestVerificationTokenRegex().Match(html).Groups[1].Value;
        string roleToken = Regex.Match(
            html,
            $"<h2 class=\"h5\">{Regex.Escape(role)}</h2>.*?name=\"roleToken\" value=\"([^\"]+)\"",
            RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(requestToken);
        Assert.NotEmpty(roleToken);

        using HttpResponseMessage response = await client.PostAsync(
            "/demo-login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = roleToken,
                ["__RequestVerificationToken"] = requestToken
            }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<(string Token, string IdempotencyKey)> GetResetFormAsync(HttpClient client)
    {
        string html = await client.GetStringAsync("/administration/reset");
        string token = RequestVerificationTokenRegex().Match(html).Groups[1].Value;
        string key = Regex.Match(html, "name=\"IdempotencyKey\"[^>]*value=\"([^\"]+)\"")
            .Groups[1].Value;
        Assert.NotEmpty(token);
        Assert.NotEmpty(key);
        return (token, key);
    }

    private static Task<HttpResponseMessage> PostResetAsync(
        HttpClient client,
        string token,
        string confirmation,
        string idempotencyKey) =>
        client.PostAsync(
            "/administration/reset",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Confirmation"] = confirmation,
                ["IdempotencyKey"] = idempotencyKey,
                ["__RequestVerificationToken"] = token
            }));

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex RequestVerificationTokenRegex();

    private static async Task<IReadOnlyDictionary<string, string>> ReadDemoFingerprintsAsync(string connectionString)
    {
        string[] tables =
        [
            "Branches",
            "Parties",
            "PartyRoles",
            "PartyBranchAssignments",
            "Contacts",
            "Sites",
            "SalesOpportunities",
            "WorkOrders",
            "WorkEvents",
            "AspNetUsers",
            "AspNetUserRoles",
            "AspNetRoles"
        ];
        Dictionary<string, string> fingerprints = new(StringComparer.Ordinal);
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        foreach (string table in tables)
        {
            await using NpgsqlCommand command = new(
                $"""
                SELECT md5(COALESCE(string_agg(row_json, E'\n' ORDER BY row_json), ''))
                FROM (SELECT to_jsonb(source)::text AS row_json FROM "{table}" AS source) AS rows
                """,
                connection);
            fingerprints[table] = (string)(await command.ExecuteScalarAsync()
                ?? throw new InvalidOperationException($"No fingerprint was returned for {table}."));
        }

        return fingerprints;
    }

    private sealed class ThrowingPhaseObserver(DemoResetPhase failurePhase) : IDemoResetPhaseObserver
    {
        public Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken) =>
            phase == failurePhase
                ? Task.FromException(new InvalidOperationException("Injected reset failure."))
                : Task.CompletedTask;
    }

    private sealed class OneShotThrowingPhaseObserver(DemoResetPhase failurePhase) : IDemoResetPhaseObserver
    {
        private int _thrown;

        public Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken) =>
            phase == failurePhase && Interlocked.CompareExchange(ref _thrown, 1, 0) == 0
                ? Task.FromException(new InvalidOperationException("Injected one-shot reset failure."))
                : Task.CompletedTask;
    }
}