using System.Net;
using System.Text.RegularExpressions;

using FieldOps.Domain.Entities;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;
using FieldOps.Web.Authorization;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.IntegrationTests.Authorization;

[Collection(DatabaseCollection.Name)]
public sealed class AuthorizationPolicyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task UnauthenticatedDashboardRequestRedirectsToDemoLogin()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using WebApplicationFactory<Program> application = new FieldOpsWebApplicationFactory(connectionString);
        using HttpClient client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/demo-login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task UnauthenticatedUnadornedEndpointIsDeniedWhileLoginAndAssetsRemainPublic()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        using HttpResponseMessage unadorned = await client.GetAsync("/authorization-probe/unadorned");
        using HttpResponseMessage login = await client.GetAsync("/demo-login");
        using HttpResponseMessage asset = await client.GetAsync("/css/site.css");

        Assert.Equal(HttpStatusCode.Redirect, unadorned.StatusCode);
        Assert.Equal("/demo-login", unadorned.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
    }

    [Fact]
    public async Task BranchManagerDirectResetRequestReturnsForbidden()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsAsync(client, "Branch Manager");

        using HttpResponseMessage response = await client.GetAsync("/authorization-probe/reset");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DirectUrlPolicyMatrixIsEnforced()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        (string Role, string[] AllowedPaths)[] matrix =
        [
            ("System Administrator", ["dashboard", "parties", "sales", "work-orders", "audit", "reset"]),
            ("Branch Manager", ["dashboard", "parties", "sales", "work-orders", "audit"]),
            ("Sales Representative", ["dashboard", "parties", "sales", "work-orders"]),
            ("Field Technician", ["dashboard", "sales", "work-orders"])
        ];
        string[] paths = ["dashboard", "parties", "sales", "work-orders", "audit", "reset"];

        foreach ((string role, string[] allowedPaths) in matrix)
        {
            using HttpClient client = application.CreateClient(new()
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            await LoginAsAsync(client, role);

            foreach (string path in paths)
            {
                using HttpResponseMessage response = await client.GetAsync($"/authorization-probe/{path}");
                HttpStatusCode expected = allowedPaths.Contains(path, StringComparer.Ordinal)
                    ? HttpStatusCode.OK
                    : HttpStatusCode.Forbidden;
                Assert.True(
                    response.StatusCode == expected,
                    $"{role} requesting {path}: expected {(int)expected}, received {(int)response.StatusCode}.");
            }
        }
    }

    [Fact]
    public async Task HiddenNavigationMatchesPolicyMatrix()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        (string Role, string[] VisiblePolicies)[] matrix =
        [
            ("System Administrator", ["ViewDashboard", "ManageParties", "ReadSales", "ReadWorkOrders", "ViewAudit", "ResetDemo"]),
            ("Branch Manager", ["ViewDashboard", "ManageParties", "ReadSales", "ReadWorkOrders", "ViewAudit"]),
            ("Sales Representative", ["ViewDashboard", "ManageParties", "ReadSales", "ReadWorkOrders"]),
            ("Field Technician", ["ViewDashboard", "ReadSales", "ReadWorkOrders"])
        ];
        string[] policies = ["ViewDashboard", "ManageParties", "ReadSales", "ReadWorkOrders", "ViewAudit", "ResetDemo"];

        foreach ((string role, string[] visiblePolicies) in matrix)
        {
            using HttpClient client = application.CreateClient(new()
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            await LoginAsAsync(client, role);
            string html = await client.GetStringAsync("/");

            foreach (string policy in policies)
            {
                string marker = $"data-policy=\"{policy}\"";
                Assert.True(
                    html.Contains(marker, StringComparison.Ordinal) == visiblePolicies.Contains(policy, StringComparer.Ordinal),
                    $"{role} navigation visibility for {policy} did not match the policy matrix.");
            }
        }
    }

    [Fact]
    public async Task DirectActionPoliciesDoNotElevateReadOrAssignedAccessToManagement()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        (string Role, string Path, HttpStatusCode Expected)[] cases =
        [
            (DemoRoleNames.SystemAdministrator, "sales/manage", HttpStatusCode.OK),
            (DemoRoleNames.BranchManager, "work-orders/manage", HttpStatusCode.OK),
            (DemoRoleNames.SalesRepresentative, "sales/manage", HttpStatusCode.OK),
            (DemoRoleNames.SalesRepresentative, "work-orders/manage", HttpStatusCode.Forbidden),
            (DemoRoleNames.SalesRepresentative, "work-orders/update", HttpStatusCode.Forbidden),
            (DemoRoleNames.FieldTechnician, "sales/manage", HttpStatusCode.Forbidden),
            (DemoRoleNames.FieldTechnician, "work-orders/manage", HttpStatusCode.Forbidden),
            (DemoRoleNames.FieldTechnician, "work-orders/update", HttpStatusCode.OK)
        ];

        foreach ((string role, string path, HttpStatusCode expected) in cases)
        {
            using HttpClient client = application.CreateClient(new()
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            await LoginAsAsync(client, role);
            using HttpResponseMessage response = await client.GetAsync($"/authorization-probe/{path}");
            Assert.Equal(expected, response.StatusCode);
        }
    }

    [Fact]
    public async Task LoadedResourceControlsBranchAccessAndPostedBranchIdIsIgnored()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsAsync(client, DemoRoleNames.BranchManager);

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        ApplicationUser manager = await dbContext.Users.SingleAsync(user => user.UserName == "branch.manager@fieldops.demo");
        Guid ownBranchId = Assert.IsType<Guid>(manager.BranchId);
        Guid foreignBranchId = await dbContext.Branches
            .Where(branch => branch.Id != ownBranchId)
            .Select(branch => branch.Id)
            .SingleAsync();

        using HttpResponseMessage ownResource = await client.GetAsync(
            $"/authorization-probe/resource/{BranchResourceAction.ManageParties}/{ownBranchId}?BranchId={foreignBranchId}");
        using HttpResponseMessage foreignResource = await client.GetAsync(
            $"/authorization-probe/resource/{BranchResourceAction.ManageParties}/{foreignBranchId}?BranchId={ownBranchId}");

        Assert.Equal(HttpStatusCode.OK, ownResource.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignResource.StatusCode);
    }

    [Fact]
    public async Task ResourcePolicyMatrixEnforcesBranchAndAssignmentScope()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        ResourceIds resources = await CreateAssignedResourcesAsync(dbContext);
        (string Role, BranchResourceAction Action, string ResourcePath, HttpStatusCode Expected)[] cases =
        [
            .. BuildResourceCases(DemoRoleNames.SystemAdministrator, resources, [true, true, true, true, true, true, true, true]),
            .. BuildResourceCases(DemoRoleNames.BranchManager, resources, [true, true, true, true, true, true, true, true]),
            .. BuildResourceCases(DemoRoleNames.SalesRepresentative, resources, [true, true, true, true, true, false, false, false]),
            .. BuildResourceCases(DemoRoleNames.FieldTechnician, resources, [true, false, true, false, true, false, true, false])
        ];

        foreach (IGrouping<string, (string Role, BranchResourceAction Action, string ResourcePath, HttpStatusCode Expected)> roleCases in
            cases.GroupBy(testCase => testCase.Role, StringComparer.Ordinal))
        {
            using HttpClient client = application.CreateClient(new()
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            await LoginAsAsync(client, roleCases.Key);
            foreach ((string role, BranchResourceAction action, string resourcePath, HttpStatusCode expected) in roleCases)
            {
                using HttpResponseMessage response = await client.GetAsync(
                    $"/authorization-probe/{resourcePath}");
                Assert.True(
                    response.StatusCode == expected,
                    $"{role} requesting {action}: expected {(int)expected}, received {(int)response.StatusCode}.");
            }
        }

        using HttpClient technicianClient = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsAsync(technicianClient, DemoRoleNames.FieldTechnician);
        using HttpResponseMessage unassigned = await technicianClient.GetAsync(
            $"/authorization-probe/work-order/{BranchResourceAction.UpdateWorkOrders}/{resources.UnassignedFieldWorkOrderId}");
        Assert.Equal(HttpStatusCode.Forbidden, unassigned.StatusCode);

        using HttpClient salesClient = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        await LoginAsAsync(salesClient, DemoRoleNames.SalesRepresentative);
        using HttpResponseMessage foreignSales = await salesClient.GetAsync(
            $"/authorization-probe/sales-opportunity/{BranchResourceAction.ReadSales}/{resources.FieldSalesOpportunityId}");
        using HttpResponseMessage missing = await salesClient.GetAsync(
            $"/authorization-probe/sales-opportunity/{BranchResourceAction.ReadSales}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, foreignSales.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static IEnumerable<(string Role, BranchResourceAction Action, string ResourcePath, HttpStatusCode Expected)> BuildResourceCases(
        string role,
        ResourceIds resources,
        bool[] allowed)
    {
        bool technician = role == DemoRoleNames.FieldTechnician;
        string branch = $"resource/{{0}}/{(technician ? resources.FieldBranchId : resources.CentralBranchId)}";
        string sales = $"sales-opportunity/{{0}}/{(technician ? resources.FieldSalesOpportunityId : resources.CentralSalesOpportunityId)}";
        string work = $"work-order/{{0}}/{(technician ? resources.FieldWorkOrderId : resources.CentralWorkOrderId)}";
        (BranchResourceAction Action, string Path)[] cells =
        [
            (BranchResourceAction.ViewDashboard, work),
            (BranchResourceAction.ManageParties, branch),
            (BranchResourceAction.ReadSales, sales),
            (BranchResourceAction.ManageSales, sales),
            (BranchResourceAction.ReadWorkOrders, work),
            (BranchResourceAction.ManageWorkOrders, work),
            (BranchResourceAction.UpdateWorkOrders, work),
            (BranchResourceAction.ViewAudit, branch)
        ];

        return cells.Select((cell, index) => (
            role,
            cell.Action,
            string.Format(System.Globalization.CultureInfo.InvariantCulture, cell.Path, cell.Action),
            allowed[index] ? HttpStatusCode.OK : HttpStatusCode.Forbidden));
    }

    private static async Task<ResourceIds> CreateAssignedResourcesAsync(FieldOpsDbContext dbContext)
    {
        ApplicationUser salesUser = await dbContext.Users.SingleAsync(user => user.UserName == "sales.rep@fieldops.demo");
        ApplicationUser technician = await dbContext.Users.SingleAsync(user => user.UserName == "field.tech@fieldops.demo");
        Guid centralBranchId = Assert.IsType<Guid>(salesUser.BranchId);
        Guid fieldBranchId = Assert.IsType<Guid>(technician.BranchId);
        Branch centralBranch = await dbContext.Branches.SingleAsync(branch => branch.Id == centralBranchId);
        Branch fieldBranch = await dbContext.Branches.SingleAsync(branch => branch.Id == fieldBranchId);

        Party centralParty = Party.CreateOrganization("Fictional Central Authorization Customer");
        centralParty.AssignToBranch(centralBranch);
        centralParty.AddSite(centralBranch, "Fictional Central Authorization Site");
        (SalesOpportunity centralSales, WorkOrder centralWork) = TestWorkOrderFactory.CreateFromWon(
            centralBranch, centralParty, centralParty.Sites.Single());
        centralSales.AssignOwner(salesUser.Id);

        Party fieldParty = Party.CreateOrganization("Fictional Field Authorization Customer");
        fieldParty.AssignToBranch(fieldBranch);
        fieldParty.AddSite(fieldBranch, "Fictional Field Authorization Site");
        (SalesOpportunity fieldSales, WorkOrder fieldWork) = TestWorkOrderFactory.CreateFromWon(
            fieldBranch, fieldParty, fieldParty.Sites.Single());
        fieldSales.AssignToUser(technician.Id);
        fieldWork.AssignToUser(technician.Id);
        (SalesOpportunity unassignedFieldSales, WorkOrder unassignedFieldWork) = TestWorkOrderFactory.CreateFromWon(
            fieldBranch, fieldParty, fieldParty.Sites.Single());

        dbContext.AddRange(
            centralParty, centralSales, centralWork,
            fieldParty, fieldSales, fieldWork, unassignedFieldSales, unassignedFieldWork);
        await dbContext.SaveChangesAsync();
        return new ResourceIds(
            centralBranchId,
            fieldBranchId,
            centralSales.Id,
            fieldSales.Id,
            centralWork.Id,
            fieldWork.Id,
            unassignedFieldWork.Id);
    }

    private sealed record ResourceIds(
        Guid CentralBranchId,
        Guid FieldBranchId,
        Guid CentralSalesOpportunityId,
        Guid FieldSalesOpportunityId,
        Guid CentralWorkOrderId,
        Guid FieldWorkOrderId,
        Guid UnassignedFieldWorkOrderId);

    private static async Task LoginAsAsync(HttpClient client, string role)
    {
        using HttpResponseMessage page = await client.GetAsync("/demo-login");
        string html = await page.Content.ReadAsStringAsync();
        string token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        string roleToken = Regex.Match(
            html,
            $"<h2 class=\"h5\">{Regex.Escape(role)}</h2>.*?name=\"roleToken\" value=\"([^\"]+)\"",
            RegexOptions.Singleline).Groups[1].Value;
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

}