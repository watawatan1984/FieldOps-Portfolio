using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace FieldOps.IntegrationTests.Features;

[Collection(DatabaseCollection.Name)]
public sealed class WorkHistorySearchTests(PostgresFixture postgres)
{
    [Fact]
    public async Task DatabaseCleanupProbeUsesOneAbsoluteDeadline()
    {
        TaskCompletionSource cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Stopwatch elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TestDatabaseLease.RunWithAbsoluteDeadlineAsync(
                async cancellationToken =>
                {
                    Assert.True(cancellationToken.CanBeCanceled);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved.TrySetResult();
                        throw;
                    }
                },
                TimeSpan.FromMilliseconds(100)));

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EmptyCriteriaReturnsAllWorkInTheAuthorizedBranch()
    {
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        string connectionString = database.ConnectionString;
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        SearchSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        using HttpResponseMessage response = await client.GetAsync($"/work-history?branchId={seed.BranchId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Fictional Sakura Facilities", html);
        Assert.Contains("Fictional Ume Services", html);
        Assert.DoesNotContain("Fictional Foreign Customer", html);
    }

    [Fact]
    public async Task KeywordWithNoMatchesReturnsTheEmptyState()
    {
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        string connectionString = database.ConnectionString;
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        SearchSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        using HttpResponseMessage response = await client.GetAsync(
            $"/work-history?branchId={seed.BranchId}&keyword=definitely-not-present");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("No work history matches these filters.", html);
        Assert.Empty(GetResultsTableBody(html));
    }

    [Fact]
    public async Task MultipleCriteriaComposeAndMatchJapaneseText()
    {
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        string connectionString = database.ConnectionString;
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        SearchSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        string path = "/work-history?" + string.Join('&', new[]
        {
            $"branchId={seed.BranchId}",
            $"customerId={seed.SakuraPartyId}",
            $"businessPartnerId={seed.BusinessPartnerId}",
            $"siteId={seed.SakuraSiteId}",
            $"workStatus={WorkOrderStatus.Completed}",
            $"eventType={WorkEventType.Arrival}",
            $"technicianId={Uri.EscapeDataString(seed.TechnicianId)}",
            "scheduledFrom=2026-08-20",
            "scheduledTo=2026-08-20",
            "completedFrom=2026-08-31",
            "completedTo=2026-08-31",
            $"keyword={Uri.EscapeDataString("　設備　点検　")}"
        });

        using HttpResponseMessage response = await client.GetAsync(path);
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string resultRows = GetResultsTableBody(html);
        Assert.Contains("Fictional Sakura Facilities", resultRows);
        Assert.DoesNotContain("Fictional Ume Services", resultRows);
        Assert.Contains("1 result(s)", html);
    }

    [Fact]
    public async Task KeywordNormalizationMatchesEveryPersistedSearchSurface()
    {
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        await using FieldOpsWebApplicationFactory application = new(database.ConnectionString);
        using HttpClient client = CreateClient(application);
        SearchSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        string[] equivalentKeywords =
        [
            "フィールド Café",
            "ガス設備",
            "設備 点検 Café"
        ];

        foreach (string keyword in equivalentKeywords)
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"/work-history?branchId={seed.BranchId}&keyword={Uri.EscapeDataString(keyword)}");
            string html = await response.Content.ReadAsStringAsync();
            string resultRows = GetResultsTableBody(html);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Fictional Sakura Facilities", resultRows);
            Assert.DoesNotContain("Fictional Ume Services", resultRows);
        }
    }

    [Fact]
    public async Task EachConflictingCriterionCanExcludeAnOtherwiseMatchingKeyword()
    {
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        string connectionString = database.ConnectionString;
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        SearchSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        string common = $"/work-history?branchId={seed.BranchId}&keyword={Uri.EscapeDataString("設備 点検")}";
        string[] conflicts =
        [
            $"customerId={seed.UmePartyId}",
            $"businessPartnerId={seed.UnrelatedBusinessPartnerId}",
            $"siteId={seed.UmeSiteId}",
            $"workStatus={WorkOrderStatus.Planned}",
            $"eventType={WorkEventType.Correction}",
            $"technicianId={Uri.EscapeDataString("tampered-technician-id")}",
            "scheduledFrom=2026-08-21&scheduledTo=2026-08-21",
            "completedFrom=2026-09-01&completedTo=2026-09-01"
        ];

        foreach (string conflict in conflicts)
        {
            using HttpResponseMessage response = await client.GetAsync($"{common}&{conflict}");
            string html = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("No work history matches these filters.", html);
            Assert.Empty(GetResultsTableBody(html));
        }
    }

    [Fact]
    public async Task PagingIsStableAndLinksPreserveFilters()
    {
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        string connectionString = database.ConnectionString;
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        SearchSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        string path = $"/work-history?branchId={seed.BranchId}&keyword=Fictional&pageSize=1&page=1";
        string firstHtml = await client.GetStringAsync(path);
        string secondHtml = await client.GetStringAsync(path.Replace("page=1", "page=2", StringComparison.Ordinal));
        string repeatedFirstHtml = await client.GetStringAsync(path);

        string firstId = Regex.Match(firstHtml, "/work-orders/([0-9a-f-]{36})", RegexOptions.IgnoreCase).Groups[1].Value;
        string secondId = Regex.Match(secondHtml, "/work-orders/([0-9a-f-]{36})", RegexOptions.IgnoreCase).Groups[1].Value;
        string repeatedFirstId = Regex.Match(repeatedFirstHtml, "/work-orders/([0-9a-f-]{36})", RegexOptions.IgnoreCase).Groups[1].Value;
        Assert.NotEmpty(firstId);
        Assert.NotEmpty(secondId);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(firstId, repeatedFirstId);
        Assert.Contains("keyword=Fictional", firstHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pageSize=1", firstHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("page=2", firstHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PageSizeIsBoundedAtOneHundred()
    {
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        string connectionString = database.ConnectionString;
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        SearchSeed seed = await SeedAsync(application);
        await AddManyAuthorizedWorkOrdersAsync(application, seed.BranchId, 103);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        string html = await client.GetStringAsync(
            $"/work-history?branchId={seed.BranchId}&keyword=Fictional&pageSize=1000");

        Assert.Equal(100, Regex.Matches(html, "/work-orders/[0-9a-f-]{36}", RegexOptions.IgnoreCase).Count);
        Assert.Matches(
            new Regex("<input(?=[^>]*name=\"PageSize\")(?=[^>]*value=\"100\")[^>]*>", RegexOptions.IgnoreCase),
            html);
    }

    [Fact]
    public async Task DirectForeignBranchUrlIsForbiddenAndInvalidCriteriaAreRejected()
    {
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        string connectionString = database.ConnectionString;
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        SearchSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        using HttpResponseMessage foreignBranch = await client.GetAsync($"/work-history?branchId={seed.ForeignBranchId}");
        using HttpResponseMessage reversedDates = await client.GetAsync(
            $"/work-history?branchId={seed.BranchId}&scheduledFrom=2026-08-21&scheduledTo=2026-08-20");
        using HttpResponseMessage invalidEventType = await client.GetAsync(
            $"/work-history?branchId={seed.BranchId}&eventType=999");

        Assert.Equal(HttpStatusCode.Forbidden, foreignBranch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reversedDates.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidEventType.StatusCode);
    }

    [Fact]
    public async Task MalformedBranchIdIsRejectedBeforeBranchDefaulting()
    {
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        CapturingLoggerProvider loggerProvider = new();
        await using FieldOpsWebApplicationFactory application = new(
            database.ConnectionString,
            configureLogging: logging => logging.AddProvider(loggerProvider));
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        using HttpResponseMessage response = await client.GetAsync("/work-history?branchId=not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain(loggerProvider.Messages, message =>
            message.Contains("Work history search completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FieldTechnicianCannotExpandAssignedWorkScopeByQueryTampering()
    {
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        await using FieldOpsWebApplicationFactory application = new(database.ConnectionString);
        using HttpClient client = CreateClient(application);
        SearchSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.FieldTechnician);

        string html = await client.GetStringAsync($"/work-history?branchId={seed.BranchId}");
        using HttpResponseMessage technicianTamper = await client.GetAsync(
            $"/work-history?branchId={seed.BranchId}&technicianId={Uri.EscapeDataString(seed.ManagerId)}");
        string technicianTamperHtml = await technicianTamper.Content.ReadAsStringAsync();
        using HttpResponseMessage branchTamper = await client.GetAsync(
            $"/work-history?branchId={seed.ForeignBranchId}&technicianId={Uri.EscapeDataString(seed.ManagerId)}");

        string resultRows = GetResultsTableBody(html);
        Assert.Contains("Fictional Sakura Facilities", resultRows);
        Assert.DoesNotContain("Fictional Ume Services", resultRows);
        Assert.Equal(HttpStatusCode.OK, technicianTamper.StatusCode);
        Assert.Empty(GetResultsTableBody(technicianTamperHtml));
        Assert.Equal(HttpStatusCode.Forbidden, branchTamper.StatusCode);
    }

    [Theory]
    [InlineData("%", "Literal Percent % Customer")]
    [InlineData("_", "Literal Underscore _ Customer")]
    [InlineData("\\", "Literal Backslash \\ Customer")]
    public async Task KeywordTreatsLikeMetacharactersAsLiterals(string keyword, string expectedCustomer)
    {
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        await using FieldOpsWebApplicationFactory application = new(database.ConnectionString);
        using HttpClient client = CreateClient(application);
        SearchSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        string html = await client.GetStringAsync(
            $"/work-history?branchId={seed.BranchId}&keyword={Uri.EscapeDataString(keyword)}");
        string resultRows = GetResultsTableBody(html);

        Assert.Contains(expectedCustomer, resultRows);
        Assert.Single(Regex.Matches(resultRows, "/work-orders/[0-9a-f-]{36}", RegexOptions.IgnoreCase).Cast<Match>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2147483648")]
    [InlineData("2147483647")]
    public async Task ExtremePageValuesAreRejectedWithoutServerErrors(string page)
    {
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        await using FieldOpsWebApplicationFactory application = new(database.ConnectionString);
        using HttpClient client = CreateClient(application);
        SearchSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        using HttpResponseMessage response = await client.GetAsync(
            $"/work-history?branchId={seed.BranchId}&page={page}&pageSize=100");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SearchCriteriaAreNeverWrittenToLogs()
    {
        const string rawKeyword = "極秘_設備%検索";
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        string connectionString = database.ConnectionString;
        CapturingLoggerProvider loggerProvider = new();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configureLogging: logging => logging.AddProvider(loggerProvider));
        using HttpClient client = CreateClient(application);
        SearchSeed seed = await SeedAsync(application);
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        string path = "/work-history?" + string.Join('&', new[]
        {
            $"branchId={seed.BranchId}",
            $"customerId={seed.SakuraPartyId}",
            $"businessPartnerId={seed.BusinessPartnerId}",
            $"siteId={seed.SakuraSiteId}",
            $"technicianId={Uri.EscapeDataString(seed.TechnicianId)}",
            $"keyword={Uri.EscapeDataString(rawKeyword)}"
        });
        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(loggerProvider.Messages, message =>
            message.Contains("Work history search completed", StringComparison.Ordinal));
        string[] sensitiveValues =
        [
            rawKeyword,
            seed.BranchId.ToString(),
            seed.SakuraPartyId.ToString(),
            seed.BusinessPartnerId.ToString(),
            seed.SakuraSiteId.ToString(),
            seed.TechnicianId
        ];
        foreach (string sensitiveValue in sensitiveValues)
        {
            Assert.DoesNotContain(loggerProvider.Messages, message =>
                message.Contains(sensitiveValue, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task TenThousandRowBranchDateExplainUsesTheWorkHistoryIndex()
    {
        string evidencePath = Path.Combine(FindRepositoryRoot(), "docs", "evidence", "work-history-explain.json");
        byte[] evidenceBefore = await File.ReadAllBytesAsync(evidencePath);
        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        string connectionString = database.ConnectionString;
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SearchSeed seed = await SeedAsync(application);
        ExplainCapture capture = await CaptureExplainAsync(connectionString, seed);

        Assert.DoesNotContain(capture.Nodes, node =>
            node.NodeType == "Seq Scan" && node.RelationName == "WorkOrders");
        Assert.Contains(capture.Nodes, node =>
            node.IndexName == "IX_WorkOrders_BranchId_ScheduledStartUtc_Id");

        Assert.Equal(evidenceBefore, await File.ReadAllBytesAsync(evidencePath));
    }

    [Fact]
    public void CommittedExplainEvidenceIsStableSanitizedAndRejectsSequentialScan()
    {
        string evidencePath = Path.Combine(FindRepositoryRoot(), "docs", "evidence", "work-history-explain.json");
        string evidenceJson = File.ReadAllText(evidencePath);
        using JsonDocument evidence = JsonDocument.Parse(evidenceJson);
        JsonElement root = evidence.RootElement;

        Assert.Equal(1, root.GetProperty("evidenceSchemaVersion").GetInt32());
        Assert.Equal(10000, root.GetProperty("dataset").GetProperty("workOrders").GetInt32());
        Assert.Equal("ANALYZE, BUFFERS, FORMAT JSON", root.GetProperty("explainOptions").GetString());
        JsonElement rootPlan = root.GetProperty("explainOutput")[0].GetProperty("Plan");
        List<(string NodeType, string? RelationName, string? IndexName)> nodes = [];
        CollectPlanNodes(rootPlan, nodes);
        Assert.DoesNotContain(nodes, node =>
            node.NodeType == "Seq Scan" && node.RelationName == "WorkOrders");
        Assert.Contains(nodes, node =>
            node.IndexName == "IX_WorkOrders_BranchId_ScheduledStartUtc_Id");
        Assert.DoesNotMatch(
            "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            evidenceJson);
    }

    [Fact]
    public async Task ExplicitWorkHistoryExplainEvidenceRefresh()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("FIELDOPS_REFRESH_WORK_HISTORY_EXPLAIN"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        await using TestDatabaseLease database = await CreateDatabaseLeaseAsync();
        await using FieldOpsWebApplicationFactory application = new(database.ConnectionString);
        SearchSeed seed = await SeedAsync(application);
        ExplainCapture capture = await CaptureExplainAsync(database.ConnectionString, seed);
        Assert.DoesNotContain(capture.Nodes, node =>
            node.NodeType == "Seq Scan" && node.RelationName == "WorkOrders");
        JsonObject evidence = new()
        {
            ["evidenceSchemaVersion"] = 1,
            ["postgresImage"] = "postgres:17-alpine",
            ["dataset"] = new JsonObject
            {
                ["workOrders"] = 10000,
                ["branchDistribution"] = "alternating"
            },
            ["query"] = "branch-plus-scheduled-date, ordered and limited to 100",
            ["explainOptions"] = "ANALYZE, BUFFERS, FORMAT JSON",
            ["explainOutput"] = SanitizeExplain(capture.RawExplainJson)
        };
        string evidencePath = Path.Combine(FindRepositoryRoot(), "docs", "evidence", "work-history-explain.json");
        await File.WriteAllTextAsync(
            evidencePath,
            evidence.ToJsonString(new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true
            }) + Environment.NewLine);
    }

    private static async Task<SearchSeed> SeedAsync(FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        ApplicationUser manager = await dbContext.Users.SingleAsync(user => user.UserName == "branch.manager@fieldops.demo");
        ApplicationUser technician = await dbContext.Users.SingleAsync(user => user.UserName == "field.tech@fieldops.demo");
        Branch authorizedBranch = await dbContext.Branches.SingleAsync(branch => branch.Id == manager.BranchId);
        Branch foreignBranch = await dbContext.Branches.SingleAsync(branch => branch.Id != authorizedBranch.Id);
        technician.BranchId = authorizedBranch.Id;

        Party businessPartner = Party.CreateOrganization("Fictional Sakura Delivery Partner");
        businessPartner.AddRole(PartyRoleType.BusinessPartner);
        businessPartner.AssignToBranch(authorizedBranch);
        Party unrelatedBusinessPartner = Party.CreateOrganization("Fictional Unrelated Delivery Partner");
        unrelatedBusinessPartner.AddRole(PartyRoleType.BusinessPartner);
        unrelatedBusinessPartner.AssignToBranch(authorizedBranch);

        (Party sakuraParty, Site sakuraSite, WorkOrder sakuraWork) = AddWorkOrder(
            dbContext,
            authorizedBranch,
            "Fictional Sakura Facilities ﾌｨｰﾙﾄﾞ Cafe\u0301",
            "Tokyo Sakura Site ｶﾞｽ設備");
        sakuraWork.AssignToUser(technician.Id);
        sakuraWork.Schedule(
            new DateTime(2026, 8, 20, 23, 59, 59, DateTimeKind.Utc),
            new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc));
        sakuraWork.MoveTo(WorkOrderStatus.InProgress, new DateTime(2026, 8, 20, 23, 0, 0, DateTimeKind.Utc));
        sakuraWork.AddEvent(
            WorkEventType.Arrival,
            new DateTime(2026, 8, 20, 23, 30, 0, DateTimeKind.Utc),
            "設備 点検のため現場に到着",
            technician.Id);
        sakuraWork.AddEvent(
            WorkEventType.Completion,
            new DateTime(2026, 8, 31, 23, 59, 59, DateTimeKind.Utc),
            "設備 点検 Cafe\u0301 を完了",
            technician.Id);
        sakuraWork.MoveTo(WorkOrderStatus.Completed, new DateTime(2026, 8, 31, 23, 59, 59, DateTimeKind.Utc));
        sakuraWork.AssignBusinessPartner(businessPartner);
        (Party umeParty, Site umeSite, _) = AddWorkOrder(
            dbContext,
            authorizedBranch,
            "Fictional Ume Services",
            "Tokyo Ume Site");
        AddWorkOrder(dbContext, foreignBranch, "Fictional Foreign Customer", "Remote Site");
        AddWorkOrder(dbContext, authorizedBranch, "Literal Percent % Customer", "Literal Percent Site");
        AddWorkOrder(dbContext, authorizedBranch, "Literal Underscore _ Customer", "Literal Underscore Site");
        AddWorkOrder(dbContext, authorizedBranch, "Literal Backslash \\ Customer", "Literal Backslash Site");
        dbContext.AddRange(businessPartner, unrelatedBusinessPartner);
        await dbContext.SaveChangesAsync();
        return new SearchSeed(
            authorizedBranch.Id,
            foreignBranch.Id,
            sakuraParty.Id,
            umeParty.Id,
            sakuraSite.Id,
            umeSite.Id,
            sakuraWork.Id,
            businessPartner.Id,
            unrelatedBusinessPartner.Id,
            technician.Id,
            manager.Id);
    }

    private async Task<TestDatabaseLease> CreateDatabaseLeaseAsync() =>
        new(await postgres.CreateEmptyDatabaseAsync());

    private static (Party Party, Site Site, WorkOrder WorkOrder) AddWorkOrder(
        FieldOpsDbContext dbContext,
        Branch branch,
        string partyName,
        string siteName)
    {
        Party party = Party.CreateOrganization(partyName);
        party.AddRole(PartyRoleType.Customer);
        party.AssignToBranch(branch);
        party.AddSite(branch, siteName);
        (SalesOpportunity opportunity, WorkOrder workOrder) = TestWorkOrderFactory.CreateFromWon(
            branch,
            party,
            party.Sites.Single());
        dbContext.AddRange(party, opportunity, workOrder);
        return (party, party.Sites.Single(), workOrder);
    }

    private static async Task AddManyAuthorizedWorkOrdersAsync(
        FieldOpsWebApplicationFactory application,
        Guid branchId,
        int count)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch branch = await dbContext.Branches.SingleAsync(item => item.Id == branchId);
        for (int index = 0; index < count; index++)
        {
            AddWorkOrder(
                dbContext,
                branch,
                $"Fictional Paging Customer {index:D3}",
                $"Paging Site {index:D3}");
        }
        await dbContext.SaveChangesAsync();
    }

    private static async Task<ExplainCapture> CaptureExplainAsync(
        string connectionString,
        SearchSeed seed)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using (NpgsqlCommand insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO "WorkOrders"
                    ("Id", "BranchId", "PartyId", "SiteId", "SalesOpportunityId", "AssignedUserId",
                     "Status", "ScheduledStartUtc", "CreatedAtUtc", "UpdatedAtUtc")
                SELECT gen_random_uuid(),
                       CASE WHEN number % 2 = 0 THEN @branchId ELSE @foreignBranchId END,
                       @partyId,
                       @siteId,
                       NULL,
                       NULL,
                       @status,
                       TIMESTAMPTZ '2026-01-01 00:00:00+00' + (number % 365) * INTERVAL '1 day',
                       TIMESTAMPTZ '2026-01-01 00:00:00+00',
                       TIMESTAMPTZ '2026-01-01 00:00:00+00'
                FROM generate_series(1, 10000) AS number
                """;
            insert.Parameters.AddWithValue("branchId", seed.BranchId);
            insert.Parameters.AddWithValue("foreignBranchId", seed.ForeignBranchId);
            insert.Parameters.AddWithValue("partyId", seed.SakuraPartyId);
            insert.Parameters.AddWithValue("siteId", seed.SakuraSiteId);
            insert.Parameters.AddWithValue("status", (int)WorkOrderStatus.Scheduled);
            Assert.Equal(10000, await insert.ExecuteNonQueryAsync());
        }

        await using (NpgsqlCommand analyze = new("ANALYZE \"WorkOrders\"", connection))
        {
            await analyze.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand explain = connection.CreateCommand();
        explain.CommandText = """
            EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
            SELECT "Id", "ScheduledStartUtc"
            FROM "WorkOrders"
            WHERE "BranchId" = @branchId
              AND "ScheduledStartUtc" >= TIMESTAMPTZ '2026-08-01 00:00:00+00'
              AND "ScheduledStartUtc" < TIMESTAMPTZ '2026-09-01 00:00:00+00'
            ORDER BY "ScheduledStartUtc" DESC, "Id"
            LIMIT 100
            """;
        explain.Parameters.AddWithValue("branchId", seed.BranchId);
        object value = await explain.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL did not return an EXPLAIN plan.");
        using JsonDocument planDocument = JsonDocument.Parse(
            value.ToString() ?? throw new InvalidOperationException("PostgreSQL returned an empty EXPLAIN plan."));
        JsonElement rootPlan = planDocument.RootElement[0].GetProperty("Plan");
        List<(string NodeType, string? RelationName, string? IndexName)> nodes = [];
        CollectPlanNodes(rootPlan, nodes);
        return new ExplainCapture(nodes, value.ToString()!);
    }

    private static JsonNode SanitizeExplain(string explainJson)
    {
        JsonNode root = JsonNode.Parse(explainJson)
            ?? throw new InvalidOperationException("PostgreSQL returned invalid EXPLAIN JSON.");
        SanitizeNode(root);
        return root;
    }

    private static void SanitizeNode(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (child is not null)
                {
                    SanitizeNode(child);
                }
            }
            return;
        }
        if (node is not JsonObject item)
        {
            return;
        }

        foreach (string propertyName in item.Select(property => property.Key).ToArray())
        {
            JsonNode? value = item[propertyName];
            if (value is null)
            {
                continue;
            }
            if (IsVolatileExplainMetric(propertyName))
            {
                item[propertyName] = "<sanitized-runtime-value>";
            }
            else if (value is JsonValue scalar && scalar.TryGetValue(out string? textValue))
            {
                item[propertyName] = Regex.Replace(
                    textValue,
                    "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
                    "<sanitized-guid>",
                    RegexOptions.IgnoreCase);
            }
            else
            {
                SanitizeNode(value);
            }
        }
    }

    private static bool IsVolatileExplainMetric(string propertyName) =>
        propertyName is "Startup Cost" or "Total Cost" or "Plan Rows" or "Plan Width" or
            "Actual Startup Time" or "Actual Total Time" or "Heap Fetches" or
            "Planning Time" or "Execution Time" ||
        propertyName.EndsWith(" Blocks", StringComparison.Ordinal);

    private static async Task LoginAsAsync(HttpClient client, string role)
    {
        using HttpResponseMessage page = await client.GetAsync("/demo-login");
        string html = await page.Content.ReadAsStringAsync();
        string token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        string roleToken = Regex.Match(
            html,
            $"<h2 class=\"h5\">{Regex.Escape(role)}</h2>.*?name=\"roleToken\" value=\"([^\"]+)\"",
            RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(token);
        Assert.NotEmpty(roleToken);
        using HttpResponseMessage response = await client.PostAsync(
            "/demo-login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = roleToken,
                ["__RequestVerificationToken"] = token
            }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static string GetResultsTableBody(string html) =>
        Regex.Match(html, "<tbody>(.*?)</tbody>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value;

    private static void CollectPlanNodes(
        JsonElement node,
        ICollection<(string NodeType, string? RelationName, string? IndexName)> nodes)
    {
        nodes.Add((
            node.GetProperty("Node Type").GetString()!,
            node.TryGetProperty("Relation Name", out JsonElement relation) ? relation.GetString() : null,
            node.TryGetProperty("Index Name", out JsonElement index) ? index.GetString() : null));
        if (node.TryGetProperty("Plans", out JsonElement childPlans))
        {
            foreach (JsonElement child in childPlans.EnumerateArray())
            {
                CollectPlanNodes(child, nodes);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("The repository root could not be found.");
    }

    private static HttpClient CreateClient(FieldOpsWebApplicationFactory application) =>
        application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private sealed record SearchSeed(
        Guid BranchId,
        Guid ForeignBranchId,
        Guid SakuraPartyId,
        Guid UmePartyId,
        Guid SakuraSiteId,
        Guid UmeSiteId,
        Guid SakuraWorkOrderId,
        Guid BusinessPartnerId,
        Guid UnrelatedBusinessPartnerId,
        string TechnicianId,
        string ManagerId);

    private sealed record ExplainCapture(
        IReadOnlyList<(string NodeType, string? RelationName, string? IndexName)> Nodes,
        string RawExplainJson);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class TestDatabaseLease(string connectionString) : IAsyncDisposable
    {
        private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(5);

        public string ConnectionString { get; } = connectionString;

        public async ValueTask DisposeAsync()
        {
            using NpgsqlConnection connection = new(ConnectionString);
            NpgsqlConnection.ClearPool(connection);

            NpgsqlConnectionStringBuilder adminConnectionString = new(ConnectionString)
            {
                Database = "postgres",
                Pooling = false
            };
            List<string> remainingConnections = [];
            try
            {
                await RunWithAbsoluteDeadlineAsync(
                    async cancellationToken =>
                    {
                        await using NpgsqlConnection adminConnection = new(adminConnectionString.ConnectionString);
                        await adminConnection.OpenAsync(cancellationToken);
                        await using NpgsqlCommand inspectConnections = new(
                            """
                            SELECT pid, state, COALESCE(wait_event_type, ''), COALESCE(wait_event, ''), COALESCE(md5(query), '')
                            FROM pg_stat_activity
                            WHERE datname = @databaseName
                            ORDER BY pid
                            """,
                            adminConnection)
                        {
                            CommandTimeout = 5
                        };
                        inspectConnections.Parameters.AddWithValue(
                            "databaseName",
                            new NpgsqlConnectionStringBuilder(ConnectionString).Database!);

                        while (true)
                        {
                            remainingConnections.Clear();
                            await using (NpgsqlDataReader reader =
                                await inspectConnections.ExecuteReaderAsync(cancellationToken))
                            {
                                while (await reader.ReadAsync(cancellationToken))
                                {
                                    remainingConnections.Add(
                                        $"pid={reader.GetInt32(0)},state={reader.GetString(1)}," +
                                        $"wait={reader.GetString(2)}/{reader.GetString(3)},queryHash={reader.GetString(4)}");
                                }
                            }

                            if (remainingConnections.Count == 0)
                            {
                                return;
                            }

                            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                        }
                    },
                    CleanupDeadline);
            }
            catch (OperationCanceledException)
            {
                string diagnostics = remainingConnections.Count == 0
                    ? "no completed activity probe"
                    : string.Join(";", remainingConnections);
                throw new InvalidOperationException(
                    "Task 10 database cleanup probe exceeded its absolute 5-second deadline; " +
                    $"last safe activity diagnostics: {diagnostics}");
            }
        }

        public static async Task RunWithAbsoluteDeadlineAsync(
            Func<CancellationToken, Task> operation,
            TimeSpan deadline)
        {
            using CancellationTokenSource deadlineCancellation = new(deadline);
            CancellationToken cancellationToken = deadlineCancellation.Token;
            await operation(cancellationToken).WaitAsync(cancellationToken);
        }
    }
}