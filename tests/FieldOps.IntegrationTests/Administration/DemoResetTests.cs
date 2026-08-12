using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using FieldOps.Features.Administration;
using FieldOps.Infrastructure.Demo;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;
using FieldOps.Web.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace FieldOps.IntegrationTests.Administration;

[Collection(DatabaseCollection.Name)]
public sealed partial class DemoResetTests(PostgresFixture fixture) : IAsyncLifetime
{
    private Task12Postgres postgres { get; } = new(fixture);

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        return postgres.AssertNoDatabaseActivityAsync();
    }

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

    [Fact]
    public async Task BorrowedAntiforgeryTokenWithoutAResetIntentCannotExecuteAnArbitraryKey()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, DemoRoleNames.SystemAdministrator);
        string dashboardHtml = await client.GetStringAsync("/");
        string borrowedToken = RequestVerificationTokenRegex().Match(dashboardHtml).Groups[1].Value;
        Assert.NotEmpty(borrowedToken);

        using HttpResponseMessage response = await client.PostAsync(
            "/administration/reset",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Confirmation"] = "RESET",
                ["IdempotencyKey"] = "arbitrary-with-borrowed-antiforgery",
                ["__RequestVerificationToken"] = borrowedToken
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .DemoResetExecutions.ToListAsync());
    }

    [Fact]
    public async Task TamperedOrKeyMismatchedResetIntentIsRejectedWithoutExecution()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, DemoRoleNames.SystemAdministrator);
        (string token, string key, string intentToken) = await GetResetFormAsync(client);
        char replacement = intentToken[^1] == 'A' ? 'B' : 'A';
        string tampered = intentToken[..^1] + replacement;

        using HttpResponseMessage tamperedResponse =
            await PostResetAsync(client, token, "RESET", key, tampered);
        using HttpResponseMessage mismatchedResponse =
            await PostResetAsync(client, token, "RESET", key + "-changed", intentToken);

        Assert.Equal(HttpStatusCode.BadRequest, tamperedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, mismatchedResponse.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .DemoResetExecutions.ToListAsync());
    }

    [Fact]
    public async Task ResetIntentIsUserBoundAndExpiresThroughTheInjectedTimeProvider()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        MutableTimeProvider timeProvider = new(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(timeProvider);
            });
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, DemoRoleNames.SystemAdministrator);
        (string token, string key, string intentToken) = await GetResetFormAsync(client);
        DemoResetIntentProtector protector = application.Services.GetRequiredService<DemoResetIntentProtector>();

        Assert.False(protector.IsValid(intentToken, "different-administrator", key));
        timeProvider.Advance(DemoResetIntentProtector.Lifetime.Add(TimeSpan.FromSeconds(1)));
        using HttpResponseMessage response =
            await PostResetAsync(client, token, "RESET", key, intentToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .DemoResetExecutions.ToListAsync());
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
        (string token, string key, string intentToken) = await GetResetFormAsync(client);

        using HttpResponseMessage response = await PostResetAsync(
            client,
            token,
            confirmation,
            key,
            intentToken);

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
        (string token, _, string intentToken) = await GetResetFormAsync(client);

        using HttpResponseMessage response = await PostResetAsync(
            client,
            token,
            "RESET",
            new string('k', 65),
            intentToken);

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
        (string token, string key, string intentToken) = await GetResetFormAsync(client);

        using HttpResponseMessage response = await PostResetAsync(client, token, "RESET", key, intentToken);
        string responseHtml = await response.Content.ReadAsStringAsync();
        using HttpResponseMessage dashboard = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("初期化が完了しました", responseHtml, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.DoesNotContain("/demo-login", dashboard.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchSuccessCarriesASafeCompletionFlashToTheDashboardRedirect()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, DemoRoleNames.SystemAdministrator);
        (string token, string key, string intentToken) = await GetResetFormAsync(client);
        using HttpRequestMessage request = CreateResetRequest(token, "RESET", key, intentToken);
        request.Headers.Add("X-Requested-With", "fetch");

        using HttpResponseMessage response = await client.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        using JsonDocument responseJson = JsonDocument.Parse(responseBody);
        string redirectUrl = responseJson.RootElement.GetProperty("redirectUrl").GetString()
            ?? throw new InvalidOperationException("A reset redirect URL was not returned.");
        Assert.Contains("resetCompletion=", redirectUrl, StringComparison.Ordinal);
        string completionToken = Uri.UnescapeDataString(
            redirectUrl[(redirectUrl.IndexOf("resetCompletion=", StringComparison.Ordinal) + "resetCompletion=".Length)..]);
        using (IServiceScope scope = application.Services.CreateScope())
        {
            DemoResetCompletionProtector protector = scope.ServiceProvider
                .GetRequiredService<DemoResetCompletionProtector>();
            Assert.True(protector.TryGetCorrelationId(
                completionToken,
                DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                out _));
        }
        using HttpResponseMessage dashboard = await client.GetAsync(redirectUrl);
        string dashboardHtml = await dashboard.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("初期化が完了しました", dashboardHtml, StringComparison.Ordinal);
        Assert.Contains("data-demo-reset-completed", dashboardHtml, StringComparison.Ordinal);
        Assert.Contains("window.history.replaceState", dashboardHtml, StringComparison.Ordinal);
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
        Assert.Contains("data-demo-reset-guidance", html, StringComparison.Ordinal);
        Assert.Contains("相関 ID", html, StringComparison.Ordinal);
        Assert.Contains("if (submitting)", script, StringComparison.Ordinal);
        Assert.Contains("submitButton.disabled = true", script, StringComparison.Ordinal);
        Assert.Contains("form.setAttribute(\"aria-busy\", \"true\")", script, StringComparison.Ordinal);
        Assert.Contains("await fetch(form.action", script, StringComparison.Ordinal);
        Assert.Contains("submitButton.disabled = false", script, StringComparison.Ordinal);
        Assert.Contains("X-Correlation-ID", script, StringComparison.Ordinal);
        Assert.Contains("await response.json()", script, StringComparison.Ordinal);
        Assert.Contains("problem?.errors", script, StringComparison.Ordinal);
        Assert.Contains("guidance.textContent", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchValidationFailureReturnsSafeFieldGuidanceAndDoesNotExecuteReset()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, DemoRoleNames.SystemAdministrator);
        (string token, _, _) = await GetResetFormAsync(client);
        const string untrustedIntent = "attacker-supplied-intent";
        using HttpRequestMessage request = CreateResetRequest(
            token,
            "reset",
            new string('k', 65),
            untrustedIntent);
        request.Headers.Add("X-Requested-With", "fetch");

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        Assert.Contains("RESET", body, StringComparison.Ordinal);
        Assert.Contains("IdempotencyKey", body, StringComparison.Ordinal);
        Assert.Contains("IntentToken", body, StringComparison.Ordinal);
        Assert.Contains("correlationId", body, StringComparison.Ordinal);
        Assert.Contains("確認画面を開き直してください", body, StringComparison.Ordinal);
        Assert.DoesNotContain(untrustedIntent, body, StringComparison.Ordinal);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .DemoResetExecutions.ToListAsync());
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
    public async Task ResetPreservesExistingPasswordHashesAndRestoresFixedIdentityManifest()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        _ = application.CreateClient();

        IReadOnlyDictionary<string, DemoIdentityState> before = await ReadDemoIdentityStateAsync(connectionString);
        Assert.Equal(DemoDataManifest.DemoUserCount, before.Count);
        Assert.All(before.Values, state =>
        {
            Assert.False(string.IsNullOrWhiteSpace(state.PasswordHash));
            Assert.False(string.IsNullOrWhiteSpace(state.SecurityStamp));
            Assert.False(string.IsNullOrWhiteSpace(state.RoleIds));
        });

        await using (AsyncServiceScope resetScope = application.Services.CreateAsyncScope())
        {
            await resetScope.ServiceProvider.GetRequiredService<IDemoResetService>().ResetAsync(new DemoResetCommand(
                "identity-preservation",
                DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                "identity-preservation-correlation"));
        }

        IReadOnlyDictionary<string, DemoIdentityState> after = await ReadDemoIdentityStateAsync(connectionString);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach ((string role, DemoUser user) in DemoDataManifest.UsersByRole)
        {
            Assert.Equal(before[user.Id].PasswordHash, after[user.Id].PasswordHash);
            Assert.Equal(user.SecurityStamp, after[user.Id].SecurityStamp);
            Assert.Equal(user.ConcurrencyStamp, after[user.Id].ConcurrencyStamp);
            Assert.Equal(DemoDataManifest.RoleIds[role], after[user.Id].RoleIds);
        }
    }

    [Fact]
    public async Task MissingDatabaseDatasetMarkerRejectsResetBeforeAnyRowsChange()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        RecordingPhaseObserver observer = new();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.RemoveAll<IDemoResetPhaseObserver>();
                services.AddSingleton<IDemoResetPhaseObserver>(observer);
            });
        _ = application.CreateClient();
        await using (NpgsqlConnection connection = new(connectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand removeMarker = new(
                """
                DO $body$
                BEGIN
                    IF to_regclass('"DemoDatasetMarkers"') IS NOT NULL THEN
                        DELETE FROM "DemoDatasetMarkers";
                    END IF;
                END
                $body$;
                """,
                connection);
            await removeMarker.ExecuteNonQueryAsync();
        }

        IReadOnlyDictionary<string, string> before = await ReadDemoFingerprintsAsync(connectionString);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        IDemoResetService service = scope.ServiceProvider.GetRequiredService<IDemoResetService>();

        await Assert.ThrowsAsync<DemoModeUnavailableException>(() => service.ResetAsync(new DemoResetCommand(
            "missing-marker",
            DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
            "missing-marker-correlation")));

        Assert.Empty(observer.Phases);
        Assert.Equal(before, await ReadDemoFingerprintsAsync(connectionString));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .DemoResetExecutions.ToListAsync());
    }

    [Fact]
    public async Task DisabledDemoModeRejectsTheResetServiceBeforeAnyExecution()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configuration: new Dictionary<string, string?>
            {
                ["DemoMode:Enabled"] = "false",
                ["DemoMode:DatasetIdentifier"] = null,
                ["DemoMode:DatasetVersion"] = null
            });
        _ = application.CreateClient();
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();

        await Assert.ThrowsAsync<DemoModeUnavailableException>(() =>
            scope.ServiceProvider.GetRequiredService<IDemoResetService>().ResetAsync(new DemoResetCommand(
                "disabled-demo-mode",
                DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                "disabled-demo-mode-correlation")));

        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .DemoResetExecutions.ToListAsync());
    }

    [Fact]
    public async Task MarkerRemovedAfterStartupLeavesCachedUiStateButDeniesResetWithoutExecution()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsync(client, DemoRoleNames.SystemAdministrator);
        (string token, string key, string intentToken) = await GetResetFormAsync(client);

        await using (NpgsqlConnection connection = new(connectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand corruptMarker = new(
                "UPDATE \"DemoDatasetMarkers\" SET \"DatasetVersion\" = 'wrong'",
                connection);
            Assert.Equal(1, await corruptMarker.ExecuteNonQueryAsync());
        }

        using HttpResponseMessage dashboard = await client.GetAsync("/");
        string dashboardHtml = await dashboard.Content.ReadAsStringAsync();
        using HttpResponseMessage getReset = await client.GetAsync("/administration/reset");
        using HttpResponseMessage postReset = await PostResetAsync(client, token, "RESET", key, intentToken);
        using HttpClient anonymous = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        using HttpResponseMessage demoLogin = await anonymous.GetAsync("/demo-login");

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("/administration/reset", dashboardHtml, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, getReset.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, postReset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, demoLogin.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .DemoResetExecutions.ToListAsync());
    }

    [Fact]
    public async Task MarkerChangedAfterPrecheckButBeforeDeleteAbortsWithoutChangingDemoRows()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        CorruptingMarkerPhaseObserver observer = new(connectionString);
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.RemoveAll<IDemoResetPhaseObserver>();
                services.AddSingleton<IDemoResetPhaseObserver>(observer);
            });
        _ = application.CreateClient();
        IReadOnlyDictionary<string, string> before = await ReadDemoFingerprintsAsync(connectionString);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();

        await Assert.ThrowsAsync<DemoResetFailedException>(() =>
            scope.ServiceProvider.GetRequiredService<IDemoResetService>().ResetAsync(new DemoResetCommand(
                "marker-toctou",
                DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                "marker-toctou-correlation")));

        Assert.Equal(before, await ReadDemoFingerprintsAsync(connectionString));
        Assert.Equal(DemoResetState.Failed, (await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .DemoResetExecutions.SingleAsync(item => item.IdempotencyKey == "marker-toctou")).State);
    }

    [Fact]
    public async Task WrongDatabaseDatasetMarkerAtStartupFailsClosedForLoginAndResetService()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (FieldOpsWebApplicationFactory migrationApplication = new(connectionString))
        {
            _ = migrationApplication.CreateClient();
        }

        await using (NpgsqlConnection connection = new(connectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand corruptMarker = new(
                "UPDATE \"DemoDatasetMarkers\" SET \"DatasetIdentifier\" = 'unapproved-dataset'",
                connection);
            Assert.Equal(1, await corruptMarker.ExecuteNonQueryAsync());
        }

        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        using HttpResponseMessage login = await client.GetAsync("/demo-login");
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();

        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        await Assert.ThrowsAsync<DemoModeUnavailableException>(() =>
            scope.ServiceProvider.GetRequiredService<IDemoResetService>().ResetAsync(new DemoResetCommand(
                "wrong-marker-at-startup",
                DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                "wrong-marker-at-startup-correlation")));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>()
            .DemoResetExecutions.ToListAsync());

        await using (NpgsqlConnection connection = new(connectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand repairMarker = new(
                """
                UPDATE "DemoDatasetMarkers"
                SET "DatasetIdentifier" = @datasetIdentifier,
                    "DatasetVersion" = @datasetVersion
                """,
                connection);
            repairMarker.Parameters.AddWithValue(
                "datasetIdentifier",
                DemoModeOptions.ApprovedDatasetIdentifier);
            repairMarker.Parameters.AddWithValue(
                "datasetVersion",
                DemoModeOptions.ApprovedDatasetVersion);
            Assert.Equal(1, await repairMarker.ExecuteNonQueryAsync());
        }

        IDemoModeVerifier verifier = scope.ServiceProvider.GetRequiredService<IDemoModeVerifier>();
        Assert.False(await verifier.IsApprovedAsync());
        Assert.True(await verifier.IsDatabaseApprovedAsync());
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
    public async Task PostCommitDisposeFailureCannotTurnCommittedResetIntoFailure()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        ThrowingTransactionDisposer disposal = new(DemoResetTransactionDisposal.CommittedSuccess);
        CapturingResetLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.RemoveAll<IDemoResetTransactionDisposer>();
                services.AddSingleton<IDemoResetTransactionDisposer>(disposal);
            },
            logging => logging.AddProvider(logs));
        _ = application.CreateClient();
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        IDemoResetService service = scope.ServiceProvider.GetRequiredService<IDemoResetService>();
        DemoResetCommand command = new(
            "post-commit-dispose",
            DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
            "post-commit-dispose-correlation");

        DemoResetResult result = await service.ResetAsync(command);
        DemoResetResult stored = await service.ResetAsync(command with
        {
            CorrelationId = "post-commit-dispose-ignored-correlation"
        });

        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(command.CorrelationId, result.CorrelationId);
        Assert.True(stored.WasAlreadyCompleted);
        Assert.Equal(result.CorrelationId, stored.CorrelationId);
        Assert.Equal(result.DurationMilliseconds, stored.DurationMilliseconds);
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        DemoResetExecution execution = await dbContext.DemoResetExecutions
            .SingleAsync(item => item.IdempotencyKey == command.IdempotencyKey);
        Assert.Equal(DemoResetState.Completed, execution.State);
        Assert.Equal(DemoDataManifest.BranchCount, await dbContext.Branches.CountAsync());
        Assert.Equal(DemoDataManifest.PartyCount, await dbContext.Parties.CountAsync());
        Assert.Equal(DemoDataManifest.SalesOpportunityCount, await dbContext.SalesOpportunities.CountAsync());
        Assert.Equal(DemoDataManifest.WorkOrderCount, await dbContext.WorkOrders.CountAsync());
        Assert.Equal(
            DemoDataManifest.WorkEventCount,
            await dbContext.Set<FieldOps.Domain.Entities.WorkEvent>().CountAsync());
        Assert.Equal(DemoDataManifest.DemoUserCount, await dbContext.Users.CountAsync());
        Assert.Equal(1, await dbContext.AuditEntries.CountAsync(item =>
            item.AggregateId == execution.Id && item.Action == "ResetCompleted"));
        Assert.Equal(0, await dbContext.AuditEntries.CountAsync(item =>
            item.AggregateId == execution.Id && item.Action == "ResetFailed"));
        CapturedResetLog cleanupLog = logs.Entries.Single(entry =>
            entry.Message.StartsWith("Demo reset transaction dispose cleanup failed;", StringComparison.Ordinal) &&
            Equals(entry.Properties.GetValueOrDefault("CorrelationId"), command.CorrelationId));
        Assert.Equal("Unexpected", cleanupLog.Properties["FailureCategory"]);
        Assert.DoesNotContain(ThrowingTransactionDisposer.RawMessage, cleanupLog.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredCompletedDisposeFailureStillReturnsTheStoredSuccessWithoutFailureEvidence()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        ThrowingTransactionDisposer disposal = new(DemoResetTransactionDisposal.StoredCompleted);
        CapturingResetLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.RemoveAll<IDemoResetTransactionDisposer>();
                services.AddSingleton<IDemoResetTransactionDisposer>(disposal);
            },
            logging => logging.AddProvider(logs));
        _ = application.CreateClient();
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        IDemoResetService service = scope.ServiceProvider.GetRequiredService<IDemoResetService>();
        DemoResetCommand command = new(
            "stored-completed-dispose",
            DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
            "stored-completed-original-correlation");
        DemoResetResult first = await service.ResetAsync(command);

        DemoResetResult stored = await service.ResetAsync(command with
        {
            CorrelationId = "stored-completed-ignored-correlation"
        });

        Assert.True(stored.WasAlreadyCompleted);
        Assert.Equal(first.CorrelationId, stored.CorrelationId);
        Assert.Equal(first.DurationMilliseconds, stored.DurationMilliseconds);
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        DemoResetExecution execution = await dbContext.DemoResetExecutions
            .SingleAsync(item => item.IdempotencyKey == command.IdempotencyKey);
        Assert.Equal(DemoResetState.Completed, execution.State);
        Assert.Equal(1, await dbContext.AuditEntries.CountAsync(item =>
            item.AggregateId == execution.Id && item.Action == "ResetCompleted"));
        Assert.Equal(0, await dbContext.AuditEntries.CountAsync(item =>
            item.AggregateId == execution.Id && item.Action == "ResetFailed"));
        CapturedResetLog cleanupLog = logs.Entries.Single(entry =>
            entry.Message.StartsWith("Demo reset transaction dispose cleanup failed;", StringComparison.Ordinal) &&
            Equals(entry.Properties.GetValueOrDefault("CorrelationId"), first.CorrelationId));
        Assert.Equal("Unexpected", cleanupLog.Properties["FailureCategory"]);
        Assert.DoesNotContain(ThrowingTransactionDisposer.RawMessage, cleanupLog.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredFailedDisposeFailureStillReturnsTheOriginalImmutableFailure()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        OneShotThrowingPhaseObserver phaseFailure = new(DemoResetPhase.RowsDeleted);
        ThrowingTransactionDisposer disposal = new(DemoResetTransactionDisposal.StoredFailed);
        CapturingResetLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.RemoveAll<IDemoResetPhaseObserver>();
                services.AddSingleton<IDemoResetPhaseObserver>(phaseFailure);
                services.RemoveAll<IDemoResetTransactionDisposer>();
                services.AddSingleton<IDemoResetTransactionDisposer>(disposal);
            },
            logging => logging.AddProvider(logs));
        _ = application.CreateClient();
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        IDemoResetService service = scope.ServiceProvider.GetRequiredService<IDemoResetService>();
        DemoResetCommand command = new(
            "stored-failed-dispose",
            DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
            "stored-failed-original-correlation");
        await Assert.ThrowsAsync<DemoResetFailedException>(() => service.ResetAsync(command));
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        DemoResetExecution original = await dbContext.DemoResetExecutions
            .SingleAsync(item => item.IdempotencyKey == command.IdempotencyKey);

        DemoResetFailedException stored = await Assert.ThrowsAsync<DemoResetFailedException>(() =>
            service.ResetAsync(command with { CorrelationId = "stored-failed-ignored-correlation" }));

        Assert.True(stored.WasPreviouslyRecorded);
        Assert.Equal(original.CorrelationId, stored.CorrelationId);
        Assert.Equal(original.DurationMilliseconds, stored.DurationMilliseconds);
        Assert.Equal(DemoResetState.Failed, original.State);
        Assert.Equal(1, await dbContext.DemoResetExecutions.CountAsync(item =>
            item.IdempotencyKey == command.IdempotencyKey));
        Assert.Equal(1, await dbContext.AuditEntries.CountAsync(item =>
            item.AggregateId == original.Id && item.Action == "ResetFailed"));
        Assert.Equal(0, await dbContext.AuditEntries.CountAsync(item =>
            item.AggregateId == original.Id && item.Action == "ResetCompleted"));
        CapturedResetLog cleanupLog = logs.Entries.Single(entry =>
            entry.Message.StartsWith("Demo reset transaction dispose cleanup failed;", StringComparison.Ordinal) &&
            Equals(entry.Properties.GetValueOrDefault("CorrelationId"), original.CorrelationId));
        Assert.Equal("Unexpected", cleanupLog.Properties["FailureCategory"]);
        Assert.DoesNotContain(ThrowingTransactionDisposer.RawMessage, cleanupLog.Message, StringComparison.Ordinal);
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

        await AddIdentityAuxiliaryRowsAsync(connectionString);
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
    public async Task CancellationAfterRowsAreDeletedRollsBackAndPersistsFailureEvidenceBoundedly()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (FieldOpsWebApplicationFactory seedApplication = new(connectionString))
        {
            _ = seedApplication.CreateClient();
            await using AsyncServiceScope seedScope = seedApplication.Services.CreateAsyncScope();
            await seedScope.ServiceProvider.GetRequiredService<IDemoResetService>().ResetAsync(new DemoResetCommand(
                "cancel-baseline",
                DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                "cancel-baseline-correlation"));
        }

        IReadOnlyDictionary<string, string> before = await ReadDemoFingerprintsAsync(connectionString);
        using CancellationTokenSource requestCancellation = new();
        CancelingPhaseObserver observer = new(DemoResetPhase.RowsDeleted, requestCancellation);
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.RemoveAll<IDemoResetPhaseObserver>();
                services.AddSingleton<IDemoResetPhaseObserver>(observer);
            });
        _ = application.CreateClient();
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();

        DemoResetFailedException exception = await Assert.ThrowsAsync<DemoResetFailedException>(() =>
            scope.ServiceProvider.GetRequiredService<IDemoResetService>().ResetAsync(
                new DemoResetCommand(
                    "cancel-after-delete",
                    DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                    "cancel-after-delete-correlation"),
                requestCancellation.Token).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal("cancel-after-delete-correlation", exception.CorrelationId);
        Assert.Equal(before, await ReadDemoFingerprintsAsync(connectionString));
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        DemoResetExecution failed = await dbContext.DemoResetExecutions
            .SingleAsync(item => item.IdempotencyKey == "cancel-after-delete");
        Assert.Equal(DemoResetState.Failed, failed.State);
        Assert.True(await dbContext.AuditEntries.AnyAsync(item =>
            item.AggregateId == failed.Id && item.Action == "ResetFailed"));
    }

    [Fact]
    public async Task BackendTerminationDuringResetReturnsBoundedlyAndLogsSanitizedCorrelationEvidence()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (FieldOpsWebApplicationFactory seedApplication = new(connectionString))
        {
            _ = seedApplication.CreateClient();
            await using AsyncServiceScope seedScope = seedApplication.Services.CreateAsyncScope();
            await seedScope.ServiceProvider.GetRequiredService<IDemoResetService>().ResetAsync(new DemoResetCommand(
                "termination-baseline",
                DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                "termination-baseline-correlation"));
        }

        IReadOnlyDictionary<string, string> before = await ReadDemoFingerprintsAsync(connectionString);
        CapturingResetLoggerProvider logs = new();
        await using (FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.RemoveAll<IDemoResetPhaseObserver>();
                services.AddScoped<IDemoResetPhaseObserver>(provider => new TerminatingBackendPhaseObserver(
                    DemoResetPhase.RowsDeleted,
                    connectionString,
                    provider.GetRequiredService<FieldOpsDbContext>()));
            },
            logging => logging.AddProvider(logs)))
        {
            _ = application.CreateClient();
            await using AsyncServiceScope scope = application.Services.CreateAsyncScope();

            DemoResetFailedException exception = await Assert.ThrowsAsync<DemoResetFailedException>(() =>
                scope.ServiceProvider.GetRequiredService<IDemoResetService>().ResetAsync(new DemoResetCommand(
                    "terminated-backend",
                    DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                    "terminated-backend-correlation")).WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal("terminated-backend-correlation", exception.CorrelationId);
        }

        Assert.Equal(before, await ReadDemoFingerprintsAsync(connectionString));
        CapturedResetLog fallback = logs.Entries.Single(entry =>
            entry.Message.StartsWith("Demo reset failed;", StringComparison.Ordinal) &&
            Equals(entry.Properties.GetValueOrDefault("CorrelationId"), "terminated-backend-correlation"));
        Assert.Equal("DatabaseUnavailable", fallback.Properties["FailureCategory"]);
        Assert.Contains(logs.Entries, entry =>
            entry.Message.StartsWith("Demo reset transaction rollback cleanup failed;", StringComparison.Ordinal) &&
            Equals(entry.Properties.GetValueOrDefault("CorrelationId"), "terminated-backend-correlation"));
        string serializedLogs = string.Join('\n', logs.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain("terminating connection due to administrator command", serializedLogs, StringComparison.OrdinalIgnoreCase);

        await using FieldOpsWebApplicationFactory verificationApplication = new(connectionString);
        _ = verificationApplication.CreateClient();
        await using AsyncServiceScope verificationScope = verificationApplication.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = verificationScope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        DemoResetExecution failed = await dbContext.DemoResetExecutions
            .SingleAsync(item => item.IdempotencyKey == "terminated-backend");
        Assert.Equal(DemoResetState.Failed, failed.State);
        Assert.True(await dbContext.AuditEntries.AnyAsync(item =>
            item.AggregateId == failed.Id && item.Action == "ResetFailed"));
    }

    [Fact]
    public async Task DatabaseOutageDuringFailureLogsTheSecondSanitizedFallbackAndTerminatesBoundedly()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (FieldOpsWebApplicationFactory seedApplication = new(connectionString))
        {
            _ = seedApplication.CreateClient();
            await using AsyncServiceScope seedScope = seedApplication.Services.CreateAsyncScope();
            await seedScope.ServiceProvider.GetRequiredService<IDemoResetService>().ResetAsync(new DemoResetCommand(
                "outage-baseline",
                DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                "outage-baseline-correlation"));
        }

        IReadOnlyDictionary<string, string> before = await ReadDemoFingerprintsAsync(connectionString);
        DatabaseAvailabilityController databaseAvailability = new(connectionString);
        CapturingResetLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.RemoveAll<IDemoResetPhaseObserver>();
                services.AddScoped<IDemoResetPhaseObserver>(provider => new DisablingDatabasePhaseObserver(
                    DemoResetPhase.RowsDeleted,
                    databaseAvailability,
                    provider.GetRequiredService<FieldOpsDbContext>()));
            },
            logging => logging.AddProvider(logs));
        _ = application.CreateClient();
        try
        {
            await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
            DemoResetFailedException exception = await Assert.ThrowsAsync<DemoResetFailedException>(() =>
                scope.ServiceProvider.GetRequiredService<IDemoResetService>().ResetAsync(new DemoResetCommand(
                    "database-outage",
                    DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id,
                    "database-outage-correlation")).WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.Equal("database-outage-correlation", exception.CorrelationId);
        }
        finally
        {
            await databaseAvailability.EnableConnectionsAsync();
        }

        Assert.Equal(before, await ReadDemoFingerprintsAsync(connectionString));
        CapturedResetLog persistenceFallback = logs.Entries.Single(entry =>
            entry.Message.StartsWith("Demo reset failure evidence persistence failed;", StringComparison.Ordinal) &&
            Equals(entry.Properties.GetValueOrDefault("CorrelationId"), "database-outage-correlation"));
        Assert.True(
            persistenceFallback.Properties["FailureCategory"]?.ToString() is
                "DatabaseUnavailable" or "Interrupted");
        Assert.DoesNotContain(
            "is not currently accepting connections",
            string.Join('\n', logs.Entries.Select(entry => entry.Message)),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedKeyReturnsStoredFailureAndANewKeySuccessPreservesImmutableFailureHistory()
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

        DemoResetFailedException repeated = await Assert.ThrowsAsync<DemoResetFailedException>(() => service.ResetAsync(
            failedCommand with { CorrelationId = "retry-failed-second" }));
        DemoResetResult newKeyResult = await service.ResetAsync(new DemoResetCommand(
            "retry-new-key",
            failedCommand.ActorUserId,
            "retry-new-key-correlation"));

        Assert.Equal("retry-failed-first", repeated.CorrelationId);
        Assert.True(repeated.WasPreviouslyRecorded);
        Assert.NotNull(repeated.DurationMilliseconds);
        Assert.False(newKeyResult.WasAlreadyCompleted);
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        DemoResetExecution failedExecution = await dbContext.DemoResetExecutions
            .SingleAsync(item => item.IdempotencyKey == failedCommand.IdempotencyKey);
        Assert.Equal(DemoResetState.Failed, failedExecution.State);
        Assert.Equal("retry-failed-first", failedExecution.CorrelationId);
        Assert.Equal(1, await dbContext.AuditEntries.CountAsync(item =>
            item.AggregateId == failedExecution.Id && item.Action == "ResetFailed"));
        Assert.Equal(2, await dbContext.DemoResetExecutions.CountAsync());
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
        Assert.Equal(DemoDataManifest.SeedAuditEntryCount + 4, await dbContext.AuditEntries.CountAsync());
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

        DemoResetExecution latestExecution = await dbContext.DemoResetExecutions
            .SingleAsync(item => item.IdempotencyKey == "two-reset-second");
        FieldOps.Domain.Entities.AuditEntry started = await dbContext.AuditEntries
            .SingleAsync(item => item.AggregateId == latestExecution.Id && item.Action == "ResetStarted");
        FieldOps.Domain.Entities.AuditEntry completed = await dbContext.AuditEntries
            .SingleAsync(item => item.AggregateId == latestExecution.Id && item.Action == "ResetCompleted");
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

    private static async Task<string> LoginAsync(HttpClient client, string role)
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
        string setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal) &&
            !value.StartsWith(".AspNetCore.Identity.Application=;", StringComparison.Ordinal));
        return setCookie[..setCookie.IndexOf(';')];
    }

    private static async Task<(string Token, string IdempotencyKey, string IntentToken)> GetResetFormAsync(HttpClient client)
    {
        string html = await client.GetStringAsync("/administration/reset");
        string token = RequestVerificationTokenRegex().Match(html).Groups[1].Value;
        string key = Regex.Match(html, "name=\"IdempotencyKey\"[^>]*value=\"([^\"]+)\"")
            .Groups[1].Value;
        string intentToken = Regex.Match(html, "name=\"IntentToken\"[^>]*value=\"([^\"]+)\"")
            .Groups[1].Value;
        Assert.NotEmpty(token);
        Assert.NotEmpty(key);
        Assert.NotEmpty(intentToken);
        return (token, key, intentToken);
    }

    private static Task<HttpResponseMessage> PostResetAsync(
        HttpClient client,
        string token,
        string confirmation,
        string idempotencyKey,
        string intentToken)
    {
        return client.SendAsync(CreateResetRequest(token, confirmation, idempotencyKey, intentToken));
    }

    private static HttpRequestMessage CreateResetRequest(
        string token,
        string confirmation,
        string idempotencyKey,
        string intentToken)
    {
        return new HttpRequestMessage(HttpMethod.Post, "/administration/reset")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Confirmation"] = confirmation,
                ["IdempotencyKey"] = idempotencyKey,
                ["IntentToken"] = intentToken,
                ["__RequestVerificationToken"] = token
            })
        };
    }

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
            "AspNetUserClaims",
            "AspNetUserLogins",
            "AspNetUserTokens",
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

    private static async Task AddIdentityAuxiliaryRowsAsync(string connectionString)
    {
        string userId = DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id;
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            INSERT INTO "AspNetUserClaims" ("UserId", "ClaimType", "ClaimValue")
            VALUES (@userId, 'demo-reset-rollback-claim', 'fictional-value');
            INSERT INTO "AspNetUserLogins" ("LoginProvider", "ProviderKey", "ProviderDisplayName", "UserId")
            VALUES ('demo-reset-test', 'fictional-provider-key', 'Fictional provider', @userId);
            INSERT INTO "AspNetUserTokens" ("UserId", "LoginProvider", "Name", "Value")
            VALUES (@userId, 'demo-reset-test', 'fictional-token', 'fictional-value');
            """,
            connection);
        command.Parameters.AddWithValue("userId", userId);
        Assert.Equal(3, await command.ExecuteNonQueryAsync());
    }

    private static async Task<IReadOnlyDictionary<string, DemoIdentityState>> ReadDemoIdentityStateAsync(
        string connectionString)
    {
        string[] userIds = DemoDataManifest.UsersByRole.Values.Select(user => user.Id).ToArray();
        Dictionary<string, DemoIdentityState> state = new(StringComparer.Ordinal);
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT u."Id", u."PasswordHash", u."SecurityStamp", u."ConcurrencyStamp",
                   string_agg(ur."RoleId", ',' ORDER BY ur."RoleId") AS role_ids
            FROM "AspNetUsers" AS u
            LEFT JOIN "AspNetUserRoles" AS ur ON ur."UserId" = u."Id"
            WHERE u."Id" = ANY (@userIds)
            GROUP BY u."Id", u."PasswordHash", u."SecurityStamp", u."ConcurrencyStamp"
            ORDER BY u."Id"
            """,
            connection);
        command.Parameters.AddWithValue("userIds", userIds);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            state.Add(reader.GetString(0), new DemoIdentityState(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4)));
        }

        return state;
    }

    private sealed record DemoIdentityState(
        string? PasswordHash,
        string SecurityStamp,
        string ConcurrencyStamp,
        string RoleIds);

    private sealed class ThrowingPhaseObserver(DemoResetPhase failurePhase) : IDemoResetPhaseObserver
    {
        public Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken)
        {
            return phase == failurePhase
                ? Task.FromException(new InvalidOperationException("Injected reset failure."))
                : Task.CompletedTask;
        }
    }

    private sealed class RecordingPhaseObserver : IDemoResetPhaseObserver
    {
        public List<DemoResetPhase> Phases { get; } = [];

        public Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken)
        {
            Phases.Add(phase);
            return Task.CompletedTask;
        }
    }

    private sealed class CorruptingMarkerPhaseObserver(string connectionString) : IDemoResetPhaseObserver
    {
        private int _changed;

        public async Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken)
        {
            if (phase != DemoResetPhase.LockAcquired || Interlocked.CompareExchange(ref _changed, 1, 0) != 0)
            {
                return;
            }

            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlCommand command = new(
                "UPDATE \"DemoDatasetMarkers\" SET \"DatasetVersion\" = 'changed-during-reset'",
                connection);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        }
    }

    private sealed class CancelingPhaseObserver(
        DemoResetPhase cancelPhase,
        CancellationTokenSource cancellation) : IDemoResetPhaseObserver
    {
        public Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken)
        {
            if (phase == cancelPhase)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TerminatingBackendPhaseObserver(
        DemoResetPhase terminatePhase,
        string connectionString,
        FieldOpsDbContext dbContext) : IDemoResetPhaseObserver
    {
        public async Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken)
        {
            if (phase != terminatePhase)
            {
                return;
            }

            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken);
            int resetBackendPid = ((NpgsqlConnection)dbContext.Database.GetDbConnection()).ProcessID;
            await using NpgsqlCommand terminate = new(
                "SELECT pg_terminate_backend(@resetBackendPid)",
                connection);
            terminate.Parameters.AddWithValue("resetBackendPid", resetBackendPid);
            Assert.True((bool)(await terminate.ExecuteScalarAsync(cancellationToken) ?? false));
        }
    }

    private sealed class DisablingDatabasePhaseObserver(
        DemoResetPhase disablePhase,
        DatabaseAvailabilityController databaseAvailability,
        FieldOpsDbContext dbContext) : IDemoResetPhaseObserver
    {
        public async Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken)
        {
            if (phase != disablePhase)
            {
                return;
            }

            int resetBackendPid = ((NpgsqlConnection)dbContext.Database.GetDbConnection()).ProcessID;
            await databaseAvailability.DisableAndTerminateAsync(resetBackendPid, cancellationToken);
        }
    }

    private sealed class DatabaseAvailabilityController(string connectionString)
    {
        private readonly NpgsqlConnectionStringBuilder _databaseConnection = new(connectionString);
        private int _disabled;

        public async Task DisableAndTerminateAsync(int resetBackendPid, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _disabled, 1, 0) != 0)
            {
                return;
            }

            await using NpgsqlConnection admin = new(AdminConnectionString());
            await admin.OpenAsync(cancellationToken);
            string databaseName = DatabaseName();
            string databaseIdentifier = databaseName.Replace("\"", "\"\"", StringComparison.Ordinal);
            await using (NpgsqlCommand disable = new(
                $"ALTER DATABASE \"{databaseIdentifier}\" WITH ALLOW_CONNECTIONS false",
                admin))
            {
                await disable.ExecuteNonQueryAsync(cancellationToken);
            }

            await using NpgsqlCommand terminate = new(
                "SELECT pg_terminate_backend(@resetBackendPid)",
                admin);
            terminate.Parameters.AddWithValue("resetBackendPid", resetBackendPid);
            Assert.True((bool)(await terminate.ExecuteScalarAsync(cancellationToken) ?? false));
        }

        public async Task EnableConnectionsAsync()
        {
            if (Volatile.Read(ref _disabled) == 0)
            {
                return;
            }

            await using NpgsqlConnection admin = new(AdminConnectionString());
            await admin.OpenAsync();
            string databaseName = DatabaseName();
            string databaseIdentifier = databaseName.Replace("\"", "\"\"", StringComparison.Ordinal);
            await using NpgsqlCommand enable = new(
                $"ALTER DATABASE \"{databaseIdentifier}\" WITH ALLOW_CONNECTIONS true",
                admin);
            await enable.ExecuteNonQueryAsync();
        }

        private string AdminConnectionString()
        {
            NpgsqlConnectionStringBuilder admin = new(_databaseConnection.ConnectionString)
            {
                Database = "postgres",
                Pooling = false
            };
            return admin.ConnectionString;
        }

        private string DatabaseName()
        {
            return _databaseConnection.Database
                ?? throw new InvalidOperationException("The Task 12 database name is required.");
        }
    }

    private sealed class CapturingResetLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<CapturedResetLog> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingResetLogger(this, categoryName);
        }

        public void Dispose()
        {
        }

        private sealed class CapturingResetLogger(
            CapturingResetLoggerProvider provider,
            string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!category.StartsWith("FieldOps.Infrastructure.Demo", StringComparison.Ordinal) ||
                    state is not IEnumerable<KeyValuePair<string, object?>> properties)
                {
                    return;
                }

                provider.Entries.Enqueue(new CapturedResetLog(
                    formatter(state, exception),
                    properties.Where(item => item.Key != "{OriginalFormat}")
                        .ToDictionary(item => item.Key, item => item.Value)));
            }
        }
    }

    private sealed record CapturedResetLog(
        string Message,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }

    private sealed class OneShotThrowingPhaseObserver(DemoResetPhase failurePhase) : IDemoResetPhaseObserver
    {
        private int _thrown;

        public Task ObserveAsync(DemoResetPhase phase, CancellationToken cancellationToken)
        {
            return phase == failurePhase && Interlocked.CompareExchange(ref _thrown, 1, 0) == 0
                ? Task.FromException(new InvalidOperationException("Injected one-shot reset failure."))
                : Task.CompletedTask;
        }
    }

    private sealed class ThrowingTransactionDisposer(
        DemoResetTransactionDisposal failureDisposal) : IDemoResetTransactionDisposer
    {
        public const string RawMessage = "raw simulated transaction disposal detail";

        public async Task DisposeAsync(
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
            DemoResetTransactionDisposal disposal,
            CancellationToken cancellationToken)
        {
            await transaction.DisposeAsync();
            if (disposal == failureDisposal)
            {
                throw new InvalidOperationException(RawMessage);
            }
        }
    }
}