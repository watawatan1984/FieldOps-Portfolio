using System.Net;
using System.Text.RegularExpressions;

using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.IntegrationTests.Features;

[Collection(DatabaseCollection.Name)]
public sealed class QuoteFeatureTests(PostgresFixture postgres)
{
    private static readonly (string Description, string UnitName, decimal Quantity, decimal UnitPrice)[] DefaultLineItems =
    [
        ("Fictional Widget A", "個", 3m, 1500m),
        ("Fictional Widget B", "個", 1m, 333m)
    ];

    [Fact]
    public async Task SalesRepresentativeCreatesQuoteWithLineItemsAndFractionalTax()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        QuoteSeed seed = await SeedAsync(application);
        Guid opportunityId = await CreateOpportunityAsync(application, seed);

        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid quoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-01-01", DefaultLineItems);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Quote quote = await dbContext.Quotes.Include(item => item.LineItems).SingleAsync(item => item.Id == quoteId);

        Assert.Equal(1, quote.RevisionNumber);
        Assert.Equal(QuoteStatus.Draft, quote.Status);
        Assert.Equal(seed.BranchId, quote.BranchId);
        Assert.Equal(seed.PartyId, quote.PartyId);
        Assert.Equal(seed.SiteId, quote.SiteId);

        Assert.Equal(2, quote.LineItems.Count);
        int[] sortOrders = [.. quote.LineItems.Select(item => item.SortOrder).OrderBy(sortOrder => sortOrder)];
        Assert.Equal([1, 2], sortOrders);

        Assert.Equal(4833m, quote.Subtotal);
        Assert.Equal(483m, quote.TaxAmount);
        Assert.Equal(5316m, quote.TotalAmount);
    }

    [Fact]
    public async Task IssuingQuoteSynchronisesOpportunityProposalAndExpectedClose()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        QuoteSeed seed = await SeedAsync(application);
        Guid opportunityId = await CreateOpportunityAsync(application, seed, initialAmount: 1000m, initialClose: new DateTime(2026, 10, 1));

        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid quoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-01-01", DefaultLineItems);
        uint version = await GetQuoteVersionAsync(application, quoteId);

        using HttpResponseMessage transitionResponse = await TransitionQuoteAsync(client, quoteId, version, QuoteStatus.Issued);
        Assert.Equal(HttpStatusCode.Redirect, transitionResponse.StatusCode);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Quote quote = await dbContext.Quotes.SingleAsync(item => item.Id == quoteId);
        Assert.Equal(QuoteStatus.Issued, quote.Status);
        Assert.NotNull(quote.IssuedOn);

        SalesOpportunity opportunity = await dbContext.SalesOpportunities.SingleAsync(item => item.Id == opportunityId);
        Assert.Equal(5316m, opportunity.ProposedAmount);
        Assert.Equal(new DateTime(2030, 1, 1), opportunity.ExpectedCloseDate);
    }

    [Fact]
    public async Task EditingNonDraftQuoteIsRefused()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        QuoteSeed seed = await SeedAsync(application);
        Guid opportunityId = await CreateOpportunityAsync(application, seed);

        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid quoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-01-01", DefaultLineItems);
        uint version = await GetQuoteVersionAsync(application, quoteId);
        using HttpResponseMessage transitionResponse = await TransitionQuoteAsync(client, quoteId, version, QuoteStatus.Issued);
        Assert.Equal(HttpStatusCode.Redirect, transitionResponse.StatusCode);

        using HttpResponseMessage editPage = await client.GetAsync($"/quotes/{quoteId}/edit");
        Assert.Equal(HttpStatusCode.Redirect, editPage.StatusCode);

        uint issuedVersion = await GetQuoteVersionAsync(application, quoteId);
        string editToken = await GetAntiforgeryTokenAsync(client, $"/quotes/{quoteId}");
        using HttpResponseMessage crafted = await client.PostAsync(
            $"/quotes/{quoteId}/edit",
            QuoteEditForm(quoteId, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-01-01", "Fictional crafted note", issuedVersion, DefaultLineItems, editToken));
        Assert.Equal(HttpStatusCode.BadRequest, crafted.StatusCode);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Quote quote = await dbContext.Quotes.SingleAsync(item => item.Id == quoteId);
        Assert.Null(quote.Notes);
    }

    [Fact]
    public async Task QuotePdfReturnsPdfDocument()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        QuoteSeed seed = await SeedAsync(application);
        Guid opportunityId = await CreateOpportunityAsync(application, seed);

        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid quoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-01-01", DefaultLineItems);

        using HttpResponseMessage response = await client.GetAsync($"/quotes/{quoteId}/pdf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 5);
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes("%PDF-"), bytes[..5]);
    }

    [Fact]
    public async Task SecondQuoteOnSameOpportunityGetsRevisionTwo()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        QuoteSeed seed = await SeedAsync(application);
        Guid opportunityId = await CreateOpportunityAsync(application, seed);

        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid firstQuoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-01-01", DefaultLineItems);
        Guid secondQuoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-02-01", DefaultLineItems);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(1, await dbContext.Quotes.Where(item => item.Id == firstQuoteId).Select(item => item.RevisionNumber).SingleAsync());
        Assert.Equal(2, await dbContext.Quotes.Where(item => item.Id == secondQuoteId).Select(item => item.RevisionNumber).SingleAsync());
    }

    [Fact]
    public async Task StaleVersionOnQuoteTransitionYieldsConflict()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        QuoteSeed seed = await SeedAsync(application);
        Guid opportunityId = await CreateOpportunityAsync(application, seed);

        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid quoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-01-01", DefaultLineItems);
        uint staleVersion = await GetQuoteVersionAsync(application, quoteId);

        string editToken = await GetAntiforgeryTokenAsync(client, $"/quotes/{quoteId}/edit");
        using HttpResponseMessage editResponse = await client.PostAsync(
            $"/quotes/{quoteId}/edit",
            QuoteEditForm(quoteId, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-01-01", "Fictional updated note", staleVersion, DefaultLineItems, editToken));
        Assert.Equal(HttpStatusCode.Redirect, editResponse.StatusCode);

        using HttpResponseMessage transitionResponse = await TransitionQuoteAsync(client, quoteId, staleVersion, QuoteStatus.Issued);
        Assert.Equal(HttpStatusCode.Conflict, transitionResponse.StatusCode);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Quote quote = await dbContext.Quotes.SingleAsync(item => item.Id == quoteId);
        Assert.Equal(QuoteStatus.Draft, quote.Status);
        Assert.Null(quote.IssuedOn);
    }

    [Fact]
    public async Task CreatingQuoteWithZeroLineItemsIsRejected()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        QuoteSeed seed = await SeedAsync(application);
        Guid opportunityId = await CreateOpportunityAsync(application, seed);

        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        string token = await GetAntiforgeryTokenAsync(client, $"/quotes/create?branchId={seed.BranchId}");
        using HttpResponseMessage response = await client.PostAsync(
            "/quotes/create",
            QuoteCreateForm(seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-01-01", null, [], token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(0, await dbContext.Quotes.CountAsync());
    }

    [Fact]
    public async Task UndocumentedQuoteTransitionIsRefused()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        QuoteSeed seed = await SeedAsync(application);
        Guid opportunityId = await CreateOpportunityAsync(application, seed);

        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid quoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-01-01", DefaultLineItems);
        uint version = await GetQuoteVersionAsync(application, quoteId);

        using HttpResponseMessage response = await TransitionQuoteAsync(client, quoteId, version, QuoteStatus.Accepted);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Quote quote = await dbContext.Quotes.SingleAsync(item => item.Id == quoteId);
        Assert.Equal(QuoteStatus.Draft, quote.Status);
    }

    [Fact]
    public async Task QuoteAuditEntriesUseApprovedFieldNames()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        QuoteSeed seed = await SeedAsync(application);
        Guid opportunityId = await CreateOpportunityAsync(application, seed);

        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid quoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-01-01", DefaultLineItems);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        AuditEntry audit = await dbContext.AuditEntries.SingleAsync(item => item.AggregateId == quoteId);
        Assert.Equal(nameof(Quote), audit.AggregateType);
        Assert.Equal("Created", audit.Action);
        Assert.Equal("LineItems,OwnerUserId,SalesOpportunityId,TaxRatePercent,ValidUntil", audit.ChangeSummary);
    }

    [Fact]
    public async Task SalesRepresentativeCannotReachAnotherOwnersQuoteDetailsOrPdf()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        QuoteSeed seed = await SeedAsync(application);
        Guid opportunityId = await CreateOpportunityAsync(application, seed, ownerUserId: seed.SecondSalesUserId);
        Guid quoteId = await CreateQuoteDirectAsync(application, seed, opportunityId, seed.SecondSalesUserId);

        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/quotes/{quoteId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/quotes/{quoteId}/pdf")).StatusCode);
    }

    [Fact]
    public async Task FieldTechnicianCannotReachQuoteManagementRoutes()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        QuoteSeed seed = await SeedAsync(application);
        Guid opportunityId = await CreateOpportunityAsync(application, seed);
        Guid quoteId = await CreateQuoteDirectAsync(application, seed, opportunityId, seed.SalesUserId);

        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.FieldTechnician);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/quotes/create?branchId={seed.BranchId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/quotes/{quoteId}/edit")).StatusCode);
    }

    [Fact]
    public async Task QuoteIndexFiltersByStatusAndPaginatesWithDisjointPages()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        QuoteSeed seed = await SeedAsync(application);
        Guid opportunityId = await CreateOpportunityAsync(application, seed);

        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid firstQuoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-01-01", DefaultLineItems);
        Guid secondQuoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-02-01", DefaultLineItems);
        Guid thirdQuoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-03-01", DefaultLineItems);
        Guid fourthQuoteId = await CreateQuoteViaHttpAsync(
            client, seed.BranchId, opportunityId, seed.SalesUserId, 10m, "2030-04-01", DefaultLineItems);

        uint firstVersion = await GetQuoteVersionAsync(application, firstQuoteId);
        using HttpResponseMessage issueResponse = await TransitionQuoteAsync(client, firstQuoteId, firstVersion, QuoteStatus.Issued);
        Assert.Equal(HttpStatusCode.Redirect, issueResponse.StatusCode);

        using HttpResponseMessage draftPage1 = await client.GetAsync($"/quotes?branchId={seed.BranchId}&status=Draft&page=1&pageSize=2");
        using HttpResponseMessage draftPage2 = await client.GetAsync($"/quotes?branchId={seed.BranchId}&status=Draft&page=2&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, draftPage1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, draftPage2.StatusCode);
        string draftHtml1 = await draftPage1.Content.ReadAsStringAsync();
        string draftHtml2 = await draftPage2.Content.ReadAsStringAsync();

        Assert.Contains($"/quotes/{secondQuoteId}", draftHtml1);
        Assert.Contains($"/quotes/{thirdQuoteId}", draftHtml1);
        Assert.DoesNotContain($"/quotes/{fourthQuoteId}", draftHtml1);
        Assert.DoesNotContain($"/quotes/{firstQuoteId}", draftHtml1);

        Assert.Contains($"/quotes/{fourthQuoteId}", draftHtml2);
        Assert.DoesNotContain($"/quotes/{secondQuoteId}", draftHtml2);
        Assert.DoesNotContain($"/quotes/{thirdQuoteId}", draftHtml2);
        Assert.DoesNotContain($"/quotes/{firstQuoteId}", draftHtml2);

        using HttpResponseMessage issuedPage = await client.GetAsync($"/quotes?branchId={seed.BranchId}&status=Issued");
        string issuedHtml = await issuedPage.Content.ReadAsStringAsync();
        Assert.Contains($"/quotes/{firstQuoteId}", issuedHtml);
        Assert.DoesNotContain($"/quotes/{secondQuoteId}", issuedHtml);
    }

    private static HttpClient CreateClient(FieldOpsWebApplicationFactory application) =>
        application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task LoginAsAsync(HttpClient client, string role)
    {
        using HttpResponseMessage page = await client.GetAsync("/demo-login");
        string html = await page.Content.ReadAsStringAsync();
        string token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        string roleToken = Regex.Match(
            html,
            $"data-role=\"{Regex.Escape(role)}\".*?name=\"roleToken\" value=\"([^\"]+)\"",
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

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        using HttpResponseMessage page = await client.GetAsync(path);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        string token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    private static async Task<QuoteSeed> SeedAsync(FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        ApplicationUser salesUser = await dbContext.Users.SingleAsync(user => user.UserName == "sales.rep@fieldops.demo");
        ApplicationUser technician = await dbContext.Users.SingleAsync(user => user.UserName == "field.tech@fieldops.demo");
        Guid branchId = Assert.IsType<Guid>(salesUser.BranchId);
        Branch branch = await dbContext.Branches.SingleAsync(item => item.Id == branchId);
        IdentityRole salesRole = await dbContext.Roles.SingleAsync(role => role.Name == DemoRoleNames.SalesRepresentative);
        ApplicationUser secondSales = AddUser(dbContext, "second.quote.sales@fieldops.demo", "Fictional Second Sales", branchId, salesRole.Id);

        Party party = Party.CreateOrganization("Fictional Quote Customer");
        party.AddRole(PartyRoleType.Customer);
        party.AssignToBranch(branch);
        party.AddSite(branch, "Fictional Quote Site");
        dbContext.Parties.Add(party);
        await dbContext.SaveChangesAsync();

        return new QuoteSeed(branchId, party.Id, party.Sites.Single().Id, salesUser.Id, secondSales.Id, technician.Id);
    }

    private static ApplicationUser AddUser(
        FieldOpsDbContext dbContext,
        string userName,
        string displayName,
        Guid branchId,
        string roleId)
    {
        ApplicationUser user = new()
        {
            Id = Guid.NewGuid().ToString(),
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = userName,
            NormalizedEmail = userName.ToUpperInvariant(),
            EmailConfirmed = true,
            DisplayName = displayName,
            BranchId = branchId,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        dbContext.Users.Add(user);
        dbContext.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = roleId });
        return user;
    }

    private static async Task<Guid> CreateOpportunityAsync(
        FieldOpsWebApplicationFactory application,
        QuoteSeed seed,
        decimal initialAmount = 1000m,
        DateTime? initialClose = null,
        string? ownerUserId = null)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch branch = await dbContext.Branches.SingleAsync(item => item.Id == seed.BranchId);
        Party party = await dbContext.Parties
            .Include(item => item.BranchAssignments)
            .Include(item => item.Sites)
            .SingleAsync(item => item.Id == seed.PartyId);
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, party.Sites.Single(item => item.Id == seed.SiteId));
        opportunity.AssignOwner(ownerUserId ?? seed.SalesUserId);
        opportunity.SetProposal(initialAmount, initialClose ?? new DateTime(2026, 10, 1));
        opportunity.MoveTo(SalesOpportunityStatus.Contacted, DateTime.UtcNow);
        opportunity.MoveTo(SalesOpportunityStatus.SurveyScheduled, DateTime.UtcNow);
        opportunity.MoveTo(SalesOpportunityStatus.Quoting, DateTime.UtcNow);
        dbContext.SalesOpportunities.Add(opportunity);
        await dbContext.SaveChangesAsync();
        return opportunity.Id;
    }

    private static async Task<Guid> CreateQuoteDirectAsync(
        FieldOpsWebApplicationFactory application,
        QuoteSeed seed,
        Guid opportunityId,
        string ownerUserId)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch branch = await dbContext.Branches.SingleAsync(item => item.Id == seed.BranchId);
        Party party = await dbContext.Parties.Include(item => item.Sites).SingleAsync(item => item.Id == seed.PartyId);
        SalesOpportunity opportunity = await dbContext.SalesOpportunities.SingleAsync(item => item.Id == opportunityId);
        Quote quote = Quote.Create(branch, party, party.Sites.Single(item => item.Id == seed.SiteId), opportunity, "Q-2030-0001", 1, 10m);
        quote.AssignOwner(ownerUserId);
        quote.SetValidUntil(new DateTime(2030, 1, 1));
        quote.AddLineItem("Fictional Widget A", "個", 3m, 1500m);
        dbContext.Quotes.Add(quote);
        await dbContext.SaveChangesAsync();
        return quote.Id;
    }

    private static async Task<Guid> CreateQuoteViaHttpAsync(
        HttpClient client,
        Guid branchId,
        Guid salesOpportunityId,
        string ownerUserId,
        decimal taxRatePercent,
        string validUntil,
        (string Description, string UnitName, decimal Quantity, decimal UnitPrice)[] lineItems,
        string? notes = null)
    {
        string token = await GetAntiforgeryTokenAsync(client, $"/quotes/create?branchId={branchId}");
        using HttpResponseMessage response = await client.PostAsync(
            "/quotes/create",
            QuoteCreateForm(branchId, salesOpportunityId, ownerUserId, taxRatePercent, validUntil, notes, lineItems, token));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        string location = response.Headers.Location?.OriginalString ?? string.Empty;
        Assert.StartsWith("/quotes/", location);
        return Guid.Parse(location["/quotes/".Length..]);
    }

    private static async Task<HttpResponseMessage> TransitionQuoteAsync(
        HttpClient client, Guid quoteId, uint version, QuoteStatus next)
    {
        string token = await GetAntiforgeryTokenAsync(client, $"/quotes/{quoteId}");
        return await client.PostAsync($"/quotes/{quoteId}/transition", QuoteTransitionForm(version, next, token));
    }

    private static async Task<uint> GetQuoteVersionAsync(FieldOpsWebApplicationFactory application, Guid id)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        return await dbContext.Quotes.Where(item => item.Id == id).Select(item => item.Version).SingleAsync();
    }

    private static Dictionary<string, string> QuoteFormFields(
        Guid branchId,
        Guid salesOpportunityId,
        string ownerUserId,
        decimal taxRatePercent,
        string validUntil,
        string? notes,
        (string Description, string UnitName, decimal Quantity, decimal UnitPrice)[] lineItems,
        string token)
    {
        Dictionary<string, string> fields = new()
        {
            ["BranchId"] = branchId.ToString(),
            ["SalesOpportunityId"] = salesOpportunityId.ToString(),
            ["OwnerUserId"] = ownerUserId,
            ["TaxRatePercent"] = taxRatePercent.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ValidUntil"] = validUntil,
            ["__RequestVerificationToken"] = token
        };
        if (notes is not null) fields["Notes"] = notes;
        for (int index = 0; index < lineItems.Length; index++)
        {
            fields[$"LineItems[{index}].Description"] = lineItems[index].Description;
            fields[$"LineItems[{index}].UnitName"] = lineItems[index].UnitName;
            fields[$"LineItems[{index}].Quantity"] = lineItems[index].Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields[$"LineItems[{index}].UnitPrice"] = lineItems[index].UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return fields;
    }

    private static FormUrlEncodedContent QuoteCreateForm(
        Guid branchId,
        Guid salesOpportunityId,
        string ownerUserId,
        decimal taxRatePercent,
        string validUntil,
        string? notes,
        (string Description, string UnitName, decimal Quantity, decimal UnitPrice)[] lineItems,
        string token) =>
        new(QuoteFormFields(branchId, salesOpportunityId, ownerUserId, taxRatePercent, validUntil, notes, lineItems, token));

    private static FormUrlEncodedContent QuoteEditForm(
        Guid id,
        Guid branchId,
        Guid salesOpportunityId,
        string ownerUserId,
        decimal taxRatePercent,
        string validUntil,
        string? notes,
        uint version,
        (string Description, string UnitName, decimal Quantity, decimal UnitPrice)[] lineItems,
        string token)
    {
        Dictionary<string, string> fields = QuoteFormFields(branchId, salesOpportunityId, ownerUserId, taxRatePercent, validUntil, notes, lineItems, token);
        fields["Id"] = id.ToString();
        fields["Version"] = version.ToString();
        return new FormUrlEncodedContent(fields);
    }

    private static FormUrlEncodedContent QuoteTransitionForm(uint version, QuoteStatus next, string token) =>
        new(new Dictionary<string, string>
        {
            ["Version"] = version.ToString(),
            ["NextStatus"] = next.ToString(),
            ["__RequestVerificationToken"] = token
        });

    private sealed record QuoteSeed(
        Guid BranchId,
        Guid PartyId,
        Guid SiteId,
        string SalesUserId,
        string SecondSalesUserId,
        string TechnicianUserId);
}