using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FieldOps.IntegrationTests.Concurrency;

[Collection(DatabaseCollection.Name)]
public sealed class ConcurrentMutationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task UpdateDeletedAfterFormLoadReturns404WithoutAuditOrReplacementRow()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
        OpportunitySeed seed = await SeedOpportunityAsync(application);
        string token = await GetAntiforgeryTokenAsync(client, $"/sales/{seed.Id}/edit");

        await using (AsyncServiceScope scope = application.Services.CreateAsyncScope())
        {
            FieldOpsDbContext db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
            _ = await db.SalesOpportunities.Where(item => item.Id == seed.Id).ExecuteDeleteAsync();
        }

        using HttpResponseMessage response = await client.PostAsync(
            $"/sales/{seed.Id}/edit",
            EditForm(seed, seed.Version, token, 2222m));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using AsyncServiceScope verifyScope = application.Services.CreateAsyncScope();
        FieldOpsDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.False(await verifyDb.SalesOpportunities.AnyAsync(item => item.Id == seed.Id));
        Assert.False(await verifyDb.AuditEntries.AnyAsync(entry =>
            entry.AggregateType == nameof(SalesOpportunity) &&
            entry.AggregateId == seed.Id));
    }

    [Fact]
    public async Task TwentySynchronizedHttpUpdatesProduceOneWinnerNineteenConflictsAndTwentySafeOutcomes()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        MutationOutcomeLoggerProvider logs = new();
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configureLogging: logging => logging.AddProvider(logs));
        using HttpClient setupClient = CreateClient(application);
        await LoginAsAsync(setupClient, DemoRoleNames.SystemAdministrator);
        OpportunitySeed seed = await SeedOpportunityAsync(application);
        string token = await GetAntiforgeryTokenAsync(setupClient, $"/sales/{seed.Id}/edit");

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpStatusCode>[] requests = Enumerable.Range(1, 20).Select(async attempt =>
        {
            await start.Task;
            using HttpResponseMessage response = await setupClient.PostAsync(
                $"/sales/{seed.Id}/edit",
                EditForm(seed, seed.Version, token, 1000m + attempt));
            return response.StatusCode;
        }).ToArray();

        start.SetResult();
        HttpStatusCode[] outcomes = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, outcomes.Count(status => status == HttpStatusCode.Redirect));
        Assert.Equal(19, outcomes.Count(status => status == HttpStatusCode.Conflict));
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, outcomes);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        SalesOpportunity final = await db.SalesOpportunities.AsNoTracking().SingleAsync(item => item.Id == seed.Id);
        Assert.InRange(final.ProposedAmount!.Value, 1001m, 1020m);
        Assert.Equal(seed.ExpectedCloseDate, final.ExpectedCloseDate);
        Assert.NotEqual(seed.Version, final.Version);
        Assert.Equal(1, await db.AuditEntries.CountAsync(entry =>
            entry.AggregateType == nameof(SalesOpportunity) &&
            entry.AggregateId == seed.Id &&
            entry.Action == "Updated" &&
            entry.Outcome == "Success"));

        IReadOnlyList<MutationOutcome> mutationOutcomes = logs.Outcomes
            .Where(entry => entry.Operation == "sales-opportunity-update")
            .ToList();
        Assert.Equal(20, mutationOutcomes.Count);
        Assert.Equal(1, mutationOutcomes.Count(entry => entry.Outcome == "success"));
        Assert.Equal(19, mutationOutcomes.Count(entry => entry.Outcome == "conflict"));
        Assert.All(mutationOutcomes, entry =>
        {
            Assert.DoesNotContain(seed.OwnerUserId, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(seed.Id.ToString(), entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UnhandledException", entry.Message, StringComparison.Ordinal);
        });
    }

    private static async Task<OpportunitySeed> SeedOpportunityAsync(FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext db = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        ApplicationUser owner = await db.Users.SingleAsync(user => user.UserName == "sales.rep@fieldops.demo");
        Guid branchId = Assert.IsType<Guid>(owner.BranchId);
        Branch branch = await db.Branches.SingleAsync(item => item.Id == branchId);
        Party party = Party.CreateOrganization("Fictional Concurrent Opportunity Party");
        party.AddRole(PartyRoleType.Customer);
        party.AssignToBranch(branch);
        party.AddSite(branch, "Fictional Concurrent Opportunity Site");
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, party.Sites.Single());
        opportunity.AssignOwner(owner.Id);
        DateTime expectedClose = new(2026, 9, 30);
        opportunity.SetProposal(1000m, expectedClose);
        db.AddRange(party, opportunity);
        await db.SaveChangesAsync();
        return new(
            opportunity.Id,
            branchId,
            party.Id,
            party.Sites.Single().Id,
            owner.Id,
            opportunity.Version,
            expectedClose);
    }

    private static FormUrlEncodedContent EditForm(
        OpportunitySeed seed,
        uint version,
        string token,
        decimal amount) => new(new Dictionary<string, string>
        {
            ["Id"] = seed.Id.ToString(),
            ["BranchId"] = seed.BranchId.ToString(),
            ["PartyId"] = seed.PartyId.ToString(),
            ["SiteId"] = seed.SiteId.ToString(),
            ["OwnerUserId"] = seed.OwnerUserId,
            ["AssignedUserId"] = string.Empty,
            ["ProposedAmount"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ExpectedCloseDate"] = seed.ExpectedCloseDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["Version"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["__RequestVerificationToken"] = token
        });

    private static HttpClient CreateClient(FieldOpsWebApplicationFactory application) =>
        application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

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

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path) =>
        GetInputValue(await client.GetStringAsync(path), "__RequestVerificationToken");

    private static string GetInputValue(string html, string name)
    {
        string value = Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(value);
        return value;
    }

    private sealed record OpportunitySeed(
        Guid Id,
        Guid BranchId,
        Guid PartyId,
        Guid SiteId,
        string OwnerUserId,
        uint Version,
        DateTime ExpectedCloseDate);
}

internal sealed class MutationOutcomeLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<MutationOutcome> Outcomes { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(MutationOutcomeLoggerProvider provider, string category) : ILogger
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
            if (category != "FieldOps.Infrastructure.Persistence.MutationExecutor" ||
                state is not IEnumerable<KeyValuePair<string, object?>> properties)
            {
                return;
            }

            Dictionary<string, object?> values = properties.ToDictionary(item => item.Key, item => item.Value);
            if (values.TryGetValue("Operation", out object? operation) &&
                values.TryGetValue("Outcome", out object? outcome))
            {
                provider.Outcomes.Enqueue(new(operation?.ToString() ?? string.Empty, outcome?.ToString() ?? string.Empty, formatter(state, exception)));
            }
        }
    }
}

internal sealed record MutationOutcome(string Operation, string Outcome, string Message);