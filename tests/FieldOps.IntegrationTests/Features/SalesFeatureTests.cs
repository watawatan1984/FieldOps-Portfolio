using System.Net;
using System.Text.RegularExpressions;

using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Features.Abstractions;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace FieldOps.IntegrationTests.Features;

[Collection(DatabaseCollection.Name)]
public sealed class SalesFeatureTests(PostgresFixture postgres)
{
    [Fact]
    public async Task SalesRepresentativeCreatesOwnedOpportunityWithOneFieldOnlyAudit()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        string token = await GetAntiforgeryTokenAsync(client, $"/sales/create?branchId={seed.CentralBranchId}");

        using HttpResponseMessage response = await client.PostAsync(
            "/sales/create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["BranchId"] = seed.CentralBranchId.ToString(),
                ["PartyId"] = seed.CentralPartyId.ToString(),
                ["SiteId"] = seed.CentralSiteId.ToString(),
                ["OwnerUserId"] = seed.SalesUserId,
                ["ProposedAmount"] = "12500.00",
                ["ExpectedCloseDate"] = "2026-09-15",
                ["__RequestVerificationToken"] = token
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/sales/", response.Headers.Location?.OriginalString);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        SalesOpportunity opportunity = await dbContext.SalesOpportunities.SingleAsync();
        Assert.Equal(seed.SalesUserId, opportunity.OwnerUserId);
        Assert.Equal(12500m, opportunity.ProposedAmount);
        AuditEntry audit = await dbContext.AuditEntries.SingleAsync(item => item.AggregateId == opportunity.Id);
        Assert.Equal("Created", audit.Action);
        Assert.Equal("BranchId,ExpectedCloseDate,OwnerUserId,PartyId,ProposedAmount,SiteId", audit.ChangeSummary);
        Assert.DoesNotContain("Fictional", audit.ChangeSummary);
    }

    [Fact]
    public async Task ListFiltersByOwnerStatusCloseAmountAndPartySiteWithStableBoundedPaging()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        Guid firstId = await CreateOpportunityAsync(application, seed, "Fictional Alpha Prospect", "Alpha Pump Station", seed.SalesUserId, 100m, new DateTime(2026, 9, 10), SalesOpportunityStatus.Contacted);
        Guid secondId = await CreateOpportunityAsync(application, seed, "Fictional Bravo Prospect", "Bravo Water Site", seed.SecondSalesUserId, 200m, new DateTime(2026, 9, 20), SalesOpportunityStatus.Contacted);
        await CreateOpportunityAsync(application, seed, "Fictional Charlie Prospect", "Charlie Yard", seed.SalesUserId, 300m, new DateTime(2026, 10, 5), SalesOpportunityStatus.OnHold);
        using HttpClient administrator = CreateClient(application);
        await LoginAsAsync(administrator, DemoRoleNames.SystemAdministrator);

        using HttpResponseMessage filtered = await administrator.GetAsync(
            $"/sales?branchId={seed.CentralBranchId}&ownerUserId={seed.SecondSalesUserId}&status=Contacted&expectedCloseFrom=2026-09-15&expectedCloseTo=2026-09-30&minimumAmount=150&maximumAmount=250&search=water");
        string filteredHtml = await filtered.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        Assert.Contains("Fictional Bravo Prospect", filteredHtml);
        Assert.DoesNotContain("Fictional Alpha Prospect", filteredHtml);
        Assert.DoesNotContain("Fictional Charlie Prospect", filteredHtml);

        using HttpResponseMessage firstPage = await administrator.GetAsync($"/sales?branchId={seed.CentralBranchId}&page=1&pageSize=1");
        using HttpResponseMessage secondPage = await administrator.GetAsync($"/sales?branchId={seed.CentralBranchId}&page=2&pageSize=1");
        string firstHtml = await firstPage.Content.ReadAsStringAsync();
        string secondHtml = await secondPage.Content.ReadAsStringAsync();
        Assert.Contains($"/sales/{firstId}", firstHtml);
        Assert.Contains($"/sales/{secondId}", secondHtml);
        Assert.Contains("pageSize=1", firstHtml);

        using HttpResponseMessage oversized = await administrator.GetAsync($"/sales?branchId={seed.CentralBranchId}&pageSize=500");
        Assert.Contains("pageSize=100", await oversized.Content.ReadAsStringAsync());
        using HttpResponseMessage overflow = await administrator.GetAsync($"/sales?branchId={seed.CentralBranchId}&page={int.MaxValue}&pageSize=100");
        Assert.Equal(HttpStatusCode.BadRequest, overflow.StatusCode);
    }

    [Fact]
    public async Task EditValidatesProposalAndReturnsRetryableConflictWithoutAudit()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        Guid id = await CreateOpportunityAsync(application, seed, "Fictional Editable Prospect", "Editable Site", seed.SalesUserId, 500m, new DateTime(2026, 9, 12));
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        uint version = await GetVersionAsync(application, id);
        (Guid partyId, Guid siteId) = await GetOpportunityKeysAsync(application, id);
        string token = await GetAntiforgeryTokenAsync(client, $"/sales/{id}/edit");

        using HttpResponseMessage updated = await client.PostAsync($"/sales/{id}/edit", EditForm(
            id, seed.CentralBranchId, partyId, siteId, seed.SalesUserId,
            seed.CentralTechnicianUserId, 750m, "2026-09-25", version, token));
        Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        await using (AsyncServiceScope scope = application.Services.CreateAsyncScope())
        {
            FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
            SalesOpportunity opportunity = await dbContext.SalesOpportunities.SingleAsync(item => item.Id == id);
            Assert.Equal(seed.CentralTechnicianUserId, opportunity.AssignedUserId);
            Assert.Equal(750m, opportunity.ProposedAmount);
            AuditEntry audit = await dbContext.AuditEntries.SingleAsync(item => item.AggregateId == id);
            Assert.Equal("Updated", audit.Action);
            Assert.Equal("AssignedUserId,ExpectedCloseDate,ProposedAmount", audit.ChangeSummary);
        }

        version = await GetVersionAsync(application, id);
        token = await GetAntiforgeryTokenAsync(client, $"/sales/{id}/edit");
        using HttpResponseMessage invalid = await client.PostAsync($"/sales/{id}/edit", EditForm(
            id, seed.CentralBranchId, partyId, siteId, seed.SalesUserId,
            seed.CentralTechnicianUserId, -1m, "", version, token));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        token = await GetAntiforgeryTokenAsync(client, $"/sales/{id}/edit");
        uint staleVersion = await GetVersionAsync(application, id);
        await TouchOpportunityAsync(application, id);
        uint latestVersion = await GetVersionAsync(application, id);
        using HttpResponseMessage stale = await client.PostAsync($"/sales/{id}/edit", EditForm(
            id, seed.CentralBranchId, partyId, siteId, seed.SalesUserId,
            seed.CentralTechnicianUserId, 800m, "2026-09-30", staleVersion, token));
        string staleHtml = await stale.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("review the latest version and retry", staleHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(latestVersion.ToString(), GetInputValue(staleHtml, "Version"));
        await using AsyncServiceScope finalScope = application.Services.CreateAsyncScope();
        FieldOpsDbContext finalDb = finalScope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(1, await finalDb.AuditEntries.CountAsync(item => item.AggregateId == id));
    }

    [Fact]
    public async Task DetailsShowsOnlyValidTransitionsAndCraftedInvalidTransitionIsRejectedWithoutAudit()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        Guid id = await CreateOpportunityAsync(application, seed, "Fictional Transition Prospect", "Transition Site", seed.SalesUserId, 900m, new DateTime(2026, 9, 15));
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);

        using HttpResponseMessage details = await client.GetAsync($"/sales/{id}");
        string html = await details.Content.ReadAsStringAsync();
        Assert.Contains("Move to Contacted", html);
        Assert.Contains("Move to Lost", html);
        Assert.Contains("Move to OnHold", html);
        Assert.DoesNotContain("Move to Won", html);
        string token = ExtractAntiforgeryToken(html);
        uint version = await GetVersionAsync(application, id);
        using HttpResponseMessage invalid = await client.PostAsync($"/sales/{id}/transition", TransitionForm(version, SalesOpportunityStatus.Won, token));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using HttpResponseMessage refreshed = await client.GetAsync($"/sales/{id}");
        token = ExtractAntiforgeryToken(await refreshed.Content.ReadAsStringAsync());
        using HttpResponseMessage valid = await client.PostAsync($"/sales/{id}/transition", TransitionForm(version, SalesOpportunityStatus.Contacted, token));
        Assert.Equal(HttpStatusCode.Redirect, valid.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(SalesOpportunityStatus.Contacted, await dbContext.SalesOpportunities.Where(item => item.Id == id).Select(item => item.Status).SingleAsync());
        AuditEntry audit = await dbContext.AuditEntries.SingleAsync(item => item.AggregateId == id);
        Assert.Equal("StatusChanged", audit.Action);
        Assert.Equal("NextStatus", audit.ChangeSummary);

        using HttpResponseMessage contacted = await client.GetAsync($"/sales/{id}");
        string contactedHtml = await contacted.Content.ReadAsStringAsync();
        token = ExtractAntiforgeryToken(contactedHtml);
        uint staleVersion = await GetVersionAsync(application, id);
        await TouchOpportunityAsync(application, id);
        uint latestVersion = await GetVersionAsync(application, id);
        using HttpResponseMessage stale = await client.PostAsync(
            $"/sales/{id}/transition",
            TransitionForm(staleVersion, SalesOpportunityStatus.SurveyScheduled, token));
        string staleHtml = await stale.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("review the latest version and retry", staleHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"value=\"{latestVersion}\"", staleHtml);
        Assert.Equal(1, await dbContext.AuditEntries.CountAsync(item => item.AggregateId == id));
    }

    [Fact]
    public async Task LoadedOwnerBranchAndTechnicianAssignmentControlReadAndWriteAccess()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        Guid ownedId = await CreateOpportunityAsync(application, seed, "Fictional Owned Prospect", "Owned Site", seed.SalesUserId, 100m, new DateTime(2026, 9, 10));
        Guid otherOwnerId = await CreateOpportunityAsync(application, seed, "Fictional Other Owner Prospect", "Other Owner Site", seed.SecondSalesUserId, 200m, new DateTime(2026, 9, 11));
        Guid assignedFieldId = await CreateFieldOpportunityAsync(application, seed, true);
        Guid unassignedFieldId = await CreateFieldOpportunityAsync(application, seed, false);

        using HttpClient sales = CreateClient(application);
        await LoginAsAsync(sales, DemoRoleNames.SalesRepresentative);
        string createToken = await GetAntiforgeryTokenAsync(sales, $"/sales/create?branchId={seed.CentralBranchId}");
        using HttpResponseMessage ownerTampering = await sales.PostAsync(
            "/sales/create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["BranchId"] = seed.CentralBranchId.ToString(),
                ["PartyId"] = seed.CentralPartyId.ToString(),
                ["SiteId"] = seed.CentralSiteId.ToString(),
                ["OwnerUserId"] = seed.SecondSalesUserId,
                ["__RequestVerificationToken"] = createToken
            }));
        Assert.Equal(HttpStatusCode.Forbidden, ownerTampering.StatusCode);
        using HttpResponseMessage salesList = await sales.GetAsync($"/sales?branchId={seed.CentralBranchId}");
        string salesHtml = await salesList.Content.ReadAsStringAsync();
        Assert.Contains("Fictional Owned Prospect", salesHtml);
        Assert.DoesNotContain("Fictional Other Owner Prospect", salesHtml);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync($"/sales/{otherOwnerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync($"/sales?branchId={seed.FieldBranchId}")).StatusCode);
        string ownedToken = ExtractAntiforgeryToken(await (await sales.GetAsync($"/sales/{ownedId}")).Content.ReadAsStringAsync());
        using HttpResponseMessage crafted = await sales.PostAsync($"/sales/{otherOwnerId}/transition", TransitionForm(await GetVersionAsync(application, otherOwnerId), SalesOpportunityStatus.Contacted, ownedToken));
        Assert.Equal(HttpStatusCode.Forbidden, crafted.StatusCode);

        using HttpClient manager = CreateClient(application);
        await LoginAsAsync(manager, DemoRoleNames.BranchManager);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/sales/{otherOwnerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/sales/{assignedFieldId}")).StatusCode);

        using HttpClient technician = CreateClient(application);
        await LoginAsAsync(technician, DemoRoleNames.FieldTechnician);
        using HttpResponseMessage techList = await technician.GetAsync($"/sales?branchId={seed.FieldBranchId}");
        string techHtml = await techList.Content.ReadAsStringAsync();
        Assert.Contains($"/sales/{assignedFieldId}", techHtml);
        Assert.DoesNotContain($"/sales/{unassignedFieldId}", techHtml);
        using HttpResponseMessage techDetails = await technician.GetAsync($"/sales/{assignedFieldId}");
        string techDetailsHtml = await techDetails.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, techDetails.StatusCode);
        Assert.DoesNotContain("Edit opportunity", techDetailsHtml);
        Assert.DoesNotContain("Available actions", techDetailsHtml);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.GetAsync($"/sales/{unassignedFieldId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.GetAsync($"/sales/{assignedFieldId}/edit")).StatusCode);

        await using AsyncServiceScope auditScope = application.Services.CreateAsyncScope();
        FieldOpsDbContext auditDb = auditScope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Empty(await auditDb.AuditEntries.ToListAsync());
    }

    [Fact]
    public async Task AuditHistoryRequiresExplicitAuditPolicyAndBranchScope()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        Guid centralId = await CreateOpportunityAsync(
            application,
            seed,
            "Fictional Audited Prospect",
            "Audited Site",
            seed.SalesUserId,
            640m,
            new DateTime(2026, 9, 18));
        Guid fieldId = await CreateFieldOpportunityAsync(application, seed, true);
        await AddAuditAsync(application, centralId, seed.CentralBranchId);
        await AddAuditAsync(application, fieldId, seed.FieldBranchId);

        using HttpClient sales = CreateClient(application);
        await LoginAsAsync(sales, DemoRoleNames.SalesRepresentative);
        string salesHtml = await sales.GetStringAsync($"/sales/{centralId}");
        Assert.DoesNotContain("Audit history", salesHtml);
        Assert.DoesNotContain("OwnerUserId", salesHtml);

        using HttpClient technician = CreateClient(application);
        await LoginAsAsync(technician, DemoRoleNames.FieldTechnician);
        string technicianHtml = await technician.GetStringAsync($"/sales/{fieldId}");
        Assert.DoesNotContain("Audit history", technicianHtml);
        Assert.DoesNotContain("OwnerUserId", technicianHtml);

        using HttpClient manager = CreateClient(application);
        await LoginAsAsync(manager, DemoRoleNames.BranchManager);
        string managerHtml = await manager.GetStringAsync($"/sales/{centralId}");
        Assert.Contains("Audit history", managerHtml);
        Assert.Contains("OwnerUserId", managerHtml);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/sales/{fieldId}")).StatusCode);

        using HttpClient administrator = CreateClient(application);
        await LoginAsAsync(administrator, DemoRoleNames.SystemAdministrator);
        string administratorHtml = await administrator.GetStringAsync($"/sales/{fieldId}");
        Assert.Contains("Audit history", administratorHtml);
        Assert.Contains("OwnerUserId", administratorHtml);
    }

    [Fact]
    public async Task StaleTransitionReauthorizesAfterConcurrentOwnerChange()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        OwnerReassignmentPlan plan = new(connectionString);
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.AddSingleton(plan);
                services.AddScoped<IMutationExecutor, OwnerReassigningMutationExecutor>();
            });
        SalesSeed seed = await SeedAsync(application);
        Guid id = await CreateOpportunityAsync(
            application,
            seed,
            "Fictional Concurrent Owner Prospect",
            "Concurrent Owner Site",
            seed.SalesUserId,
            720m,
            new DateTime(2026, 9, 22));
        plan.Configure(id, seed.SecondSalesUserId);
        using HttpClient sales = CreateClient(application);
        await LoginAsAsync(sales, DemoRoleNames.SalesRepresentative);
        string detailsHtml = await sales.GetStringAsync($"/sales/{id}");
        string token = ExtractAntiforgeryToken(detailsHtml);
        uint version = await GetVersionAsync(application, id);

        using HttpResponseMessage response = await sales.PostAsync(
            $"/sales/{id}/transition",
            TransitionForm(version, SalesOpportunityStatus.Contacted, token));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Available actions", body);
        Assert.DoesNotContain("Edit opportunity", body);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        SalesOpportunity persisted = await dbContext.SalesOpportunities.SingleAsync(item => item.Id == id);
        Assert.Equal(seed.SecondSalesUserId, persisted.OwnerUserId);
        Assert.Equal(SalesOpportunityStatus.New, persisted.Status);
        Assert.False(await dbContext.AuditEntries.AnyAsync(item => item.AggregateId == id));
    }

    [Fact]
    public async Task StaleEditReauthorizesAfterConcurrentOwnerChange()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        OwnerReassignmentPlan plan = new(connectionString);
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            services =>
            {
                services.AddSingleton(plan);
                services.AddScoped<IMutationExecutor, OwnerReassigningMutationExecutor>();
            });
        SalesSeed seed = await SeedAsync(application);
        Guid id = await CreateOpportunityAsync(
            application,
            seed,
            "Fictional Concurrent Edit Prospect",
            "Concurrent Edit Site",
            seed.SalesUserId,
            725m,
            new DateTime(2026, 9, 23));
        plan.Configure(id, seed.SecondSalesUserId);
        (Guid partyId, Guid siteId) = await GetOpportunityKeysAsync(application, id);
        uint version = await GetVersionAsync(application, id);
        using HttpClient sales = CreateClient(application);
        await LoginAsAsync(sales, DemoRoleNames.SalesRepresentative);
        string token = await GetAntiforgeryTokenAsync(sales, $"/sales/{id}/edit");

        using HttpResponseMessage response = await sales.PostAsync(
            $"/sales/{id}/edit",
            EditForm(
                id,
                seed.CentralBranchId,
                partyId,
                siteId,
                seed.SalesUserId,
                null,
                825m,
                "2026-09-24",
                version,
                token));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("OwnerUserId", body);
        Assert.DoesNotContain("Retry", body);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        SalesOpportunity persisted = await dbContext.SalesOpportunities.SingleAsync(item => item.Id == id);
        Assert.Equal(seed.SecondSalesUserId, persisted.OwnerUserId);
        Assert.Equal(725m, persisted.ProposedAmount);
        Assert.False(await dbContext.AuditEntries.AnyAsync(item => item.AggregateId == id));
    }

    [Fact]
    public async Task ExistingProposalCannotBeSilentlyClearedWithoutAudit()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        Guid id = await CreateOpportunityAsync(
            application,
            seed,
            "Fictional Proposal Retention Prospect",
            "Proposal Retention Site",
            seed.SalesUserId,
            830m,
            new DateTime(2026, 9, 24));
        (Guid partyId, Guid siteId) = await GetOpportunityKeysAsync(application, id);
        uint version = await GetVersionAsync(application, id);
        using HttpClient sales = CreateClient(application);
        await LoginAsAsync(sales, DemoRoleNames.SalesRepresentative);
        string token = await GetAntiforgeryTokenAsync(sales, $"/sales/{id}/edit");

        using HttpResponseMessage response = await sales.PostAsync(
            $"/sales/{id}/edit",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = id.ToString(),
                ["BranchId"] = seed.CentralBranchId.ToString(),
                ["PartyId"] = partyId.ToString(),
                ["SiteId"] = siteId.ToString(),
                ["OwnerUserId"] = seed.SalesUserId,
                ["AssignedUserId"] = string.Empty,
                ["ProposedAmount"] = string.Empty,
                ["ExpectedCloseDate"] = string.Empty,
                ["Version"] = version.ToString(),
                ["__RequestVerificationToken"] = token
            }));

        string html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("proposal cannot be cleared", html, StringComparison.OrdinalIgnoreCase);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        SalesOpportunity persisted = await dbContext.SalesOpportunities.SingleAsync(item => item.Id == id);
        Assert.Equal(830m, persisted.ProposedAmount);
        Assert.Equal(new DateTime(2026, 9, 24), persisted.ExpectedCloseDate);
        Assert.False(await dbContext.AuditEntries.AnyAsync(item => item.AggregateId == id));
    }

    [Fact]
    public async Task AdministratorSelectsAnyOrAllBranchesWhileManagerRemainsClaimScoped()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        Guid centralId = await CreateOpportunityAsync(
            application,
            seed,
            "Fictional Central Branch Prospect",
            "Central Branch Site",
            seed.SalesUserId,
            910m,
            new DateTime(2026, 9, 26));
        Guid fieldId = await CreateFieldOpportunityAsync(application, seed, true);

        using HttpClient administrator = CreateClient(application);
        await LoginAsAsync(administrator, DemoRoleNames.SystemAdministrator);
        using HttpResponseMessage allBranches = await administrator.GetAsync("/sales");
        string allHtml = await allBranches.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, allBranches.StatusCode);
        Assert.Contains("All branches", allHtml);
        Assert.Contains("id=\"branch-filter\"", allHtml);
        Assert.Contains($"value=\"{seed.CentralBranchId}\"", allHtml);
        Assert.Contains($"value=\"{seed.FieldBranchId}\"", allHtml);
        Assert.Contains($"/sales/{centralId}", allHtml);
        Assert.Contains($"/sales/{fieldId}", allHtml);

        using HttpResponseMessage selectedBranch = await administrator.GetAsync(
            $"/sales?branchId={seed.FieldBranchId}");
        string selectedHtml = await selectedBranch.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, selectedBranch.StatusCode);
        Assert.Contains($"/sales/{fieldId}", selectedHtml);
        Assert.DoesNotContain($"/sales/{centralId}", selectedHtml);

        using HttpClient manager = CreateClient(application);
        await LoginAsAsync(manager, DemoRoleNames.BranchManager);
        using HttpResponseMessage managerLanding = await manager.GetAsync("/sales");
        Assert.Equal(HttpStatusCode.Redirect, managerLanding.StatusCode);
        Assert.Contains($"branchId={seed.CentralBranchId}", managerLanding.Headers.Location?.OriginalString);
        using HttpResponseMessage managerPage = await manager.GetAsync(managerLanding.Headers.Location);
        Assert.DoesNotContain("id=\"branch-filter\"", await managerPage.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CraftedEditCannotChangeOwnerOrImmutableBranchContext()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        Guid id = await CreateOpportunityAsync(
            application,
            seed,
            "Fictional Crafted Edit Prospect",
            "Crafted Edit Site",
            seed.SalesUserId,
            960m,
            new DateTime(2026, 9, 27));
        (Guid partyId, Guid siteId) = await GetOpportunityKeysAsync(application, id);
        uint version = await GetVersionAsync(application, id);
        using HttpClient sales = CreateClient(application);
        await LoginAsAsync(sales, DemoRoleNames.SalesRepresentative);
        string token = await GetAntiforgeryTokenAsync(sales, $"/sales/{id}/edit");

        using HttpResponseMessage ownerTamper = await sales.PostAsync(
            $"/sales/{id}/edit",
            EditForm(
                id,
                seed.CentralBranchId,
                partyId,
                siteId,
                seed.SecondSalesUserId,
                null,
                960m,
                "2026-09-27",
                version,
                token));
        Assert.Equal(HttpStatusCode.Forbidden, ownerTamper.StatusCode);

        token = await GetAntiforgeryTokenAsync(sales, $"/sales/{id}/edit");
        using HttpResponseMessage branchTamper = await sales.PostAsync(
            $"/sales/{id}/edit",
            EditForm(
                id,
                seed.FieldBranchId,
                seed.FieldPartyId,
                seed.FieldSiteId,
                seed.SalesUserId,
                null,
                960m,
                "2026-09-27",
                version,
                token));
        string branchBody = await branchTamper.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, branchTamper.StatusCode);
        Assert.DoesNotContain("Fictional Field Customer", branchBody);
        Assert.DoesNotContain(seed.FieldSalesUserId, branchBody);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        SalesOpportunity persisted = await dbContext.SalesOpportunities.SingleAsync(item => item.Id == id);
        Assert.Equal(seed.SalesUserId, persisted.OwnerUserId);
        Assert.Equal(seed.CentralBranchId, persisted.BranchId);
        Assert.False(await dbContext.AuditEntries.AnyAsync(item => item.AggregateId == id));
    }

    [Fact]
    public async Task BranchManagerMutatesAnyOpportunityInOwnBranch()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        Guid id = await CreateOpportunityAsync(
            application,
            seed,
            "Fictional Manager Mutation Prospect",
            "Manager Mutation Site",
            seed.SecondSalesUserId,
            1010m,
            new DateTime(2026, 9, 28));
        (Guid partyId, Guid siteId) = await GetOpportunityKeysAsync(application, id);
        uint version = await GetVersionAsync(application, id);
        using HttpClient manager = CreateClient(application);
        await LoginAsAsync(manager, DemoRoleNames.BranchManager);
        string token = await GetAntiforgeryTokenAsync(manager, $"/sales/{id}/edit");

        using HttpResponseMessage response = await manager.PostAsync(
            $"/sales/{id}/edit",
            EditForm(
                id,
                seed.CentralBranchId,
                partyId,
                siteId,
                seed.SalesUserId,
                seed.CentralTechnicianUserId,
                1110m,
                "2026-10-01",
                version,
                token));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        SalesOpportunity persisted = await dbContext.SalesOpportunities.SingleAsync(item => item.Id == id);
        Assert.Equal(seed.SalesUserId, persisted.OwnerUserId);
        Assert.Equal(seed.CentralTechnicianUserId, persisted.AssignedUserId);
        Assert.Equal(1110m, persisted.ProposedAmount);
        AuditEntry audit = await dbContext.AuditEntries.SingleAsync(item => item.AggregateId == id);
        Assert.Equal("Updated", audit.Action);
        Assert.Equal("AssignedUserId,ExpectedCloseDate,OwnerUserId,ProposedAmount", audit.ChangeSummary);
    }

    [Fact]
    public async Task LegacyUnownedOpportunityIsVisibleOnlyToManagerAndAdministrator()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        Guid id = await CreateLegacyUnownedOpportunityAsync(application, seed);

        using HttpClient administrator = CreateClient(application);
        await LoginAsAsync(administrator, DemoRoleNames.SystemAdministrator);
        string administratorHtml = await administrator.GetStringAsync("/sales");
        Assert.Contains($"/sales/{id}", administratorHtml);
        Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync($"/sales/{id}")).StatusCode);

        using HttpClient manager = CreateClient(application);
        await LoginAsAsync(manager, DemoRoleNames.BranchManager);
        string managerHtml = await manager.GetStringAsync($"/sales?branchId={seed.CentralBranchId}");
        Assert.Contains($"/sales/{id}", managerHtml);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/sales/{id}")).StatusCode);

        using HttpClient sales = CreateClient(application);
        await LoginAsAsync(sales, DemoRoleNames.SalesRepresentative);
        string salesHtml = await sales.GetStringAsync($"/sales?branchId={seed.CentralBranchId}");
        Assert.DoesNotContain($"/sales/{id}", salesHtml);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync($"/sales/{id}")).StatusCode);
    }

    [Fact]
    public async Task TerminalOpportunityRejectsCraftedTransitionWithoutAudit()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        Guid id = await CreateOpportunityAsync(
            application,
            seed,
            "Fictional Terminal Prospect",
            "Terminal Site",
            seed.SalesUserId,
            1180m,
            new DateTime(2026, 9, 29),
            SalesOpportunityStatus.Lost);
        using HttpClient sales = CreateClient(application);
        await LoginAsAsync(sales, DemoRoleNames.SalesRepresentative);
        string detailsHtml = await sales.GetStringAsync($"/sales/{id}");
        Assert.DoesNotContain("Available actions", detailsHtml);
        Assert.DoesNotContain("Move to", detailsHtml);
        string token = await GetAntiforgeryTokenAsync(sales, $"/sales/{id}/edit");
        uint version = await GetVersionAsync(application, id);

        using HttpResponseMessage response = await sales.PostAsync(
            $"/sales/{id}/transition",
            TransitionForm(version, SalesOpportunityStatus.Contacted, token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(
            SalesOpportunityStatus.Lost,
            await dbContext.SalesOpportunities.Where(item => item.Id == id).Select(item => item.Status).SingleAsync());
        Assert.False(await dbContext.AuditEntries.AnyAsync(item => item.AggregateId == id));
    }

    [Fact]
    public async Task AllSalesWritesRejectMissingAntiforgeryToken()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        SalesSeed seed = await SeedAsync(application);
        Guid id = await CreateOpportunityAsync(
            application,
            seed,
            "Fictional Antiforgery Prospect",
            "Antiforgery Site",
            seed.SalesUserId,
            1220m,
            new DateTime(2026, 9, 30));
        (Guid partyId, Guid siteId) = await GetOpportunityKeysAsync(application, id);
        uint version = await GetVersionAsync(application, id);
        using HttpClient sales = CreateClient(application);
        await LoginAsAsync(sales, DemoRoleNames.SalesRepresentative);

        using HttpResponseMessage create = await sales.PostAsync(
            "/sales/create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["BranchId"] = seed.CentralBranchId.ToString(),
                ["PartyId"] = seed.CentralPartyId.ToString(),
                ["SiteId"] = seed.CentralSiteId.ToString(),
                ["OwnerUserId"] = seed.SalesUserId
            }));
        using HttpResponseMessage edit = await sales.PostAsync(
            $"/sales/{id}/edit",
            EditForm(
                id,
                seed.CentralBranchId,
                partyId,
                siteId,
                seed.SalesUserId,
                null,
                1300m,
                "2026-10-02",
                version,
                string.Empty));
        using HttpResponseMessage transition = await sales.PostAsync(
            $"/sales/{id}/transition",
            TransitionForm(version, SalesOpportunityStatus.Contacted, string.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, transition.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(1, await dbContext.SalesOpportunities.CountAsync());
        SalesOpportunity persisted = await dbContext.SalesOpportunities.SingleAsync(item => item.Id == id);
        Assert.Equal(1220m, persisted.ProposedAmount);
        Assert.Equal(SalesOpportunityStatus.New, persisted.Status);
        Assert.False(await dbContext.AuditEntries.AnyAsync(item => item.AggregateId == id));
    }

    private static HttpClient CreateClient(FieldOpsWebApplicationFactory application) =>
        application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task<SalesSeed> SeedAsync(FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        ApplicationUser salesUser = await dbContext.Users.SingleAsync(user => user.UserName == "sales.rep@fieldops.demo");
        ApplicationUser technician = await dbContext.Users.SingleAsync(user => user.UserName == "field.tech@fieldops.demo");
        Guid centralBranchId = Assert.IsType<Guid>(salesUser.BranchId);
        Guid fieldBranchId = Assert.IsType<Guid>(technician.BranchId);
        Branch centralBranch = await dbContext.Branches.SingleAsync(branch => branch.Id == centralBranchId);
        Branch fieldBranch = await dbContext.Branches.SingleAsync(branch => branch.Id == fieldBranchId);
        IdentityRole salesRole = await dbContext.Roles.SingleAsync(role => role.Name == DemoRoleNames.SalesRepresentative);
        IdentityRole technicianRole = await dbContext.Roles.SingleAsync(role => role.Name == DemoRoleNames.FieldTechnician);
        ApplicationUser secondSales = AddUser(dbContext, "second.sales@fieldops.demo", "Morgan Quinn", centralBranchId, salesRole.Id);
        ApplicationUser fieldSales = AddUser(dbContext, "field.sales@fieldops.demo", "Riley Chen", fieldBranchId, salesRole.Id);
        ApplicationUser centralTechnician = AddUser(dbContext, "central.tech@fieldops.demo", "Jamie Park", centralBranchId, technicianRole.Id);

        Party centralParty = Party.CreateOrganization("Fictional Harbor Customer");
        centralParty.AddRole(PartyRoleType.Customer);
        centralParty.AssignToBranch(centralBranch);
        centralParty.AddSite(centralBranch, "Fictional Harbor Site");
        Party fieldParty = Party.CreateOrganization("Fictional Field Customer");
        fieldParty.AddRole(PartyRoleType.Customer);
        fieldParty.AssignToBranch(fieldBranch);
        fieldParty.AddSite(fieldBranch, "Fictional Field Site");
        dbContext.Parties.AddRange(centralParty, fieldParty);
        await dbContext.SaveChangesAsync();

        return new SalesSeed(
            centralBranchId,
            fieldBranchId,
            centralParty.Id,
            centralParty.Sites.Single().Id,
            fieldParty.Id,
            fieldParty.Sites.Single().Id,
            salesUser.Id,
            secondSales.Id,
            fieldSales.Id,
            technician.Id,
            centralTechnician.Id);
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
        SalesSeed seed,
        string partyName,
        string siteName,
        string ownerUserId,
        decimal amount,
        DateTime expectedClose,
        SalesOpportunityStatus status = SalesOpportunityStatus.New)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch branch = await dbContext.Branches.SingleAsync(item => item.Id == seed.CentralBranchId);
        Party party = Party.CreateOrganization(partyName);
        party.AddRole(PartyRoleType.Customer);
        party.AssignToBranch(branch);
        party.AddSite(branch, siteName);
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, party.Sites.Single());
        opportunity.AssignOwner(ownerUserId);
        opportunity.SetProposal(amount, expectedClose);
        foreach (SalesOpportunityStatus next in PathTo(status)) opportunity.MoveTo(next, DateTime.UtcNow);
        dbContext.AddRange(party, opportunity);
        await dbContext.SaveChangesAsync();
        return opportunity.Id;
    }

    private static async Task<Guid> CreateFieldOpportunityAsync(FieldOpsWebApplicationFactory application, SalesSeed seed, bool assigned)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch branch = await dbContext.Branches.SingleAsync(item => item.Id == seed.FieldBranchId);
        Party party = await dbContext.Parties
            .Include(item => item.BranchAssignments)
            .Include(item => item.Sites)
            .SingleAsync(item => item.Id == seed.FieldPartyId);
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, party.Sites.Single(item => item.Id == seed.FieldSiteId));
        opportunity.AssignOwner(seed.FieldSalesUserId);
        if (assigned) opportunity.AssignToUser(seed.TechnicianUserId);
        dbContext.SalesOpportunities.Add(opportunity);
        await dbContext.SaveChangesAsync();
        return opportunity.Id;
    }

    private static async Task<Guid> CreateLegacyUnownedOpportunityAsync(
        FieldOpsWebApplicationFactory application,
        SalesSeed seed)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch branch = await dbContext.Branches.SingleAsync(item => item.Id == seed.CentralBranchId);
        Party party = Party.CreateOrganization("Fictional Legacy Unowned Prospect");
        party.AddRole(PartyRoleType.Customer);
        party.AssignToBranch(branch);
        party.AddSite(branch, "Legacy Unowned Site");
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, party.Sites.Single());
        dbContext.AddRange(party, opportunity);
        await dbContext.SaveChangesAsync();
        return opportunity.Id;
    }

    private static IEnumerable<SalesOpportunityStatus> PathTo(SalesOpportunityStatus status) => status switch
    {
        SalesOpportunityStatus.New => [],
        SalesOpportunityStatus.Contacted => [SalesOpportunityStatus.Contacted],
        SalesOpportunityStatus.Lost => [SalesOpportunityStatus.Lost],
        SalesOpportunityStatus.OnHold => [SalesOpportunityStatus.OnHold],
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static async Task<uint> GetVersionAsync(FieldOpsWebApplicationFactory application, Guid id)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        return await dbContext.SalesOpportunities.Where(item => item.Id == id).Select(item => item.Version).SingleAsync();
    }

    private static async Task<(Guid PartyId, Guid SiteId)> GetOpportunityKeysAsync(
        FieldOpsWebApplicationFactory application,
        Guid id)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        return await dbContext.SalesOpportunities.Where(item => item.Id == id)
            .Select(item => new ValueTuple<Guid, Guid>(item.PartyId, item.SiteId))
            .SingleAsync();
    }

    private static async Task TouchOpportunityAsync(FieldOpsWebApplicationFactory application, Guid id)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        SalesOpportunity opportunity = await dbContext.SalesOpportunities.SingleAsync(item => item.Id == id);
        opportunity.AssignOwner(opportunity.OwnerUserId!);
        await dbContext.SaveChangesAsync();
    }

    private static async Task AddAuditAsync(
        FieldOpsWebApplicationFactory application,
        Guid opportunityId,
        Guid branchId)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        dbContext.AuditEntries.Add(new AuditEntry(
            nameof(SalesOpportunity),
            opportunityId,
            branchId,
            "Updated",
            "Success",
            "OwnerUserId",
            DateTime.UtcNow,
            "fictional.audit.actor"));
        await dbContext.SaveChangesAsync();
    }

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

    private static string ExtractAntiforgeryToken(string html)
    {
        string token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    private static FormUrlEncodedContent EditForm(
        Guid id,
        Guid branchId,
        Guid partyId,
        Guid siteId,
        string ownerUserId,
        string? assignedUserId,
        decimal amount,
        string expectedCloseDate,
        uint version,
        string token) => new(new Dictionary<string, string>
        {
            ["Id"] = id.ToString(),
            ["BranchId"] = branchId.ToString(),
            ["PartyId"] = partyId.ToString(),
            ["SiteId"] = siteId.ToString(),
            ["OwnerUserId"] = ownerUserId,
            ["AssignedUserId"] = assignedUserId ?? string.Empty,
            ["ProposedAmount"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ExpectedCloseDate"] = expectedCloseDate,
            ["Version"] = version.ToString(),
            ["__RequestVerificationToken"] = token
        });

    private static FormUrlEncodedContent TransitionForm(uint version, SalesOpportunityStatus next, string token) =>
        new(new Dictionary<string, string>
        {
            ["Version"] = version.ToString(),
            ["NextStatus"] = next.ToString(),
            ["__RequestVerificationToken"] = token
        });

    private static string GetInputValue(string html, string id) =>
        Regex.Match(html, $"<input(?=[^>]*id=\"{Regex.Escape(id)}\")(?=[^>]*value=\"([^\"]*)\")[^>]*>", RegexOptions.IgnoreCase).Groups[1].Value;

    private sealed record SalesSeed(
        Guid CentralBranchId,
        Guid FieldBranchId,
        Guid CentralPartyId,
        Guid CentralSiteId,
        Guid FieldPartyId,
        Guid FieldSiteId,
        string SalesUserId,
        string SecondSalesUserId,
        string FieldSalesUserId,
        string TechnicianUserId,
        string CentralTechnicianUserId);

    private sealed class OwnerReassignmentPlan(string connectionString)
    {
        public string ConnectionString { get; } = connectionString;
        public Guid OpportunityId { get; private set; }
        public string OwnerUserId { get; private set; } = string.Empty;

        public void Configure(Guid opportunityId, string ownerUserId)
        {
            OpportunityId = opportunityId;
            OwnerUserId = ownerUserId;
        }
    }

    private sealed class OwnerReassigningMutationExecutor(
        FieldOpsDbContext dbContext,
        OwnerReassignmentPlan plan) : IMutationExecutor
    {
        public async Task<TResult> ExecuteAsync<TResult>(
            string operation,
            Func<CancellationToken, Task<TResult>> action,
            CancellationToken cancellationToken = default)
        {
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            TResult result = await action(cancellationToken);
            await using NpgsqlConnection connection = new(plan.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE \"SalesOpportunities\" SET \"OwnerUserId\" = @owner WHERE \"Id\" = @id";
            command.Parameters.AddWithValue("owner", plan.OwnerUserId);
            command.Parameters.AddWithValue("id", plan.OpportunityId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
    }
}