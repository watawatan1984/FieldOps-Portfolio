using System.Net;
using System.Text.RegularExpressions;

using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.IntegrationTests.Features;

[Collection(DatabaseCollection.Name)]
public sealed class PartyFeatureTests(PostgresFixture postgres)
{
    [Fact]
    public async Task UnicodeNamesThatPostgresNormalizesEquallyShareOneConcurrentLock()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        const string dotlessName = "Fictional Unicode ı";
        const string asciiName = "Fictional Unicode i";
        string dotlessDatabaseName = await GetDatabaseUpperAsync(application, dotlessName);
        string asciiDatabaseName = await GetDatabaseUpperAsync(application, asciiName);
        Assert.Equal(asciiDatabaseName, dotlessDatabaseName);
        Assert.NotEqual(asciiName.ToUpperInvariant(), dotlessName.ToUpperInvariant());

        Guid branchId = await GetBranchIdAsync(application, "sales.rep@fieldops.demo");
        await InstallPartyWriteDelayAsync(application);
        using HttpClient firstClient = CreateClient(application);
        using HttpClient secondClient = CreateClient(application);
        await LoginAsAsync(firstClient, DemoRoleNames.SalesRepresentative);
        await LoginAsAsync(secondClient, DemoRoleNames.SalesRepresentative);
        string firstToken = await GetAntiforgeryTokenAsync(firstClient, $"/parties/create?branchId={branchId}");
        string secondToken = await GetAntiforgeryTokenAsync(secondClient, $"/parties/create?branchId={branchId}");

        Task<HttpResponseMessage> firstRequest = firstClient.PostAsync(
            "/parties/create",
            CreatePartyForm(branchId, dotlessName, firstToken));
        Task<HttpResponseMessage> secondRequest = secondClient.PostAsync(
            "/parties/create",
            CreatePartyForm(branchId, asciiName, secondToken));
        HttpResponseMessage[] responses = await Task.WhenAll(firstRequest, secondRequest);
        using HttpResponseMessage firstResponse = responses[0];
        using HttpResponseMessage secondResponse = responses[1];

        Assert.Equal(
            [HttpStatusCode.Redirect, HttpStatusCode.Conflict],
            responses.Select(response => response.StatusCode).Order().ToArray());
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(1, await dbContext.Parties.CountAsync(
            party => EF.Property<string>(party, "NormalizedName") == asciiDatabaseName));
        Assert.Equal(1, await dbContext.AuditEntries.CountAsync(audit => audit.Action == "Created"));
    }

    [Fact]
    public async Task ConcurrentDuplicateCreatesProduceOneSuccessAndOneConflict()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        Guid branchId = await GetBranchIdAsync(application, "sales.rep@fieldops.demo");
        await InstallPartyWriteDelayAsync(application);
        using HttpClient firstClient = CreateClient(application);
        using HttpClient secondClient = CreateClient(application);
        await LoginAsAsync(firstClient, DemoRoleNames.SalesRepresentative);
        await LoginAsAsync(secondClient, DemoRoleNames.SalesRepresentative);
        string firstToken = await GetAntiforgeryTokenAsync(firstClient, $"/parties/create?branchId={branchId}");
        string secondToken = await GetAntiforgeryTokenAsync(secondClient, $"/parties/create?branchId={branchId}");

        Task<HttpResponseMessage> firstRequest = firstClient.PostAsync(
            "/parties/create",
            CreatePartyForm(branchId, "Fictional Concurrent Duplicate", firstToken));
        Task<HttpResponseMessage> secondRequest = secondClient.PostAsync(
            "/parties/create",
            CreatePartyForm(branchId, "Fictional Concurrent Duplicate", secondToken));
        HttpResponseMessage[] responses = await Task.WhenAll(firstRequest, secondRequest);
        using HttpResponseMessage firstResponse = responses[0];
        using HttpResponseMessage secondResponse = responses[1];

        Assert.Equal(
            [HttpStatusCode.Redirect, HttpStatusCode.Conflict],
            responses.Select(response => response.StatusCode).Order().ToArray());
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(1, await dbContext.Parties.CountAsync(
            party => party.OrganizationName == "Fictional Concurrent Duplicate"));
        Assert.Equal(1, await dbContext.AuditEntries.CountAsync(audit => audit.Action == "Created"));
    }

    [Fact]
    public async Task ConcurrentRenameAndCreateEnforceOneNormalizedNameWinner()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        (Guid partyId, Guid branchId, _, uint version) = await SeedEditablePartyAsync(application);
        await InstallPartyWriteDelayAsync(application);
        using HttpClient renameClient = CreateClient(application);
        using HttpClient createClient = CreateClient(application);
        await LoginAsAsync(renameClient, DemoRoleNames.SalesRepresentative);
        await LoginAsAsync(createClient, DemoRoleNames.SalesRepresentative);
        string renameToken = await GetAntiforgeryTokenAsync(
            renameClient,
            $"/parties/{partyId}/edit?branchId={branchId}");
        string createToken = await GetAntiforgeryTokenAsync(
            createClient,
            $"/parties/create?branchId={branchId}");

        const string targetName = "Fictional Concurrent Rename Target";
        Task<HttpResponseMessage> renameRequest = renameClient.PostAsync(
            $"/parties/{partyId}/edit",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = partyId.ToString(),
                ["BranchId"] = branchId.ToString(),
                ["Version"] = version.ToString(),
                ["OrganizationName"] = targetName,
                ["IsCustomer"] = "true",
                ["__RequestVerificationToken"] = renameToken
            }));
        Task<HttpResponseMessage> createRequest = createClient.PostAsync(
            "/parties/create",
            CreatePartyForm(branchId, targetName, createToken));
        HttpResponseMessage[] responses = await Task.WhenAll(renameRequest, createRequest);
        using HttpResponseMessage renameResponse = responses[0];
        using HttpResponseMessage createResponse = responses[1];

        Assert.Equal(
            [HttpStatusCode.Redirect, HttpStatusCode.Conflict],
            responses.Select(response => response.StatusCode).Order().ToArray());
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(1, await dbContext.Parties.CountAsync(
            party => EF.Property<string>(party, "NormalizedName") == targetName.ToUpperInvariant()));
        Assert.Equal(1, await dbContext.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task BranchScopedDetailsHideOtherAssignmentsWhileAdministratorSeesAll()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        (Guid partyId, Guid sourceBranchId, Guid targetBranchId, _) = await SeedEditablePartyAsync(application);
        await SharePartyOutOfBandAsync(application, partyId, targetBranchId);
        (string sourceName, string targetName) = await GetBranchNamesAsync(
            application,
            sourceBranchId,
            targetBranchId);
        using HttpClient managerClient = CreateClient(application);
        using HttpClient administratorClient = CreateClient(application);
        await LoginAsAsync(managerClient, DemoRoleNames.BranchManager);
        await LoginAsAsync(administratorClient, DemoRoleNames.SystemAdministrator);

        using HttpResponseMessage managerResponse = await managerClient.GetAsync(
            $"/parties/{partyId}?branchId={sourceBranchId}");
        string managerHtml = await managerResponse.Content.ReadAsStringAsync();
        string decodedManagerHtml = WebUtility.HtmlDecode(managerHtml);
        using HttpResponseMessage administratorResponse = await administratorClient.GetAsync(
            $"/parties/{partyId}?branchId={sourceBranchId}");
        string administratorHtml = await administratorResponse.Content.ReadAsStringAsync();
        string decodedAdministratorHtml = WebUtility.HtmlDecode(administratorHtml);

        Assert.Equal(HttpStatusCode.OK, managerResponse.StatusCode);
        Assert.Contains(sourceName, decodedManagerHtml);
        Assert.DoesNotContain(targetName, decodedManagerHtml);
        Assert.Equal(HttpStatusCode.OK, administratorResponse.StatusCode);
        Assert.Contains(sourceName, decodedAdministratorHtml);
        Assert.Contains(targetName, decodedAdministratorHtml);
    }

    [Fact]
    public async Task InvalidShareReturnsFocusedEditValidationWithoutAudit()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        (Guid partyId, Guid branchId, _, uint version) = await SeedEditablePartyAsync(application);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
        string token = await GetAntiforgeryTokenAsync(
            client,
            $"/parties/{partyId}/edit?branchId={branchId}");

        using HttpResponseMessage response = await client.PostAsync(
            $"/parties/{partyId}/share",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["BranchId"] = branchId.ToString(),
                ["Version"] = version.ToString(),
                ["TargetBranchId"] = string.Empty,
                ["__RequestVerificationToken"] = token
            }));
        string html = await response.Content.ReadAsStringAsync();
        string decodedHtml = WebUtility.HtmlDecode(html);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("顧客情報を変更する", html);
        Assert.Contains("共有先の支店を選んでください", decodedHtml);
        Assert.Contains("validation-summary-errors", html);
        Assert.Contains("field-validation-error", html);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.False(await dbContext.AuditEntries.AnyAsync(item => item.AggregateId == partyId));
    }

    [Fact]
    public async Task MaximumPageNumberReturnsBadRequestInsteadOfOverflowing()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        Guid branchId = await GetBranchIdAsync(application, "sales.rep@fieldops.demo");
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);

        using HttpResponseMessage response = await client.GetAsync(
            $"/parties?branchId={branchId}&page={int.MaxValue}&pageSize=100");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("page is outside the supported range", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateAndStaleShareReturnDeterministicEditUiWithoutAudit()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        (Guid partyId, Guid branchId, Guid targetBranchId, _) = await SeedEditablePartyAsync(application);
        await SharePartyOutOfBandAsync(application, partyId, targetBranchId);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
        uint currentVersion = await GetPartyVersionAsync(application, partyId);
        string token = await GetAntiforgeryTokenAsync(client, $"/parties/{partyId}/edit?branchId={branchId}");

        using HttpResponseMessage duplicate = await client.PostAsync(
            $"/parties/{partyId}/share",
            SharePartyForm(branchId, targetBranchId, currentVersion, token));
        string duplicateHtml = await duplicate.Content.ReadAsStringAsync();
        string decodedDuplicateHtml = WebUtility.HtmlDecode(duplicateHtml);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("この支店にはすでに共有されています", decodedDuplicateHtml);

        token = await GetAntiforgeryTokenAsync(client, $"/parties/{partyId}/edit?branchId={branchId}");
        uint staleVersion = await GetPartyVersionAsync(application, partyId);
        await RenamePartyOutOfBandAsync(application, partyId);
        uint refreshedVersion = await GetPartyVersionAsync(application, partyId);
        using HttpResponseMessage stale = await client.PostAsync(
            $"/parties/{partyId}/share",
            SharePartyForm(branchId, targetBranchId, staleVersion, token));
        string staleHtml = await stale.Content.ReadAsStringAsync();
        string decodedStaleHtml = WebUtility.HtmlDecode(staleHtml);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("ほかの利用者が先に更新しました", decodedStaleHtml);
        Assert.Equal(refreshedVersion.ToString(), GetInputValue(staleHtml, "Version"));

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.False(await dbContext.AuditEntries.AnyAsync(item => item.AggregateId == partyId));
    }

    [Fact]
    public async Task SearchFindsAssignedPartiesByNormalizedNameContactAndSite()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        Guid branchId = await SeedSearchPartiesAsync(application);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);

        foreach ((string term, string expected, string excluded) in new[]
        {
            ("  FICTIONAL NORTHWIND  ", "Fictional Northwind Services", "Fictional Alpine Works"),
            ("mika stone", "Fictional Northwind Services", "Fictional Alpine Works"),
            ("harbor pump", "Fictional Northwind Services", "Fictional Alpine Works")
        })
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"/parties?branchId={branchId}&search={Uri.EscapeDataString(term)}");
            string html = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(expected, html);
            Assert.DoesNotContain(excluded, html);
        }

        using HttpResponseMessage foreignSiteSearch = await client.GetAsync(
            $"/parties?branchId={branchId}&search=remote%20restricted");
        string foreignSiteHtml = await foreignSiteSearch.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, foreignSiteSearch.StatusCode);
        Assert.DoesNotContain("Fictional Northwind Services", foreignSiteHtml);
        Assert.Contains("条件に合う顧客・協力会社はまだありません", WebUtility.HtmlDecode(foreignSiteHtml));
    }

    [Fact]
    public async Task RoleTabsUsePartyRolesAndPreserveBoundedStablePaging()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        Guid branchId = await SeedRolePartiesAsync(application);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);

        using HttpResponseMessage customerPage = await client.GetAsync(
            $"/customers?branchId={branchId}&search=Fictional&page=2&pageSize=1");
        string customerHtml = await customerPage.Content.ReadAsStringAsync();
        using HttpResponseMessage partnerPage = await client.GetAsync(
            $"/business-partners?branchId={branchId}&pageSize=500");
        string partnerHtml = await partnerPage.Content.ReadAsStringAsync();
        string decodedPartnerHtml = WebUtility.HtmlDecode(partnerHtml);
        using HttpResponseMessage allPartiesPage = await client.GetAsync(
            $"/parties?branchId={branchId}&pageSize=500");
        string allPartiesHtml = await allPartiesPage.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, customerPage.StatusCode);
        Assert.Contains("顧客を探す", customerHtml);
        Assert.Contains("顧客名・担当者名・現場名で検索", customerHtml);
        Assert.Contains("顧客の情報を見る", customerHtml);
        Assert.Contains("d-none d-xl-block", customerHtml);
        Assert.Contains("d-xl-none", customerHtml);
        string visibleCustomerHtml = Regex.Replace(customerHtml, "data-policy=\"[^\"]*\"", string.Empty);
        Assert.DoesNotContain("Parties", visibleCustomerHtml);
        Assert.DoesNotContain("Business partner", visibleCustomerHtml);
        Assert.Contains("Fictional Bravo Customer", customerHtml);
        Assert.DoesNotContain("Fictional Alpha Customer", customerHtml);
        Assert.Contains("search=Fictional", customerHtml);
        Assert.Equal(HttpStatusCode.OK, partnerPage.StatusCode);
        Assert.Contains("Fictional Dual Role", partnerHtml);
        Assert.Contains("pageSize=100", partnerHtml);
        Assert.Contains("d-none d-xl-block", partnerHtml);
        Assert.Contains("d-xl-none", partnerHtml);
        Assert.Contains($"/parties/create?branchId={branchId}&role=BusinessPartner", decodedPartnerHtml);

        Assert.Equal(HttpStatusCode.OK, allPartiesPage.StatusCode);
        string decodedAllPartiesHtml = WebUtility.HtmlDecode(allPartiesHtml);
        Assert.Contains("この画面では顧客と協力会社を探し、詳しい情報を確認できます。", decodedAllPartiesHtml);
        Assert.Contains("取引先名・担当者名・現場名で検索", decodedAllPartiesHtml);
        Assert.DoesNotContain("この画面では顧客を探し、詳しい情報を確認できます。", decodedAllPartiesHtml);
        Assert.Contains("d-none d-xl-block", allPartiesHtml);
        Assert.Contains("d-xl-none", allPartiesHtml);
    }

    [Fact]
    public async Task CreatePersistsPartyContactSiteAndOneRedactedSuccessAudit()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid branchId = await GetBranchIdAsync(application, "sales.rep@fieldops.demo");
        string token = await GetAntiforgeryTokenAsync(client, $"/parties/create?branchId={branchId}");

        using HttpResponseMessage response = await client.PostAsync(
            "/parties/create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["BranchId"] = branchId.ToString(),
                ["OrganizationName"] = "Fictional Cedar Inspection",
                ["RoleType"] = PartyRoleType.Customer.ToString(),
                ["ContactFirstName"] = "Robin",
                ["ContactLastName"] = "Vale",
                ["SiteName"] = "Cedar Water Facility",
                ["__RequestVerificationToken"] = token
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/parties/", response.Headers.Location?.OriginalString);
        using HttpResponseMessage details = await client.GetAsync(response.Headers.Location);
        string detailsHtml = await details.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        Assert.Contains("Fictional Cedar Inspection", detailsHtml);
        Assert.Contains("Robin Vale", detailsHtml);
        Assert.Contains("Cedar Water Facility", detailsHtml);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Party party = await dbContext.Parties.SingleAsync(item => item.OrganizationName == "Fictional Cedar Inspection");
        AuditEntry audit = await dbContext.AuditEntries.SingleAsync(item => item.AggregateId == party.Id);
        Assert.Equal(branchId, audit.BranchId);
        Assert.Equal("Created", audit.Action);
        Assert.Equal("Success", audit.Outcome);
        Assert.Contains("ContactFirstName", audit.ChangeSummary);
        Assert.DoesNotContain("Robin", audit.ChangeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cedar Water Facility", audit.ChangeSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BusinessPartnerCreateRouteDefaultsAndPersistsBusinessPartnerRole()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid branchId = await GetBranchIdAsync(application, "sales.rep@fieldops.demo");

        using HttpResponseMessage createPage = await client.GetAsync(
            $"/parties/create?branchId={branchId}&role=BusinessPartner");
        string createHtml = await createPage.Content.ReadAsStringAsync();
        string decodedCreateHtml = WebUtility.HtmlDecode(createHtml);

        Assert.Equal(HttpStatusCode.OK, createPage.StatusCode);
        Assert.Contains("協力会社を登録する", decodedCreateHtml);
        Assert.Contains("この内容で協力会社を登録する", decodedCreateHtml);
        Assert.Contains("value=\"BusinessPartner\" selected", createHtml);

        string token = GetAntiforgeryToken(createHtml);
        using HttpResponseMessage response = await client.PostAsync(
            "/parties/create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["BranchId"] = branchId.ToString(),
                ["OrganizationName"] = "Fictional Partner Route",
                ["RoleType"] = PartyRoleType.BusinessPartner.ToString(),
                ["__RequestVerificationToken"] = token
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Party party = await dbContext.Parties.Include(item => item.Roles)
            .SingleAsync(item => item.OrganizationName == "Fictional Partner Route");
        Assert.DoesNotContain(party.Roles, role => role.RoleType == PartyRoleType.Customer);
        Assert.Contains(party.Roles, role => role.RoleType == PartyRoleType.BusinessPartner);
    }

    [Fact]
    public async Task InvalidCreateRoleQueryFallsBackToNeutralCreatePageWithoutBinderError()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid branchId = await GetBranchIdAsync(application, "sales.rep@fieldops.demo");

        using HttpResponseMessage createPage = await client.GetAsync(
            $"/parties/create?branchId={branchId}&role=NotARole");
        string createHtml = await createPage.Content.ReadAsStringAsync();
        string decodedCreateHtml = WebUtility.HtmlDecode(createHtml);

        Assert.Equal(HttpStatusCode.OK, createPage.StatusCode);
        Assert.Contains("<h1>顧客・協力会社を登録する</h1>", decodedCreateHtml);
        Assert.Contains("この内容で登録する", decodedCreateHtml);
        Assert.DoesNotContain("value=\"Customer\" selected", createHtml);
        Assert.DoesNotContain("value=\"BusinessPartner\" selected", createHtml);
        Assert.DoesNotContain("The value 'NotARole' is not valid", decodedCreateHtml);
        Assert.DoesNotContain("is not valid for", decodedCreateHtml);
        Assert.DoesNotContain("The field", decodedCreateHtml);
    }

    [Fact]
    public async Task UpdateAddsSecondRoleAndShareReusesTheSamePartyAcrossBranches()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        (Guid partyId, Guid sourceBranchId, Guid targetBranchId, uint version) = await SeedEditablePartyAsync(application);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SystemAdministrator);
        string token = await GetAntiforgeryTokenAsync(
            client,
            $"/parties/{partyId}/edit?branchId={sourceBranchId}");

        using HttpResponseMessage update = await client.PostAsync(
            $"/parties/{partyId}/edit",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = partyId.ToString(),
                ["BranchId"] = sourceBranchId.ToString(),
                ["Version"] = version.ToString(),
                ["OrganizationName"] = "Fictional Meridian Services Updated",
                ["IsCustomer"] = "true",
                ["IsBusinessPartner"] = "true",
                ["__RequestVerificationToken"] = token
            }));
        Assert.Equal(HttpStatusCode.Redirect, update.StatusCode);

        uint updatedVersion = await GetPartyVersionAsync(application, partyId);
        token = await GetAntiforgeryTokenAsync(client, $"/parties/{partyId}/edit?branchId={sourceBranchId}");
        using HttpResponseMessage share = await client.PostAsync(
            $"/parties/{partyId}/share",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["BranchId"] = sourceBranchId.ToString(),
                ["TargetBranchId"] = targetBranchId.ToString(),
                ["Version"] = updatedVersion.ToString(),
                ["__RequestVerificationToken"] = token
            }));
        Assert.Equal(HttpStatusCode.Redirect, share.StatusCode);

        using HttpResponseMessage targetDetails = await client.GetAsync(
            $"/parties/{partyId}?branchId={targetBranchId}");
        string html = await targetDetails.Content.ReadAsStringAsync();
        string decodedHtml = WebUtility.HtmlDecode(html);
        Assert.Equal(HttpStatusCode.OK, targetDetails.StatusCode);
        Assert.Contains("Fictional Meridian Services Updated", html);
        Assert.Contains(">顧客<", decodedHtml);
        Assert.Contains(">協力会社<", decodedHtml);

        uint sharedVersion = await GetPartyVersionAsync(application, partyId);
        token = await GetAntiforgeryTokenAsync(client, $"/parties/{partyId}/edit?branchId={sourceBranchId}");
        using HttpResponseMessage removal = await client.PostAsync(
            $"/parties/{partyId}/edit",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = partyId.ToString(),
                ["BranchId"] = sourceBranchId.ToString(),
                ["Version"] = sharedVersion.ToString(),
                ["OrganizationName"] = "Fictional Meridian Services Updated",
                ["IsCustomer"] = "false",
                ["IsBusinessPartner"] = "true",
                ["__RequestVerificationToken"] = token
            }));
        Assert.Equal(HttpStatusCode.BadRequest, removal.StatusCode);
        Assert.Contains("すでに登録済みの区分はこの画面では外せません", WebUtility.HtmlDecode(await removal.Content.ReadAsStringAsync()));

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(2, await dbContext.AuditEntries.CountAsync(item => item.AggregateId == partyId));
        Assert.Equal(2, await dbContext.Parties
            .Where(item => item.Id == partyId)
            .Select(item => item.BranchAssignments.Count)
            .SingleAsync());
    }

    [Fact]
    public async Task DuplicateAndInvalidCreateReturnDeterministicUiWithoutAudit()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        (Guid partyId, Guid branchId, _, _) = await SeedEditablePartyAsync(application);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        string token = await GetAntiforgeryTokenAsync(client, $"/parties/create?branchId={branchId}");

        using HttpResponseMessage duplicate = await client.PostAsync(
            "/parties/create",
            CreatePartyForm(branchId, "  FICTIONAL MERIDIAN SERVICES  ", token));
        string duplicateHtml = await duplicate.Content.ReadAsStringAsync();
        string decodedDuplicateHtml = WebUtility.HtmlDecode(duplicateHtml);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("同じ組織名がすでに登録されています", decodedDuplicateHtml);

        token = await GetAntiforgeryTokenAsync(client, $"/parties/create?branchId={branchId}");
        using HttpResponseMessage invalid = await client.PostAsync(
            "/parties/create",
            CreatePartyForm(branchId, string.Empty, token));
        string invalidHtml = await invalid.Content.ReadAsStringAsync();
        string decodedInvalidHtml = WebUtility.HtmlDecode(invalidHtml);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("組織名を入力してください", decodedInvalidHtml);

        token = await GetAntiforgeryTokenAsync(client, $"/parties/create?branchId={branchId}");
        using HttpResponseMessage invalidRole = await client.PostAsync(
            "/parties/create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["BranchId"] = branchId.ToString(),
                ["OrganizationName"] = "Fictional Invalid Role",
                ["RoleType"] = "999",
                ["__RequestVerificationToken"] = token
            }));
        string invalidRoleHtml = await invalidRole.Content.ReadAsStringAsync();
        string decodedInvalidRoleHtml = WebUtility.HtmlDecode(invalidRoleHtml);
        Assert.Equal(HttpStatusCode.BadRequest, invalidRole.StatusCode);
        Assert.Contains("顧客または協力会社を選んでください", decodedInvalidRoleHtml);

        token = await GetAntiforgeryTokenAsync(client, $"/parties/create?branchId={branchId}");
        using HttpResponseMessage missingRole = await client.PostAsync(
            "/parties/create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["BranchId"] = branchId.ToString(),
                ["OrganizationName"] = "Fictional Missing Role",
                ["__RequestVerificationToken"] = token
            }));
        string decodedMissingRoleHtml = WebUtility.HtmlDecode(await missingRole.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, missingRole.StatusCode);
        Assert.Contains("顧客または協力会社を選んでください", decodedMissingRoleHtml);

        token = await GetAntiforgeryTokenAsync(client, $"/parties/create?branchId={branchId}");
        using HttpResponseMessage emptyRole = await client.PostAsync(
            "/parties/create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["BranchId"] = branchId.ToString(),
                ["OrganizationName"] = "Fictional Empty Role",
                ["RoleType"] = string.Empty,
                ["__RequestVerificationToken"] = token
            }));
        string decodedEmptyRoleHtml = WebUtility.HtmlDecode(await emptyRole.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, emptyRole.StatusCode);
        Assert.Contains("顧客または協力会社を選んでください", decodedEmptyRoleHtml);

        token = await GetAntiforgeryTokenAsync(client, $"/parties/create?branchId={branchId}");
        string tooLongName = new('長', 201);
        using HttpResponseMessage tooLong = await client.PostAsync(
            "/parties/create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["BranchId"] = branchId.ToString(),
                ["OrganizationName"] = tooLongName,
                ["RoleType"] = PartyRoleType.Customer.ToString(),
                ["ContactFirstName"] = new('名', 101),
                ["ContactLastName"] = new('姓', 101),
                ["SiteName"] = new('現', 201),
                ["__RequestVerificationToken"] = token
            }));
        string decodedTooLongHtml = WebUtility.HtmlDecode(await tooLong.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("組織名は200文字以内で入力してください", decodedTooLongHtml);
        Assert.Contains("担当者の名は100文字以内で入力してください", decodedTooLongHtml);
        Assert.Contains("担当者の姓は100文字以内で入力してください", decodedTooLongHtml);
        Assert.Contains("現場名は200文字以内で入力してください", decodedTooLongHtml);
        Assert.DoesNotContain("The field", decodedTooLongHtml);
        Assert.DoesNotContain("must be a string", decodedTooLongHtml);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.Equal(1, await dbContext.Parties.CountAsync());
        Assert.False(await dbContext.AuditEntries.AnyAsync(item => item.AggregateId == partyId));
    }

    [Fact]
    public async Task StaleVersionReturnsConflictAndDoesNotWriteAudit()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        (Guid partyId, Guid branchId, _, uint staleVersion) = await SeedEditablePartyAsync(application);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        string token = await GetAntiforgeryTokenAsync(client, $"/parties/{partyId}/edit?branchId={branchId}");
        await RenamePartyOutOfBandAsync(application, partyId);

        using HttpResponseMessage response = await client.PostAsync(
            $"/parties/{partyId}/edit",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = partyId.ToString(),
                ["BranchId"] = branchId.ToString(),
                ["Version"] = staleVersion.ToString(),
                ["OrganizationName"] = "Fictional Stale Browser Value",
                ["IsCustomer"] = "true",
                ["__RequestVerificationToken"] = token
            }));
        string html = await response.Content.ReadAsStringAsync();
        string decodedHtml = WebUtility.HtmlDecode(html);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("ほかの利用者が先に更新しました", decodedHtml);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Assert.False(await dbContext.AuditEntries.AnyAsync(item => item.AggregateId == partyId));
    }

    [Fact]
    public async Task DirectCrossBranchUrlsAndWritesReturnForbidden()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        (Guid partyId, _, Guid foreignBranchId, _) = await SeedEditablePartyAsync(application);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);
        Guid ownBranchId = await GetBranchIdAsync(application, "sales.rep@fieldops.demo");
        string token = await GetAntiforgeryTokenAsync(client, $"/parties/create?branchId={ownBranchId}");

        using HttpResponseMessage index = await client.GetAsync($"/parties?branchId={foreignBranchId}");
        using HttpResponseMessage details = await client.GetAsync(
            $"/parties/{partyId}?branchId={foreignBranchId}");
        using HttpResponseMessage create = await client.PostAsync(
            "/parties/create",
            CreatePartyForm(foreignBranchId, "Fictional Forbidden Party", token));

        Assert.Equal(HttpStatusCode.Forbidden, index.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, details.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task BranchScopedNavigationDefaultsToTheAuthenticatedUsersBranch()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        Guid reassignedBranchId = await ReassignSalesUserToFieldBranchAsync(application);
        using HttpClient client = CreateClient(application);
        await LoginAsAsync(client, DemoRoleNames.SalesRepresentative);

        using HttpResponseMessage response = await client.GetAsync("/parties");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains($"branchId={reassignedBranchId}", response.Headers.Location?.OriginalString);
    }

    private static HttpClient CreateClient(FieldOpsWebApplicationFactory application) =>
        application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task<Guid> SeedSearchPartiesAsync(FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        ApplicationUser user = await dbContext.Users.SingleAsync(
            item => item.UserName == "sales.rep@fieldops.demo");
        Guid branchId = Assert.IsType<Guid>(user.BranchId);
        Branch branch = await dbContext.Branches.SingleAsync(item => item.Id == branchId);
        Branch foreignBranch = await dbContext.Branches.SingleAsync(item => item.Id != branchId);

        Party northwind = Party.CreateOrganization("Fictional Northwind Services");
        northwind.AddRole(PartyRoleType.Customer);
        northwind.AssignToBranch(branch);
        northwind.AddContact("Mika", "Stone", true);
        northwind.AddSite(branch, "Harbor Pump Station");
        northwind.AssignToBranch(foreignBranch);
        northwind.AddSite(foreignBranch, "Remote Restricted Depot");

        Party alpine = Party.CreateOrganization("Fictional Alpine Works");
        alpine.AddRole(PartyRoleType.BusinessPartner);
        alpine.AssignToBranch(branch);
        alpine.AddContact("Avery", "Quill", true);
        alpine.AddSite(branch, "Hill Service Yard");

        dbContext.Parties.AddRange(northwind, alpine);
        await dbContext.SaveChangesAsync();
        return branchId;
    }

    private static async Task<Guid> SeedRolePartiesAsync(FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        ApplicationUser user = await dbContext.Users.SingleAsync(
            item => item.UserName == "sales.rep@fieldops.demo");
        Guid branchId = Assert.IsType<Guid>(user.BranchId);
        Branch branch = await dbContext.Branches.SingleAsync(item => item.Id == branchId);

        Party alpha = Party.CreateOrganization("Fictional Alpha Customer");
        alpha.AddRole(PartyRoleType.Customer);
        alpha.AssignToBranch(branch);
        Party bravo = Party.CreateOrganization("Fictional Bravo Customer");
        bravo.AddRole(PartyRoleType.Customer);
        bravo.AssignToBranch(branch);
        Party dual = Party.CreateOrganization("Fictional Dual Role");
        dual.AddRole(PartyRoleType.Customer);
        dual.AddRole(PartyRoleType.BusinessPartner);
        dual.AssignToBranch(branch);

        dbContext.Parties.AddRange(alpha, bravo, dual);
        await dbContext.SaveChangesAsync();
        return branchId;
    }

    private static async Task<(Guid PartyId, Guid SourceBranchId, Guid TargetBranchId, uint Version)> SeedEditablePartyAsync(
        FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Branch[] branches = await dbContext.Branches.OrderBy(item => item.Name).ToArrayAsync();
        Party party = Party.CreateOrganization("Fictional Meridian Services");
        party.AddRole(PartyRoleType.Customer);
        party.AssignToBranch(branches[0]);
        dbContext.Parties.Add(party);
        await dbContext.SaveChangesAsync();
        return (party.Id, branches[0].Id, branches[1].Id, party.Version);
    }

    private static async Task<uint> GetPartyVersionAsync(
        FieldOpsWebApplicationFactory application,
        Guid partyId)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        return await dbContext.Parties.Where(party => party.Id == partyId).Select(party => party.Version).SingleAsync();
    }

    private static async Task RenamePartyOutOfBandAsync(
        FieldOpsWebApplicationFactory application,
        Guid partyId)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Party party = await dbContext.Parties.SingleAsync(item => item.Id == partyId);
        party.UpdateOrganizationName("Fictional Concurrent Value");
        await dbContext.SaveChangesAsync();
    }

    private static async Task<string> GetDatabaseUpperAsync(
        FieldOpsWebApplicationFactory application,
        string value)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        return await dbContext.Database
            .SqlQuery<string>($"SELECT upper({value}) AS \"Value\"")
            .SingleAsync();
    }

    private static async Task SharePartyOutOfBandAsync(
        FieldOpsWebApplicationFactory application,
        Guid partyId,
        Guid targetBranchId)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Party party = await dbContext.Parties
            .Include(item => item.BranchAssignments)
            .SingleAsync(item => item.Id == partyId);
        Branch targetBranch = await dbContext.Branches.SingleAsync(branch => branch.Id == targetBranchId);
        party.AssignToBranch(targetBranch);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<(string SourceName, string TargetName)> GetBranchNamesAsync(
        FieldOpsWebApplicationFactory application,
        Guid sourceBranchId,
        Guid targetBranchId)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        Dictionary<Guid, string> names = await dbContext.Branches
            .Where(branch => branch.Id == sourceBranchId || branch.Id == targetBranchId)
            .ToDictionaryAsync(branch => branch.Id, branch => branch.Name);
        return (names[sourceBranchId], names[targetBranchId]);
    }

    private static async Task InstallPartyWriteDelayAsync(FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE OR REPLACE FUNCTION fieldops_test_delay_party_write()
            RETURNS trigger AS $function$
            BEGIN
                PERFORM pg_sleep(1.5);
                RETURN NEW;
            END;
            $function$ LANGUAGE plpgsql;

            CREATE TRIGGER fieldops_test_delay_party_write
            BEFORE INSERT OR UPDATE OF "OrganizationName" ON "Parties"
            FOR EACH ROW EXECUTE FUNCTION fieldops_test_delay_party_write();
            """);
    }

    private static async Task<Guid> ReassignSalesUserToFieldBranchAsync(
        FieldOpsWebApplicationFactory application)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        ApplicationUser user = await dbContext.Users.SingleAsync(
            item => item.UserName == "sales.rep@fieldops.demo");
        Guid branchId = await dbContext.Branches
            .Where(branch => branch.Id != user.BranchId)
            .Select(branch => branch.Id)
            .SingleAsync();
        user.BranchId = branchId;
        await dbContext.SaveChangesAsync();
        return branchId;
    }

    private static async Task LoginAsAsync(HttpClient client, string role)
    {
        using HttpResponseMessage page = await client.GetAsync("/demo-login");
        string html = await page.Content.ReadAsStringAsync();
        string token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
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

    private static async Task<Guid> GetBranchIdAsync(
        FieldOpsWebApplicationFactory application,
        string userName)
    {
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        return Assert.IsType<Guid>((await dbContext.Users.SingleAsync(user => user.UserName == userName)).BranchId);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        using HttpResponseMessage page = await client.GetAsync(path);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        string token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    private static string GetAntiforgeryToken(string html)
    {
        string token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    private static FormUrlEncodedContent CreatePartyForm(Guid branchId, string organizationName, string token) =>
        new(new Dictionary<string, string>
        {
            ["BranchId"] = branchId.ToString(),
            ["OrganizationName"] = organizationName,
            ["RoleType"] = PartyRoleType.Customer.ToString(),
            ["__RequestVerificationToken"] = token
        });

    private static FormUrlEncodedContent SharePartyForm(
        Guid branchId,
        Guid targetBranchId,
        uint version,
        string token) =>
        new(new Dictionary<string, string>
        {
            ["BranchId"] = branchId.ToString(),
            ["TargetBranchId"] = targetBranchId.ToString(),
            ["Version"] = version.ToString(),
            ["__RequestVerificationToken"] = token
        });

    private static string GetInputValue(string html, string id) =>
        Regex.Match(
            html,
            $"<input(?=[^>]*id=\"{Regex.Escape(id)}\")(?=[^>]*value=\"([^\"]*)\")[^>]*>",
            RegexOptions.IgnoreCase).Groups[1].Value;
}