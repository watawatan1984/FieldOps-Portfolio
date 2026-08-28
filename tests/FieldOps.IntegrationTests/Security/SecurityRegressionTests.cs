using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Infrastructure.Demo;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace FieldOps.IntegrationTests.Security;

[Collection(DatabaseCollection.Name)]
public sealed class SecurityRegressionTests(PostgresFixture postgres)
{
    private const string ExpectedContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; style-src-attr 'unsafe-inline'; " +
        "img-src 'self' data:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    [Fact]
    public async Task SecurityHeadersCoverNormalForbiddenNotFoundConflictAndUnhandledResponsesExactlyOnce()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient anonymous = CreateHttpsClient(application);
        using HttpClient manager = CreateHttpsClient(application);
        await LoginAsAsync(manager, DemoRoleNames.BranchManager);

        using HttpResponseMessage normal = await anonymous.GetAsync("/demo-login");
        using HttpResponseMessage forbidden = await manager.GetAsync("/administration/reset");
        using HttpResponseMessage notFound = await anonymous.GetAsync("/diagnostics-probe/not-found");
        using HttpResponseMessage conflict = await anonymous.GetAsync("/diagnostics-probe/concurrency-error");
        using HttpResponseMessage unhandled = await anonymous.GetAsync("/diagnostics-probe/unhandled");

        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, unhandled.StatusCode);
        Assert.All([normal, forbidden, notFound, conflict, unhandled], AssertSecurityHeaders);
    }

    [Fact]
    public async Task ProductionHttpsResponseIncludesHstsWithoutChangingSecureIdentityCookieContract()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString, environment: "Production");
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://fieldops.test")
        });

        using HttpResponseMessage page = await client.GetAsync("/demo-login");
        string html = await page.Content.ReadAsStringAsync();
        string antiforgeryToken = GetInputValue(html, "__RequestVerificationToken");
        string roleToken = Regex.Match(
            html,
            $"data-role=\"{Regex.Escape(DemoRoleNames.SystemAdministrator)}\".*?name=\"roleToken\" value=\"([^\"]+)\"",
            RegexOptions.Singleline).Groups[1].Value;

        using HttpResponseMessage login = await client.PostAsync(
            "/demo-login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = roleToken,
                ["__RequestVerificationToken"] = antiforgeryToken
            }));

        Assert.Equal("max-age=2592000", Assert.Single(page.Headers.GetValues("Strict-Transport-Security")));
        string cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal) &&
            !value.StartsWith(".AspNetCore.Identity.Application=;", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        AssertSecurityHeaders(login);
    }

    [Fact]
    public async Task DemoLoginAllowsExactlyTwentyFailedAttemptsPerClientIpThenReturnsSafe429()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configureServices: services => services.AddSingleton<IStartupFilter>(
                new FixedRemoteIpStartupFilter(IPAddress.Parse("192.0.2.10"))));
        using HttpClient client = CreateHttpsClient(application);
        string html = await client.GetStringAsync("/demo-login");
        string antiforgeryToken = GetInputValue(html, "__RequestVerificationToken");

        for (int attempt = 1; attempt <= 20; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, $"/demo-login?role={attempt}&idempotencyKey={Guid.NewGuid():N}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["roleToken"] = $"invalid-role-token-{attempt}",
                    ["__RequestVerificationToken"] = antiforgeryToken
                })
            };
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", $"203.0.113.{attempt}");
            request.Headers.TryAddWithoutValidation("X-Role", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using HttpRequestMessage rejectedRequest = new(HttpMethod.Post, "/demo-login?role=SystemAdministrator")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = "still-invalid",
                ["__RequestVerificationToken"] = antiforgeryToken
            })
        };
        rejectedRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.200");
        rejectedRequest.Headers.Add("X-Correlation-ID", "login-rate-limit-test");
        using HttpResponseMessage rejected = await client.SendAsync(rejectedRequest);
        string body = await rejected.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(int.Parse(Assert.Single(rejected.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal("{\"correlationId\":\"login-rate-limit-test\"}", body);
        AssertSecurityHeaders(rejected);
    }

    [Fact]
    public async Task ExplicitTrustedProxyUsesForwardedClientIpForDistinctLoginWindows()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configureServices: services => services.AddSingleton<IStartupFilter>(
                new FixedRemoteIpStartupFilter(IPAddress.Parse("192.0.2.10"))),
            configuration: new Dictionary<string, string?>
            {
                ["TrustedProxy:ForwardLimit"] = "1",
                ["TrustedProxy:KnownProxies:0"] = "192.0.2.10"
            });
        using HttpClient client = CreateHttpsClient(application);
        string html = await client.GetStringAsync("/demo-login");
        string antiforgeryToken = GetInputValue(html, "__RequestVerificationToken");

        for (int clientNumber = 1; clientNumber <= 21; clientNumber++)
        {
            using HttpRequestMessage distinctClient = CreateInvalidLoginRequest(
                antiforgeryToken,
                $"198.51.100.{clientNumber}",
                $"distinct-{clientNumber}");
            using HttpResponseMessage response = await client.SendAsync(distinctClient);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        for (int attempt = 2; attempt <= 20; attempt++)
        {
            using HttpRequestMessage sameClient = CreateInvalidLoginRequest(
                antiforgeryToken,
                "198.51.100.1",
                $"same-{attempt}");
            using HttpResponseMessage response = await client.SendAsync(sameClient);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using HttpRequestMessage rejectedRequest = CreateInvalidLoginRequest(
            antiforgeryToken,
            "198.51.100.1",
            "same-21");
        rejectedRequest.Headers.Add("X-Correlation-ID", "trusted-proxy-rate-limit-test");
        using HttpResponseMessage rejected = await client.SendAsync(rejectedRequest);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("{\"correlationId\":\"trusted-proxy-rate-limit-test\"}", await rejected.Content.ReadAsStringAsync());
        AssertSecurityHeaders(rejected);
    }

    [Theory]
    [InlineData("TrustedProxy:KnownProxies:0", "not-an-ip-address")]
    [InlineData("TrustedProxy:KnownNetworks:0", "192.0.2.0/not-a-prefix")]
    [InlineData("TrustedProxy:ForwardLimit", "100")]
    public async Task InvalidTrustedProxyConfigurationFailsStartup(string key, string value)
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configuration: new Dictionary<string, string?> { [key] = value });

        _ = Assert.Throws<OptionsValidationException>(() => application.CreateClient());
    }

    [Fact]
    public async Task ResetAllowsExactlyThreeFinalPostsPerUserWhileGetsAndForbiddenUsersAreNotCounted()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient manager = CreateHttpsClient(application);
        await LoginAsAsync(manager, DemoRoleNames.BranchManager);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            using HttpResponseMessage forbidden = await manager.PostAsync(
                $"/administration/reset?idempotencyKey={Guid.NewGuid():N}",
                new FormUrlEncodedContent(new Dictionary<string, string>()));
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using HttpClient administrator = CreateHttpsClient(application);
        await LoginAsAsync(administrator, DemoRoleNames.SystemAdministrator);
        for (int read = 0; read < 10; read++)
        {
            using HttpResponseMessage page = await administrator.GetAsync("/administration/reset");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        }

        string html = await administrator.GetStringAsync("/administration/reset");
        Dictionary<string, string> form = new()
        {
            ["IdempotencyKey"] = GetInputValue(html, "IdempotencyKey"),
            ["IntentToken"] = GetInputValue(html, "IntentToken"),
            ["Confirmation"] = "NOT-RESET",
            ["__RequestVerificationToken"] = GetInputValue(html, "__RequestVerificationToken")
        };

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using HttpResponseMessage response = await administrator.PostAsync(
                $"/administration/reset?role={attempt}&idempotencyKey={Guid.NewGuid():N}",
                new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using HttpRequestMessage rejectedRequest = new(HttpMethod.Post, "/administration/reset?role=spoofed")
        {
            Content = new FormUrlEncodedContent(form)
        };
        rejectedRequest.Headers.TryAddWithoutValidation("X-User-ID", Guid.NewGuid().ToString());
        rejectedRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.250");
        rejectedRequest.Headers.Add("X-Correlation-ID", "reset-rate-limit-test");
        using HttpResponseMessage rejected = await administrator.SendAsync(rejectedRequest);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(int.Parse(Assert.Single(rejected.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal("{\"correlationId\":\"reset-rate-limit-test\"}", await rejected.Content.ReadAsStringAsync());
        AssertSecurityHeaders(rejected);
    }

    [Fact]
    public async Task EveryRepresentativeMutationLoginLogoutAndResetRejectsMissingAntiforgery()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateHttpsClient(application);
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
        Guid missing = Guid.NewGuid();
        (string Path, Dictionary<string, string> Form)[] cases =
        [
            ("/demo-login", new() { ["roleToken"] = "invalid" }),
            ("/demo-login/logout", new()),
            ("/administration/reset", new() { ["Confirmation"] = "RESET" }),
            ("/parties/create", new()),
            ($"/parties/{missing}/edit", new()),
            ($"/parties/{missing}/share", new()),
            ("/sales/create", new()),
            ($"/sales/{missing}/edit", new()),
            ($"/sales/{missing}/transition", new()),
            ($"/work-orders/from-opportunity/{missing}", new()),
            ($"/work-orders/{missing}/edit", new()),
            ($"/work-orders/{missing}/transition", new()),
            ($"/work-orders/{missing}/events/add", new())
        ];

        foreach ((string path, Dictionary<string, string> form) in cases)
        {
            using HttpResponseMessage response = await client.PostAsync(path, new FormUrlEncodedContent(form));
            Assert.True(
                response.StatusCode == HttpStatusCode.BadRequest,
                $"Expected antiforgery rejection for {path}, received {(int)response.StatusCode}.");
        }
    }

    [Fact]
    public async Task ProductionFormsIgnoreOverpostedIdentityOwnershipAssignmentAndBranchFields()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        OverpostSeed seed = await SeedOverpostResourcesAsync(application);
        using HttpClient client = CreateHttpsClient(application);
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
        string spoofUserId = $"spoof-user-{Guid.NewGuid():N}";
        string spoofOwnerId = $"spoof-owner-{Guid.NewGuid():N}";
        string spoofAssigneeId = $"spoof-assignee-{Guid.NewGuid():N}";

        string partyHtml = await client.GetStringAsync($"/parties/{seed.PartyId}/edit?branchId={seed.BranchId}");
        using HttpResponseMessage partyResponse = await client.PostAsync(
            $"/parties/{seed.PartyId}/edit",
            OverpostedForm(
                new Dictionary<string, string>
                {
                    ["Id"] = seed.PartyId.ToString(),
                    ["BranchId"] = seed.BranchId.ToString(),
                    ["Version"] = GetInputValue(partyHtml, "Version"),
                    ["OrganizationName"] = "Fictional Overpost Customer Updated",
                    ["IsCustomer"] = "true",
                    ["__RequestVerificationToken"] = GetInputValue(partyHtml, "__RequestVerificationToken")
                },
                spoofUserId,
                spoofOwnerId,
                spoofAssigneeId,
                seed.OtherBranchId));
        Assert.Equal(HttpStatusCode.Redirect, partyResponse.StatusCode);

        string salesHtml = await client.GetStringAsync($"/sales/{seed.SalesOpportunityId}");
        using HttpResponseMessage salesResponse = await client.PostAsync(
            $"/sales/{seed.SalesOpportunityId}/transition",
            OverpostedForm(
                new Dictionary<string, string>
                {
                    ["Version"] = GetInputValue(salesHtml, "Version"),
                    ["NextStatus"] = SalesOpportunityStatus.Contacted.ToString(),
                    ["__RequestVerificationToken"] = GetInputValue(salesHtml, "__RequestVerificationToken")
                },
                spoofUserId,
                spoofOwnerId,
                spoofAssigneeId,
                seed.OtherBranchId));
        Assert.Equal(HttpStatusCode.Redirect, salesResponse.StatusCode);

        string workHtml = await client.GetStringAsync($"/work-orders/{seed.WorkOrderId}");
        using HttpResponseMessage workResponse = await client.PostAsync(
            $"/work-orders/{seed.WorkOrderId}/transition",
            OverpostedForm(
                new Dictionary<string, string>
                {
                    ["Version"] = GetInputValue(workHtml, "Version"),
                    ["NextStatus"] = WorkOrderStatus.Cancelled.ToString(),
                    ["__RequestVerificationToken"] = GetInputValue(workHtml, "__RequestVerificationToken")
                },
                spoofUserId,
                spoofOwnerId,
                spoofAssigneeId,
                seed.OtherBranchId));
        Assert.Equal(HttpStatusCode.Redirect, workResponse.StatusCode);

        string responseSurface = string.Join('|', new[] { partyResponse, salesResponse, workResponse }
            .Select(response => response.Headers.Location?.OriginalString ?? string.Empty));
        Assert.DoesNotContain(spoofUserId, responseSurface, StringComparison.Ordinal);
        Assert.DoesNotContain(spoofOwnerId, responseSurface, StringComparison.Ordinal);
        Assert.DoesNotContain(spoofAssigneeId, responseSurface, StringComparison.Ordinal);
        Assert.DoesNotContain(seed.OtherBranchId.ToString(), responseSurface, StringComparison.OrdinalIgnoreCase);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Party party = await db.Parties.Include(item => item.BranchAssignments).SingleAsync(item => item.Id == seed.PartyId);
        SalesOpportunity sales = await db.SalesOpportunities.SingleAsync(item => item.Id == seed.SalesOpportunityId);
        WorkOrder work = await db.WorkOrders.SingleAsync(item => item.Id == seed.WorkOrderId);
        Assert.Equal("Fictional Overpost Customer Updated", party.OrganizationName);
        Assert.Equal([seed.BranchId], party.BranchAssignments.Select(item => item.BranchId));
        Assert.Equal((seed.BranchId, seed.OwnerUserId, seed.AssignedUserId, SalesOpportunityStatus.Contacted),
            (sales.BranchId, sales.OwnerUserId, sales.AssignedUserId, sales.Status));
        Assert.Equal((seed.BranchId, seed.AssignedUserId, WorkOrderStatus.Cancelled),
            (work.BranchId, work.AssignedUserId, work.Status));

        AuditEntry partyAudit = await db.AuditEntries.SingleAsync(item => item.AggregateId == seed.PartyId);
        AuditEntry salesAudit = await db.AuditEntries.SingleAsync(item => item.AggregateId == seed.SalesOpportunityId);
        AuditEntry workAudit = await db.AuditEntries.SingleAsync(item => item.AggregateId == seed.WorkOrderId);
        Assert.All([partyAudit, salesAudit, workAudit], audit => Assert.Equal(seed.ActorUserId, audit.ActorUserId));
        Assert.Equal((seed.BranchId, "OrganizationName"), (partyAudit.BranchId, partyAudit.ChangeSummary));
        Assert.Equal((seed.BranchId, "NextStatus"), (salesAudit.BranchId, salesAudit.ChangeSummary));
        Assert.Equal((seed.BranchId, "NextStatus"), (workAudit.BranchId, workAudit.ChangeSummary));
        Assert.DoesNotContain(
            "UserId",
            string.Join('|', new[] { partyAudit, salesAudit, workAudit }.Select(audit => audit.ChangeSummary)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnUrlCannotOpenRedirectLoginOrLogoutAndUnsupportedMethodsReturn405()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateHttpsClient(application);
        string html = await client.GetStringAsync("/demo-login");
        using HttpResponseMessage login = await client.PostAsync(
            "/demo-login?returnUrl=https%3A%2F%2Fevil.example%2Fsteal",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = Regex.Match(
                    html,
                    $"data-role=\"{Regex.Escape(DemoRoleNames.SystemAdministrator)}\".*?name=\"roleToken\" value=\"([^\"]+)\"",
                    RegexOptions.Singleline).Groups[1].Value,
                ["__RequestVerificationToken"] = GetInputValue(html, "__RequestVerificationToken")
            }));
        Assert.Equal("/", login.Headers.Location?.OriginalString);

        string dashboard = await client.GetStringAsync("/");
        foreach (HttpMethod method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using HttpResponseMessage unsupported = await client.SendAsync(new HttpRequestMessage(method, "/demo-login"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, unsupported.StatusCode);
            AssertSecurityHeaders(unsupported);
        }

        using HttpResponseMessage logout = await client.PostAsync(
            "/demo-login/logout?returnUrl=https%3A%2F%2Fevil.example%2Fsteal",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = GetInputValue(dashboard, "__RequestVerificationToken")
            }));
        Assert.Equal("/demo-login", logout.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task OversizedSearchValuesAreRejectedBeforeDatabaseQueries()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateHttpsClient(application);
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
        Guid branchId;
        await using (AsyncServiceScope scope = application.Services.CreateAsyncScope())
        {
            FieldOpsDbContext db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
            branchId = await db.Branches.Select(branch => branch.Id).FirstAsync();
        }

        string oversized = new('x', 4096);
        foreach (string path in new[]
        {
            $"/parties?branchId={branchId}&search={oversized}",
            $"/sales?branchId={branchId}&search={oversized}",
            $"/work-history?branchId={branchId}&keyword={oversized}"
        })
        {
            using HttpResponseMessage response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            AssertSecurityHeaders(response);
        }
    }

    [Fact]
    public async Task RequestLogsCoverUnmatched404Forbidden403AndAuthorized429WithoutRawInputs()
    {
        const string secret = "private.person@example.test";
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        RequestOutcomeLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configureLogging: logging => logging.AddProvider(logs));
        using HttpClient administrator = CreateHttpsClient(application);
        await LoginAsAsync(administrator, DemoRoleNames.SystemAdministrator);

        using HttpRequestMessage missingRequest = new(
            HttpMethod.Get,
            $"/unmatched/{Uri.EscapeDataString(secret)}?filter={Uri.EscapeDataString(secret)}");
        missingRequest.Headers.Add("X-Correlation-ID", "unmatched-log-test");
        using HttpResponseMessage missing = await administrator.SendAsync(missingRequest);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using HttpClient manager = CreateHttpsClient(application);
        await LoginAsAsync(manager, DemoRoleNames.BranchManager);
        using HttpRequestMessage forbiddenRequest = new(HttpMethod.Get, "/administration/reset");
        forbiddenRequest.Headers.Add("X-Correlation-ID", "forbidden-log-test");
        using HttpResponseMessage forbidden = await manager.SendAsync(forbiddenRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        string html = await administrator.GetStringAsync("/administration/reset");
        Dictionary<string, string> form = new()
        {
            ["IdempotencyKey"] = GetInputValue(html, "IdempotencyKey"),
            ["IntentToken"] = GetInputValue(html, "IntentToken"),
            ["Confirmation"] = secret,
            ["__RequestVerificationToken"] = GetInputValue(html, "__RequestVerificationToken")
        };
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using HttpResponseMessage counted = await administrator.PostAsync("/administration/reset", new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.BadRequest, counted.StatusCode);
        }
        using HttpRequestMessage limitedRequest = new(HttpMethod.Post, "/administration/reset?role=spoofed")
        {
            Content = new FormUrlEncodedContent(form)
        };
        limitedRequest.Headers.Add("X-Correlation-ID", "limited-log-test");
        using HttpResponseMessage limited = await administrator.SendAsync(limitedRequest);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        RequestOutcomeLog unmatchedLog = logs.Single("unmatched-log-test");
        RequestOutcomeLog forbiddenLog = logs.Single("forbidden-log-test");
        RequestOutcomeLog limitedLog = logs.Single("limited-log-test");
        Assert.Equal(("unmatched", 404, "failure"), (unmatchedLog.Route, unmatchedLog.StatusCode, unmatchedLog.Outcome));
        Assert.Equal(("administration/reset", 403, "failure"), (forbiddenLog.Route, forbiddenLog.StatusCode, forbiddenLog.Outcome));
        Assert.Equal(("administration/reset", 429, "failure"), (limitedLog.Route, limitedLog.StatusCode, limitedLog.Outcome));
        Assert.DoesNotContain(secret, string.Join('|', logs.Entries.Select(entry => entry.Message)), StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateHttpsClient(FieldOpsWebApplicationFactory application) =>
        application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private static HttpRequestMessage CreateInvalidLoginRequest(
        string antiforgeryToken,
        string forwardedFor,
        string roleToken)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/demo-login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = roleToken,
                ["__RequestVerificationToken"] = antiforgeryToken
            })
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return request;
    }

    private static FormUrlEncodedContent OverpostedForm(
        Dictionary<string, string> intendedFields,
        string userId,
        string ownerUserId,
        string assignedUserId,
        Guid branchId)
    {
        intendedFields["UserId"] = userId;
        intendedFields["OwnerUserId"] = ownerUserId;
        intendedFields["AssignedUserId"] = assignedUserId;
        intendedFields.TryAdd("BranchId", branchId.ToString());
        return new FormUrlEncodedContent(intendedFields);
    }

    private static async Task<OverpostSeed> SeedOverpostResourcesAsync(
        FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch[] branches = await db.Branches.OrderBy(branch => branch.Name).Take(2).ToArrayAsync();
        Assert.Equal(2, branches.Length);
        Branch branch = branches[0];
        Branch otherBranch = branches[1];
        string ownerUserId = DemoDataManifest.UsersByRole[DemoRoleNames.SalesRepresentative].Id;
        string assignedUserId = DemoDataManifest.UsersByRole[DemoRoleNames.FieldTechnician].Id;
        string actorUserId = DemoDataManifest.UsersByRole[DemoRoleNames.SystemAdministrator].Id;

        Party party = Party.CreateOrganization("Fictional Overpost Customer");
        party.AddRole(PartyRoleType.Customer);
        party.AssignToBranch(branch);
        party.AddSite(branch, "Fictional Overpost Site");
        Site site = party.Sites.Single();

        SalesOpportunity sales = SalesOpportunity.Create(branch, party, site);
        sales.AssignOwner(ownerUserId);
        sales.AssignToUser(assignedUserId);

        (SalesOpportunity wonOpportunity, WorkOrder workOrder) = TestWorkOrderFactory.CreateFromWon(branch, party, site);
        wonOpportunity.AssignOwner(ownerUserId);
        wonOpportunity.AssignToUser(assignedUserId);
        workOrder.AssignToUser(assignedUserId);
        db.AddRange(party, sales, wonOpportunity, workOrder);
        await db.SaveChangesAsync();

        return new(
            party.Id,
            sales.Id,
            workOrder.Id,
            branch.Id,
            otherBranch.Id,
            ownerUserId,
            assignedUserId,
            actorUserId);
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

    private static async Task LoginAsAsync(HttpClient client, string role)
    {
        string html = await client.GetStringAsync("/demo-login");
        using HttpResponseMessage response = await client.PostAsync(
            "/demo-login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = Regex.Match(
                    html,
                    $"data-role=\"{Regex.Escape(role)}\".*?name=\"roleToken\" value=\"([^\"]+)\"",
                    RegexOptions.Singleline).Groups[1].Value,
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

    private sealed class RequestOutcomeLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<RequestOutcomeLog> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        public RequestOutcomeLog Single(string correlationId) => Entries.Single(entry => entry.CorrelationId == correlationId);

        private sealed class Logger(RequestOutcomeLoggerProvider provider, string category) : ILogger
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
                    values.TryGetValue("Route", out object? route) &&
                    values.TryGetValue("StatusCode", out object? statusCode) &&
                    values.TryGetValue("Outcome", out object? outcome))
                {
                    provider.Entries.Enqueue(new(
                        correlationId?.ToString() ?? string.Empty,
                        route?.ToString() ?? string.Empty,
                        Convert.ToInt32(statusCode, System.Globalization.CultureInfo.InvariantCulture),
                        outcome?.ToString() ?? string.Empty,
                        formatter(state, exception)));
                }
            }
        }
    }

    private sealed record RequestOutcomeLog(
        string CorrelationId,
        string Route,
        int StatusCode,
        string Outcome,
        string Message);

    private sealed record OverpostSeed(
        Guid PartyId,
        Guid SalesOpportunityId,
        Guid WorkOrderId,
        Guid BranchId,
        Guid OtherBranchId,
        string OwnerUserId,
        string AssignedUserId,
        string ActorUserId);
}