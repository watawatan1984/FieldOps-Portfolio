using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;

using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Features.Dashboard;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;
using FieldOps.Web.Models;
using FieldOps.Web.Services;

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
    private static readonly Guid KnownBranchId = Guid.Parse("00000000-0000-4000-8000-000000000001");

    [Theory]
    [InlineData(DemoRoleNames.BranchManager, "期限を過ぎた作業")]
    [InlineData(DemoRoleNames.SalesRepresentative, "期限が近い提案")]
    [InlineData(DemoRoleNames.FieldTechnician, "今日の作業")]
    public void FactoryPutsTheRoleSpecificActionFirst(string role, string expectedTitle)
    {
        DashboardMetrics metrics = new(5, 2, 3, 1, 4, 6, new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc));
        DashboardPageViewModel model = new DashboardPageModelFactory().Create(metrics, role, KnownBranchId);

        Assert.Equal(expectedTitle, model.Today.First().Title);
    }

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
        AssertMetric(html, "work-in-progress", 3);
        AssertMetric(html, "overdue-work", 2);
        AssertMetric(html, "completions-this-month", 2);
        Assert.Contains("2026年8月12日 21:00", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.Contains("Asia/Tokyo", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DashboardHomeShowsRoleSpecificTodayActionsAndKeepsBranchScope()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        await SeedMetricDefinitionsAsync(application);
        (string Role, string FirstAction, string RecommendedAction, string HiddenBranch)[] cases =
        [
            (DemoRoleNames.SystemAdministrator, "全体の遅延", "遅れている作業を確認する", "北部サービス支店"),
            (DemoRoleNames.BranchManager, "期限を過ぎた作業", "担当者と日程を確認する", "現場サービス支店"),
            (DemoRoleNames.SalesRepresentative, "期限が近い提案", "営業案件を確認する", "現場サービス支店"),
            (DemoRoleNames.FieldTechnician, "今日の作業", "作業予定を確認する", "中央サービス支店")
        ];

        foreach ((string role, string firstAction, string recommendedAction, string hiddenBranch) in cases)
        {
            using HttpClient client = CreateClient(application);
            await LoginAsAsync(client, role);

            string html = await client.GetStringAsync("/");
            string decodedHtml = WebUtility.HtmlDecode(html);

            Assert.Contains("今日やること", decodedHtml, StringComparison.Ordinal);
            Assert.Contains(firstAction, decodedHtml, StringComparison.Ordinal);
            Assert.Contains(recommendedAction, decodedHtml, StringComparison.Ordinal);
            Assert.Contains("確認が必要", decodedHtml, StringComparison.Ordinal);
            Assert.Contains("詳しい集計を見る", decodedHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(hiddenBranch, decodedHtml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DashboardCountsAreScopedToBranchOwnerAndAssigneeForEveryRole()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        await SeedMetricDefinitionsAsync(application);
        (string Role, int[] Metrics)[] cases =
        [
            (DemoRoleNames.SystemAdministrator, [5, 2, 2, 3, 2, 2]),
            (DemoRoleNames.BranchManager, [4, 2, 1, 2, 1, 1]),
            (DemoRoleNames.SalesRepresentative, [4, 2, 1, 2, 1, 1]),
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
        Assert.Contains("role=\"status\">該当なし。今は追加対応はいりません。</p>", html, StringComparison.Ordinal);
        Assert.Contains("role=\"status\">この範囲では、今すぐ対応が必要な項目はありません。</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivacyPageUsesJapaneseFictionalDataDisclosure()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        using HttpResponseMessage response = await client.GetAsync("/Home/Privacy");
        string html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("プライバシーについて", html, StringComparison.Ordinal);
        Assert.Contains("このデモは架空データだけを使用", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Privacy Policy", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Use this page", html, StringComparison.Ordinal);
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
        string decodedComparison = WebUtility.HtmlDecode(comparison);
        Assert.Equal(HttpStatusCode.OK, comparisonResponse.StatusCode);
        Assert.Contains("支店状況", decodedComparison, StringComparison.Ordinal);
        Assert.Contains("中央サービス支店", decodedComparison, StringComparison.Ordinal);
        Assert.Contains("現場サービス支店", decodedComparison, StringComparison.Ordinal);
        Assert.True(
            decodedComparison.IndexOf("遅延件数", StringComparison.Ordinal) <
            decodedComparison.IndexOf("未対応の営業案件", StringComparison.Ordinal));
        Assert.Contains($"data-branch-id=\"{seed.CentralBranchId}\" data-open-opportunities=\"4\"", comparison, StringComparison.Ordinal);
        Assert.Contains($"data-branch-id=\"{seed.FieldBranchId}\" data-open-opportunities=\"1\"", comparison, StringComparison.Ordinal);
        Assert.Contains("予定あり", decodedComparison, StringComparison.Ordinal);
        Assert.Contains("作業中", decodedComparison, StringComparison.Ordinal);
        Assert.Contains("遅延件数", decodedComparison, StringComparison.Ordinal);
        Assert.Contains("今月の完了", decodedComparison, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync($"/branches/{seed.FieldBranchId}")).StatusCode);
        string administratorDetails = await administrator.GetStringAsync($"/branches/{seed.CentralBranchId}");
        Assert.Contains("2026年8月12日 21:00", WebUtility.HtmlDecode(administratorDetails), StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", administratorDetails, StringComparison.Ordinal);

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
        string decodedAuditPages = WebUtility.HtmlDecode(firstPage + secondPage + boundedPage);

        string[] firstActions = ExtractAuditActions(firstPage);
        Assert.Equal(2, firstActions.Length);
        Assert.Equal(firstActions, ExtractAuditActions(repeatedFirstPage));
        Assert.DoesNotContain(firstActions[0], ExtractAuditActions(secondPage));
        Assert.DoesNotContain(firstActions[1], ExtractAuditActions(secondPage));
        Assert.Contains("data-page-size=\"100\"", boundedPage, StringComparison.Ordinal);
        Assert.Contains("鈴木 美咲", decodedAuditPages, StringComparison.Ordinal);
        Assert.DoesNotContain(audit.ManagerUserId, firstPage + secondPage + boundedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("ultra-secret-value", firstPage + secondPage + boundedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha123", boundedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_value", boundedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("秘密情報", boundedPage, StringComparison.Ordinal);
        Assert.Contains("詳細は非表示です", decodedAuditPages, StringComparison.Ordinal);
        Assert.DoesNotContain("Details withheld", firstPage + secondPage + boundedPage, StringComparison.Ordinal);
        Assert.Contains("変更履歴", decodedAuditPages, StringComparison.Ordinal);
        Assert.Contains("変更した項目", decodedAuditPages, StringComparison.Ordinal);
        Assert.Contains("営業担当者、状態", decodedAuditPages, StringComparison.Ordinal);
        Assert.Contains("data-audit-action=\"CentralAuditApprovedFields\"", boundedPage, StringComparison.Ordinal);
        Assert.Contains("2026年8月12日 19:00", WebUtility.HtmlDecode(boundedPage), StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", boundedPage, StringComparison.Ordinal);
        using HttpResponseMessage extremePage = await administrator.GetAsync(
            "/audit?page=999999999999999999999&pageSize=100");
        Assert.Equal(HttpStatusCode.BadRequest, extremePage.StatusCode);

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
    public async Task AuditFallbacksUseJapaneseDisplayTextForGlobalRowsMissingUsersAndWithheldDetails()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        await using (AsyncServiceScope scope = application.Services.CreateAsyncScope())
        {
            FieldOpsDbContext db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
            db.AuditEntries.Add(new AuditEntry(
                "Party",
                Guid.NewGuid(),
                null,
                "Updated",
                "Success",
                "raw-secret-field",
                new DateTime(2026, 8, 29, 1, 0, 0, DateTimeKind.Utc),
                "missing-demo-user"));
            await db.SaveChangesAsync();
        }

        using HttpClient administrator = CreateClient(application);
        await LoginAsAsync(administrator, DemoRoleNames.SystemAdministrator);

        string html = WebUtility.HtmlDecode(await administrator.GetStringAsync("/audit?pageSize=100"));

        Assert.Contains("全支店", html, StringComparison.Ordinal);
        Assert.Contains("未登録の利用者", html, StringComparison.Ordinal);
        Assert.Contains("詳細は非表示です", html, StringComparison.Ordinal);
        Assert.DoesNotContain("National", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Former demo user", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Details withheld", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntegratedShellRendersAuthorizedNavigationActiveMarkersOffcanvasAndExactHeaderLabels()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        (string Role, string Name, string Branch, string[] Visible, string[] Hidden, bool CanInitialize)[] cases =
        [
            (DemoRoleNames.SystemAdministrator, "佐藤 健一", "全支店", ["dashboard", "customers", "business-partners", "sales", "work-orders", "work-history", "branches", "audit"], [], true),
            (DemoRoleNames.BranchManager, "鈴木 美咲", "中央サービス支店", ["dashboard", "customers", "business-partners", "sales", "work-orders", "work-history", "branches", "audit"], [], false),
            (DemoRoleNames.SalesRepresentative, "高橋 翔太", "中央サービス支店", ["dashboard", "customers", "business-partners", "sales", "work-orders", "work-history"], ["branches", "audit"], false),
            (DemoRoleNames.FieldTechnician, "田中 葵", "現場サービス支店", ["dashboard", "sales", "work-orders", "work-history"], ["customers", "business-partners", "branches", "audit"], false)
        ];

        foreach (var item in cases)
        {
            using HttpClient client = CreateClient(application);
            await LoginAsAsync(client, item.Role);
            string dashboard = await client.GetStringAsync("/");
            string decodedDashboard = WebUtility.HtmlDecode(dashboard);

            Assert.Contains("class=\"offcanvas-lg offcanvas-start app-sidebar\"", dashboard, StringComparison.Ordinal);
            Assert.Contains("data-bs-toggle=\"offcanvas\"", dashboard, StringComparison.Ordinal);
            Assert.Contains("data-bs-target=\"#primaryNavigation\"", dashboard, StringComparison.Ordinal);
            Assert.Contains("aria-label=\"主なメニュー\"", dashboard, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(dashboard, "aria-current=\"page\"").Cast<Match>());
            Assert.Contains("data-nav=\"dashboard\" aria-current=\"page\"", dashboard, StringComparison.Ordinal);
            Assert.Contains($"data-user-name=\"{item.Name}\"", decodedDashboard, StringComparison.Ordinal);
            Assert.Contains($"data-user-role=\"{item.Role}\"", dashboard, StringComparison.Ordinal);
            Assert.Contains($"data-user-branch=\"{item.Branch}\"", decodedDashboard, StringComparison.Ordinal);
            Assert.Contains("架空のデモデータのみを使用しています", decodedDashboard, StringComparison.Ordinal);
            Assert.DoesNotContain("Fictional demonstration data only", dashboard, StringComparison.Ordinal);
            Assert.Equal(item.CanInitialize, dashboard.Contains(">初期化</a>", StringComparison.Ordinal));
            if (item.CanInitialize)
            {
                Assert.Contains("href=\"/administration/reset\"", dashboard, StringComparison.Ordinal);
                Assert.DoesNotContain("aria-disabled=\"true\"", dashboard, StringComparison.Ordinal);
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

            using (HttpResponseMessage response = await client.GetAsync("/"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            Assert.Equal(2, counter.CommandCount);
            Assert.InRange(counter.CommandCount, 1, 8);
            Assert.Equal(
                new DatabaseSessionCounts(0, 0, 0),
                await CountOtherDatabaseSessionsAsync(connectionString));

            for (int request = 0; request < 3; request++)
            {
                using HttpResponseMessage repeatedResponse = await client.GetAsync("/");
                Assert.Equal(HttpStatusCode.OK, repeatedResponse.StatusCode);
            }

            Assert.Equal(
                new DatabaseSessionCounts(0, 0, 0),
                await CountOtherDatabaseSessionsAsync(connectionString));
        }
        finally
        {
            await application.DisposeAsync();
        }

        Assert.Equal(
            new DatabaseSessionCounts(0, 0, 0),
            await CountOtherDatabaseSessionsAsync(connectionString));
    }

    [Fact]
    public async Task AuditPagingIndexesMatchStableGlobalAndBranchOrdering()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        _ = application.Services;

        IReadOnlyDictionary<string, string> indexes = await ReadAuditIndexesAsync(connectionString);

        Assert.Contains("IX_AuditEntries_OccurredAtUtc_Id", indexes.Keys);
        Assert.Contains(
            "\"OccurredAtUtc\" DESC, \"Id\" DESC",
            indexes["IX_AuditEntries_OccurredAtUtc_Id"],
            StringComparison.Ordinal);
        Assert.Contains("IX_AuditEntries_BranchId_OccurredAtUtc_Id", indexes.Keys);
        Assert.Contains(
            "\"BranchId\", \"OccurredAtUtc\" DESC, \"Id\" DESC",
            indexes["IX_AuditEntries_BranchId_OccurredAtUtc_Id"],
            StringComparison.Ordinal);
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

    [Fact]
    public async Task PartyNavigationHrefsResolveToFinalScopedPagesForEveryAuthorizedRole()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = CreateApplication(connectionString);
        await SeedNavigationPartiesAsync(application);

        foreach (string role in new[]
        {
            DemoRoleNames.SystemAdministrator,
            DemoRoleNames.BranchManager,
            DemoRoleNames.SalesRepresentative
        })
        {
            using HttpClient client = CreateClient(application);
            await LoginAsAsync(client, role);
            string dashboard = await client.GetStringAsync("/");

            string customerHref = ExtractNavigationHref(dashboard, "customers");
            (HttpStatusCode CustomerStatus, string CustomerHtml) = await GetFinalResponseAsync(client, customerHref);
            string decodedCustomerHtml = WebUtility.HtmlDecode(CustomerHtml);
            Assert.Equal(HttpStatusCode.OK, CustomerStatus);
            Assert.Contains("架空中央支店 顧客", decodedCustomerHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("架空現場支店 顧客", decodedCustomerHtml, StringComparison.Ordinal);

            string partnerHref = ExtractNavigationHref(dashboard, "business-partners");
            (HttpStatusCode PartnerStatus, string PartnerHtml) = await GetFinalResponseAsync(client, partnerHref);
            string decodedPartnerHtml = WebUtility.HtmlDecode(PartnerHtml);
            Assert.Equal(HttpStatusCode.OK, PartnerStatus);
            Assert.Contains("架空中央支店 協力会社", decodedPartnerHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("架空現場支店 協力会社", decodedPartnerHtml, StringComparison.Ordinal);
        }

        using HttpClient technician = CreateClient(application);
        await LoginAsAsync(technician, DemoRoleNames.FieldTechnician);
        string technicianDashboard = await technician.GetStringAsync("/");
        Assert.DoesNotContain("data-nav=\"customers\"", technicianDashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("data-nav=\"business-partners\"", technicianDashboard, StringComparison.Ordinal);
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
        Branch central = await db.Branches.SingleAsync(branch => branch.Name == "中央サービス支店");
        Branch field = await db.Branches.SingleAsync(branch => branch.Name == "現場サービス支店");
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
        dueTomorrow.AssignOwner(salesUser.Id);
        SalesOpportunity lost = SalesOpportunity.Create(central, centralParty, centralParty.Sites.Single());
        lost.MoveTo(SalesOpportunityStatus.Lost, FixedUtcNow.AddDays(-1));

        WorkOrder scheduledOverdue = CreateWork(db, central, centralParty);
        GetTrackedOpportunity(db, scheduledOverdue).AssignOwner(salesUser.Id);
        scheduledOverdue.Schedule(FixedUtcNow.AddHours(-1), FixedUtcNow.AddDays(-1));
        WorkOrder scheduledFuture = CreateWork(db, field, fieldParty);
        GetTrackedOpportunity(db, scheduledFuture).AssignOwner(salesUser.Id);
        scheduledFuture.Schedule(FixedUtcNow.AddHours(1), FixedUtcNow.AddDays(-1));
        scheduledFuture.AssignToUser(technician.Id);
        WorkOrder inProgressOverdue = CreateWork(db, field, fieldParty);
        inProgressOverdue.Schedule(FixedUtcNow.AddDays(-1), FixedUtcNow.AddDays(-2));
        inProgressOverdue.MoveTo(WorkOrderStatus.InProgress, FixedUtcNow.AddHours(-3));
        inProgressOverdue.AssignToUser(technician.Id);
        WorkOrder inProgressFuture = CreateWork(db, central, centralParty);
        inProgressFuture.Schedule(FixedUtcNow.AddHours(2), FixedUtcNow.AddDays(-1));
        inProgressFuture.MoveTo(WorkOrderStatus.InProgress, FixedUtcNow.AddHours(-1));
        WorkOrder inProgressWithCompletionEvent = CreateWork(db, central, centralParty);
        inProgressWithCompletionEvent.Schedule(FixedUtcNow.AddHours(3), FixedUtcNow.AddDays(-1));
        inProgressWithCompletionEvent.MoveTo(WorkOrderStatus.InProgress, FixedUtcNow.AddHours(-1));
        inProgressWithCompletionEvent.AddEvent(
            WorkEventType.Completion,
            FixedUtcNow.AddMinutes(-5),
            "Fictional completion event before final transition",
            "fictional.actor");
        WorkOrder completedAtMonthStart = CreateCompletedWork(db, central, centralParty, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        GetTrackedOpportunity(db, completedAtMonthStart).AssignOwner(salesUser.Id);
        WorkOrder completedNow = CreateCompletedWork(db, field, fieldParty, FixedUtcNow);
        GetTrackedOpportunity(db, completedNow).AssignOwner(salesUser.Id);
        completedNow.AssignToUser(technician.Id);
        WorkOrder completedBeforeMonth = CreateCompletedWork(db, central, centralParty, new DateTime(2026, 7, 31, 23, 59, 59, DateTimeKind.Utc));
        WorkOrder completedAtNextMonthStart = CreateCompletedWork(db, central, centralParty, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        db.AddRange(
            centralParty, fieldParty,
            openNew, onHold, duePast, dueToday, dueTomorrow, lost,
            scheduledOverdue, scheduledFuture, inProgressOverdue, inProgressFuture, inProgressWithCompletionEvent,
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
            new AuditEntry("SalesOpportunity", Guid.NewGuid(), seed.FieldBranchId, "FieldAuditTwo", "Success", "OwnerUserId", occurredAtUtc.AddMinutes(-3), manager.Id),
            new AuditEntry("Party", Guid.NewGuid(), seed.CentralBranchId, "CentralAuditIdentifierSecret", "Success", "Alpha123", occurredAtUtc.AddMinutes(-4), manager.Id),
            new AuditEntry("Party", Guid.NewGuid(), seed.CentralBranchId, "CentralAuditUnderscoreSecret", "Success", "secret_value", occurredAtUtc.AddMinutes(-5), manager.Id),
            new AuditEntry("Party", Guid.NewGuid(), seed.CentralBranchId, "CentralAuditUnicodeSecret", "Success", "秘密情報", occurredAtUtc.AddMinutes(-6), manager.Id),
            new AuditEntry("WorkOrder", Guid.NewGuid(), seed.CentralBranchId, "CentralAuditApprovedFields", "Success", "OwnerUserId,Status", occurredAtUtc.AddMinutes(-7), manager.Id));
        await db.SaveChangesAsync();
        return new AuditSeed(manager.Id);
    }

    private static async Task SeedNavigationPartiesAsync(FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch central = await db.Branches.SingleAsync(branch => branch.Name == "中央サービス支店");
        Branch field = await db.Branches.SingleAsync(branch => branch.Name == "現場サービス支店");
        Party centralCustomer = CreateParty(central, "架空中央支店 顧客");
        centralCustomer.AddRole(PartyRoleType.Customer);
        Party fieldCustomer = CreateParty(field, "架空現場支店 顧客");
        fieldCustomer.AddRole(PartyRoleType.Customer);
        Party centralPartner = CreateParty(central, "架空中央支店 協力会社");
        centralPartner.AddRole(PartyRoleType.BusinessPartner);
        Party fieldPartner = CreateParty(field, "架空現場支店 協力会社");
        fieldPartner.AddRole(PartyRoleType.BusinessPartner);
        db.Parties.AddRange(centralCustomer, fieldCustomer, centralPartner, fieldPartner);
        await db.SaveChangesAsync();
    }

    private static string ExtractNavigationHref(string html, string nav)
    {
        string href = Regex.Match(
            html,
            $"<a data-nav=\"{Regex.Escape(nav)}\"[^>]*href=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(href);
        return System.Net.WebUtility.HtmlDecode(href);
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

    private static async Task<(HttpStatusCode StatusCode, string Html)> GetFinalResponseAsync(
        HttpClient client,
        string path)
    {
        string currentPath = path;
        for (int redirect = 0; redirect < 5; redirect++)
        {
            using HttpResponseMessage response = await client.GetAsync(currentPath);
            if (response.StatusCode != HttpStatusCode.Redirect)
            {
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            Assert.NotNull(response.Headers.Location);
            currentPath = response.Headers.Location.OriginalString;
        }

        throw new InvalidOperationException("Navigation exceeded the redirect limit.");
    }

    private static async Task<DatabaseSessionCounts> CountOtherDatabaseSessionsAsync(string connectionString)
    {
        string nonPooledConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false
        }.ConnectionString;
        await using NpgsqlConnection connection = new(nonPooledConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FILTER (WHERE state = 'active'),
                   count(*) FILTER (WHERE state = 'idle in transaction'),
                   count(*)
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND pid <> pg_backend_pid()
            """;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DatabaseSessionCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2));
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadAuditIndexesAsync(string connectionString)
    {
        string nonPooledConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false
        }.ConnectionString;
        await using NpgsqlConnection connection = new(nonPooledConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'AuditEntries'
            """;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Dictionary<string, string> indexes = new(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0), reader.GetString(1));
        }

        return indexes;
    }

    private static async Task LoginAsAsync(HttpClient client, string role)
    {
        string html = await client.GetStringAsync("/demo-login");
        string requestToken = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        string roleToken = Regex.Match(
            html,
            $"data-role=\"{Regex.Escape(role)}\".*?name=\"roleToken\" value=\"([^\"]+)\"",
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

    private sealed record DatabaseSessionCounts(int Active, int IdleInTransaction, int Total);

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