using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;

using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

namespace FieldOps.IntegrationTests.Features;

[Collection(DatabaseCollection.Name)]
public sealed class DashboardTests(PostgresFixture postgres)
{
    private static readonly DateTime FixedUtcNow = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SystemAdministratorDashboardUsesExplicitUtcMetricBoundaries()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        await SeedMetricDefinitionsAsync(application);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);

        using HttpResponseMessage response = await client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertMetric(html, "open-opportunities", 5);
        AssertMetric(html, "proposals-due", 2);
        AssertMetric(html, "scheduled-work", 2);
        AssertMetric(html, "work-in-progress", 2);
        AssertMetric(html, "overdue-work", 2);
        AssertMetric(html, "completions-this-month", 2);
    }

    [Fact]
    public async Task DashboardCountsAreScopedToBranchOwnerAndAssigneeForEveryRole()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        await SeedMetricDefinitionsAsync(application);
        (string Role, int[] Metrics)[] cases =
        [
            (DemoRoleNames.SystemAdministrator, [5, 2, 2, 2, 2, 2]),
            (DemoRoleNames.BranchManager, [4, 2, 1, 1, 1, 1]),
            (DemoRoleNames.SalesRepresentative, [2, 1, 1, 0, 1, 1]),
            (DemoRoleNames.FieldTechnician, [1, 0, 1, 1, 1, 1])
        ];
        string[] metricNames =
        [
            "open-opportunities",
            "proposals-due",
            "scheduled-work",
            "work-in-progress",
            "overdue-work",
            "completions-this-month"
        ];

        foreach ((string role, int[] metrics) in cases)
        {
            using HttpClient client = CreateClient(application);
            await LoginAsAsync(client, role);
            string html = await client.GetStringAsync("/");

            for (int index = 0; index < metricNames.Length; index++)
            {
                AssertMetric(html, metricNames[index], metrics[index]);
            }
        }
    }

    [Fact]
    public async Task EmptyDashboardRendersZeroMetricsAndAnAccessibleEmptyState()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        string html = await client.GetStringAsync("/");

        Assert.Equal(6, Regex.Matches(html, "data-value=\"0\"").Count);
        Assert.Contains("role=\"status\">No dashboard activity is available in your current scope.</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NationalBranchComparisonIsAdministratorOnlyAndBranchDetailsEnforceDirectUrlScope()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        DashboardSeed seed = await SeedMetricDefinitionsAsync(application);

        using HttpClient administrator = CreateClient(application);
        await LoginAsAsync(administrator, DemoRoleNames.SystemAdministrator);
        using HttpResponseMessage comparisonResponse = await administrator.GetAsync("/branches");
        string comparison = await comparisonResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, comparisonResponse.StatusCode);
        Assert.Contains("National branch comparison", comparison, StringComparison.Ordinal);
        Assert.Contains("Fictional Central Service Branch", comparison, StringComparison.Ordinal);
        Assert.Contains("Fictional Field Service Branch", comparison, StringComparison.Ordinal);
        Assert.Contains($"data-branch-id=\"{seed.CentralBranchId}\" data-open-opportunities=\"4\"", comparison, StringComparison.Ordinal);
        Assert.Contains($"data-branch-id=\"{seed.FieldBranchId}\" data-open-opportunities=\"1\"", comparison, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync($"/branches/{seed.FieldBranchId}")).StatusCode);

        using HttpClient manager = CreateClient(application);
        await LoginAsAsync(manager, DemoRoleNames.BranchManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/branches")).StatusCode);
        string ownDetails = await manager.GetStringAsync($"/branches/{seed.CentralBranchId}");
        Assert.DoesNotContain("National branch comparison", ownDetails, StringComparison.Ordinal);
        AssertMetric(ownDetails, "open-opportunities", 4);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/branches/{seed.FieldBranchId}")).StatusCode);

        foreach (string role in new[] { DemoRoleNames.SalesRepresentative, DemoRoleNames.FieldTechnician })
        {
            using HttpClient client = CreateClient(application);
            await LoginAsAsync(client, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/branches/{seed.CentralBranchId}")).StatusCode);
        }
    }

    [Fact]
    public async Task AuditIndexIsScopedPagedStableAndDoesNotRenderIdentityIdsOrSensitiveValues()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        DashboardSeed seed = await SeedMetricDefinitionsAsync(application);
        AuditSeed audit = await SeedAuditEntriesAsync(application, seed);

        using HttpClient administrator = CreateClient(application);
        await LoginAsAsync(administrator, DemoRoleNames.SystemAdministrator);
        string firstPage = await administrator.GetStringAsync("/audit?page=1&pageSize=2");
        string repeatedFirstPage = await administrator.GetStringAsync("/audit?page=1&pageSize=2");
        string secondPage = await administrator.GetStringAsync("/audit?page=2&pageSize=2");
        string boundedPage = await administrator.GetStringAsync("/audit?page=1&pageSize=1000");

        string[] firstActions = ExtractAuditActions(firstPage);
        Assert.Equal(2, firstActions.Length);
        Assert.Equal(firstActions, ExtractAuditActions(repeatedFirstPage));
        Assert.DoesNotContain(firstActions[0], ExtractAuditActions(secondPage));
        Assert.DoesNotContain(firstActions[1], ExtractAuditActions(secondPage));
        Assert.Contains("data-page-size=\"100\"", boundedPage, StringComparison.Ordinal);
        Assert.Contains("Jordan Lee", firstPage + secondPage + boundedPage, StringComparison.Ordinal);
        Assert.DoesNotContain(audit.ManagerUserId, firstPage + secondPage + boundedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("ultra-secret-value", firstPage + secondPage + boundedPage, StringComparison.Ordinal);
        Assert.Contains("Details withheld", firstPage + secondPage + boundedPage, StringComparison.Ordinal);

        using HttpClient manager = CreateClient(application);
        await LoginAsAsync(manager, DemoRoleNames.BranchManager);
        string managerPage = await manager.GetStringAsync("/audit?pageSize=100");
        Assert.Contains("CentralAudit", managerPage, StringComparison.Ordinal);
        Assert.DoesNotContain("FieldAudit", managerPage, StringComparison.Ordinal);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await manager.GetAsync($"/audit?branchId={seed.FieldBranchId}")).StatusCode);

        foreach (string role in new[] { DemoRoleNames.SalesRepresentative, DemoRoleNames.FieldTechnician })
        {
            using HttpClient client = CreateClient(application);
            await LoginAsAsync(client, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/audit")).StatusCode);
        }
    }

    [Fact]
    public async Task IntegratedShellRendersAuthorizedNavigationActiveMarkersOffcanvasAndExactHeaderLabels()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        (string Role, string Name, string Branch, string[] Visible, string[] Hidden, bool CanInitialize)[] cases =
        [
            (DemoRoleNames.SystemAdministrator, "Alex Morgan", "National", ["dashboard", "customers", "business-partners", "sales", "work-orders", "work-history", "branches", "audit"], [], true),
            (DemoRoleNames.BranchManager, "Jordan Lee", "Fictional Central Service Branch", ["dashboard", "customers", "business-partners", "sales", "work-orders", "work-history", "branches", "audit"], [], false),
            (DemoRoleNames.SalesRepresentative, "Casey Rivera", "Fictional Central Service Branch", ["dashboard", "customers", "business-partners", "sales", "work-orders", "work-history"], ["branches", "audit"], false),
            (DemoRoleNames.FieldTechnician, "Taylor Kim", "Fictional Field Service Branch", ["dashboard", "sales", "work-orders", "work-history"], ["customers", "business-partners", "branches", "audit"], false)
        ];

        foreach (var item in cases)
        {
            using HttpClient client = CreateClient(application);
            await LoginAsAsync(client, item.Role);
            string dashboard = await client.GetStringAsync("/");

            Assert.Contains("class=\"offcanvas-lg offcanvas-start app-sidebar\"", dashboard, StringComparison.Ordinal);
            Assert.Contains("data-bs-toggle=\"offcanvas\"", dashboard, StringComparison.Ordinal);
            Assert.Contains("data-bs-target=\"#primaryNavigation\"", dashboard, StringComparison.Ordinal);
            Assert.Contains("aria-label=\"Primary navigation\"", dashboard, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(dashboard, "aria-current=\"page\"").Cast<Match>());
            Assert.Contains("data-nav=\"dashboard\" aria-current=\"page\"", dashboard, StringComparison.Ordinal);
            Assert.Contains($"data-user-name=\"{item.Name}\"", dashboard, StringComparison.Ordinal);
            Assert.Contains($"data-user-role=\"{item.Role}\"", dashboard, StringComparison.Ordinal);
            Assert.Contains($"data-user-branch=\"{item.Branch}\"", dashboard, StringComparison.Ordinal);
            Assert.Equal(item.CanInitialize, dashboard.Contains(">初期化</button>", StringComparison.Ordinal));
            if (item.CanInitialize)
            {
                Assert.Contains("type=\"button\" class=\"btn btn-outline-secondary btn-sm\" disabled", dashboard, StringComparison.Ordinal);
                Assert.DoesNotContain("href=\"/demo-reset\"", dashboard, StringComparison.Ordinal);
            }
            Assert.Contains("action=\"/demo-login/logout\"", dashboard, StringComparison.Ordinal);
            Assert.Contains("name=\"__RequestVerificationToken\"", dashboard, StringComparison.Ordinal);

            foreach (string nav in item.Visible)
            {
                Assert.Contains($"data-nav=\"{nav}\"", dashboard, StringComparison.Ordinal);
            }
            foreach (string nav in item.Hidden)
            {
                Assert.DoesNotContain($"data-nav=\"{nav}\"", dashboard, StringComparison.Ordinal);
            }

            string history = await GetFollowingSingleRedirectAsync(client, "/work-history");
            Assert.Contains("data-nav=\"work-history\" aria-current=\"page\"", history, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(history, "aria-current=\"page\"").Cast<Match>());
        }
    }

    [Fact]
    public async Task OneDashboardRequestExecutesNoMoreThanEightSqlCommandsAfterStartupNoiseIsReset()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        DashboardCommandCounter counter = new();
        FieldOpsWebApplicationFactory application = CreateApplication(connectionString, counter);
        try
        {
            await SeedMetricDefinitionsAsync(application);
            using HttpClient client = CreateClient(application);
            await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
            counter.Reset();

            using HttpResponseMessage response = await client.GetAsync("/");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, counter.CommandCount);
            Assert.InRange(counter.CommandCount, 1, 8);
        }
        finally
        {
            await application.DisposeAsync();
        }

        Assert.Equal(0, await CountOtherDatabaseConnectionsAsync(connectionString));
    }

    [Fact]
    public async Task ShellLogoutRequiresAntiforgeryAndEndsTheDemoSession()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
        string dashboard = await client.GetStringAsync("/");
        string requestToken = Regex.Match(
            dashboard,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(requestToken);

        using HttpResponseMessage missingToken = await client.PostAsync(
            "/demo-login/logout",
            new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);

        using HttpResponseMessage logout = await client.PostAsync(
            "/demo-login/logout",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = requestToken
            }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/demo-login", logout.Headers.Location?.OriginalString);
        using HttpResponseMessage dashboardAfterLogout = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, dashboardAfterLogout.StatusCode);
        Assert.Equal("/demo-login", dashboardAfterLogout.Headers.Location?.OriginalString);
    }

    private static FieldOpsWebApplicationFactory CreateApplication(
        string connectionString,
        DashboardCommandCounter? commandCounter = null)
    {
        string nonPooledConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false
        }.ConnectionString;
        return new(nonPooledConnectionString, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedUtcNow));
            if (commandCounter is not null)
            {
                services.AddSingleton(commandCounter);
                services.AddDbContext<FieldOpsDbContext>((_, options) => options.AddInterceptors(commandCounter));
            }
        });
    }

    private static HttpClient CreateClient(FieldOpsWebApplicationFactory application) =>
        application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private static void AssertMetric(string html, string metric, int expected) =>
        Assert.Contains($"data-metric=\"{metric}\" data-value=\"{expected}\"", html, StringComparison.Ordinal);

    private static async Task<DashboardSeed> SeedMetricDefinitionsAsync(FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch central = await db.Branches.SingleAsync(branch => branch.Name == "Fictional Central Service Branch");
        Branch field = await db.Branches.SingleAsync(branch => branch.Name == "Fictional Field Service Branch");
        ApplicationUser technician = await db.Users.SingleAsync(user => user.UserName == "field.tech@fieldops.demo");
        ApplicationUser salesUser = await db.Users.SingleAsync(user => user.UserName == "sales.rep@fieldops.demo");

        Party centralParty = CreateParty(central, "Fictional Dashboard Central Customer");
        Party fieldParty = CreateParty(field, "Fictional Dashboard Field Customer");

        SalesOpportunity openNew = SalesOpportunity.Create(central, centralParty, centralParty.Sites.Single());
        openNew.AssignOwner(salesUser.Id);
        SalesOpportunity onHold = SalesOpportunity.Create(central, centralParty, centralParty.Sites.Single());
        onHold.MoveTo(SalesOpportunityStatus.OnHold, FixedUtcNow.AddDays(-2));
        SalesOpportunity duePast = CreateProposed(central, centralParty, FixedUtcNow.Date.AddDays(-1));
        SalesOpportunity dueToday = CreateProposed(central, centralParty, FixedUtcNow.Date);
        dueToday.AssignOwner(salesUser.Id);
        SalesOpportunity dueTomorrow = CreateProposed(field, fieldParty, FixedUtcNow.Date.AddDays(1));
        dueTomorrow.AssignToUser(technician.Id);
        SalesOpportunity lost = SalesOpportunity.Create(central, centralParty, centralParty.Sites.Single());
        lost.MoveTo(SalesOpportunityStatus.Lost, FixedUtcNow.AddDays(-1));

        WorkOrder scheduledOverdue = CreateWork(db, central, centralParty);
        GetTrackedOpportunity(db, scheduledOverdue).AssignOwner(salesUser.Id);
        scheduledOverdue.Schedule(FixedUtcNow.AddHours(-1), FixedUtcNow.AddDays(-1));
        WorkOrder scheduledFuture = CreateWork(db, field, fieldParty);
        scheduledFuture.Schedule(FixedUtcNow.AddHours(1), FixedUtcNow.AddDays(-1));
        scheduledFuture.AssignToUser(technician.Id);
        WorkOrder inProgressOverdue = CreateWork(db, field, fieldParty);
        inProgressOverdue.Schedule(FixedUtcNow.AddDays(-1), FixedUtcNow.AddDays(-2));
        inProgressOverdue.MoveTo(WorkOrderStatus.InProgress, FixedUtcNow.AddHours(-3));
        inProgressOverdue.AssignToUser(technician.Id);
        WorkOrder inProgressFuture = CreateWork(db, central, centralParty);
        inProgressFuture.Schedule(FixedUtcNow.AddHours(2), FixedUtcNow.AddDays(-1));
        inProgressFuture.MoveTo(WorkOrderStatus.InProgress, FixedUtcNow.AddHours(-1));
        WorkOrder completedAtMonthStart = CreateCompletedWork(db, central, centralParty, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        GetTrackedOpportunity(db, completedAtMonthStart).AssignOwner(salesUser.Id);
        WorkOrder completedNow = CreateCompletedWork(db, field, fieldParty, FixedUtcNow);
        completedNow.AssignToUser(technician.Id);
        WorkOrder completedBeforeMonth = CreateCompletedWork(db, central, centralParty, new DateTime(2026, 7, 31, 23, 59, 59, DateTimeKind.Utc));
        WorkOrder completedAtNextMonthStart = CreateCompletedWork(db, central, centralParty, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        db.AddRange(
            centralParty, fieldParty,
            openNew, onHold, duePast, dueToday, dueTomorrow, lost,
            scheduledOverdue, scheduledFuture, inProgressOverdue, inProgressFuture,
            completedAtMonthStart, completedNow, completedBeforeMonth, completedAtNextMonthStart);
        await db.SaveChangesAsync();
        return new DashboardSeed(central.Id, field.Id);
    }

    private static Party CreateParty(Branch branch, string name)
    {
        Party party = Party.CreateOrganization(name);
        party.AssignToBranch(branch);
        party.AddSite(branch, $"{name} Site");
        return party;
    }

    private static SalesOpportunity CreateProposed(Branch branch, Party party, DateTime expectedCloseDate)
    {
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, party.Sites.Single());
        opportunity.MoveTo(SalesOpportunityStatus.Contacted, FixedUtcNow.AddDays(-5));
        opportunity.MoveTo(SalesOpportunityStatus.SurveyScheduled, FixedUtcNow.AddDays(-4));
        opportunity.MoveTo(SalesOpportunityStatus.Quoting, FixedUtcNow.AddDays(-3));
        opportunity.SetProposal(1000m, expectedCloseDate);
        opportunity.MoveTo(SalesOpportunityStatus.Proposed, FixedUtcNow.AddDays(-2));
        return opportunity;
    }

    private static WorkOrder CreateWork(FieldOpsDbContext db, Branch branch, Party party)
    {
        (SalesOpportunity opportunity, WorkOrder workOrder) =
            TestWorkOrderFactory.CreateFromWon(branch, party, party.Sites.Single());
        db.SalesOpportunities.Add(opportunity);
        return workOrder;
    }

    private static WorkOrder CreateCompletedWork(FieldOpsDbContext db, Branch branch, Party party, DateTime completionUtc)
    {
        WorkOrder workOrder = CreateWork(db, branch, party);
        workOrder.Schedule(completionUtc.AddHours(-2), completionUtc.AddHours(-3));
        workOrder.MoveTo(WorkOrderStatus.InProgress, completionUtc.AddHours(-1));
        workOrder.AddEvent(WorkEventType.Completion, completionUtc, "Fictional dashboard completion", "fictional.actor");
        workOrder.MoveTo(WorkOrderStatus.Completed, completionUtc);
        return workOrder;
    }

    private static SalesOpportunity GetTrackedOpportunity(FieldOpsDbContext db, WorkOrder workOrder) =>
        db.ChangeTracker.Entries<SalesOpportunity>()
            .Select(entry => entry.Entity)
            .Single(opportunity => opportunity.Id == workOrder.SalesOpportunityId);

    private static async Task<AuditSeed> SeedAuditEntriesAsync(
        FieldOpsWebApplicationFactory application,
        DashboardSeed seed)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        ApplicationUser manager = await db.Users.SingleAsync(user => user.UserName == "branch.manager@fieldops.demo");
        DateTime occurredAtUtc = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);
        db.AuditEntries.AddRange(
            new AuditEntry("Party", Guid.NewGuid(), seed.CentralBranchId, "CentralAuditOne", "Success", "Name", occurredAtUtc, manager.Id),
            new AuditEntry("Party", Guid.NewGuid(), seed.CentralBranchId, "CentralAuditTwo", "Success", "ultra-secret-value", occurredAtUtc, manager.Id),
            new AuditEntry("WorkOrder", Guid.NewGuid(), seed.CentralBranchId, "CentralAuditThree", "Success", "Status", occurredAtUtc.AddMinutes(-1), manager.Id),
            new AuditEntry("WorkOrder", Guid.NewGuid(), seed.FieldBranchId, "FieldAuditOne", "Success", "Status", occurredAtUtc.AddMinutes(-2), manager.Id),
            new AuditEntry("SalesOpportunity", Guid.NewGuid(), seed.FieldBranchId, "FieldAuditTwo", "Success", "OwnerUserId", occurredAtUtc.AddMinutes(-3), manager.Id));
        await db.SaveChangesAsync();
        return new AuditSeed(manager.Id);
    }

    private static string[] ExtractAuditActions(string html) =>
        Regex.Matches(html, "data-audit-action=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

    private static async Task<string> GetFollowingSingleRedirectAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path);
        if (response.StatusCode != HttpStatusCode.Redirect)
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        Assert.NotNull(response.Headers.Location);
        return await client.GetStringAsync(response.Headers.Location);
    }

    private static async Task<int> CountOtherDatabaseConnectionsAsync(string connectionString)
    {
        string nonPooledConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false
        }.ConnectionString;
        await using NpgsqlConnection connection = new(nonPooledConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*)
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND pid <> pg_backend_pid()
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task LoginAsAsync(HttpClient client, string role)
    {
        string html = await client.GetStringAsync("/demo-login");
        string requestToken = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
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

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed record DashboardSeed(Guid CentralBranchId, Guid FieldBranchId);

    private sealed record AuditSeed(string ManagerUserId);

    private sealed class DashboardCommandCounter : DbCommandInterceptor
    {
        private int _commandCount;

        public int CommandCount => Volatile.Read(ref _commandCount);

        public void Reset() => Interlocked.Exchange(ref _commandCount, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _commandCount);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commandCount);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _commandCount);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commandCount);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Interlocked.Increment(ref _commandCount);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commandCount);
            return ValueTask.FromResult(result);
        }
    }
}