using System.Net;
using System.Text.RegularExpressions;

using FieldOps.IntegrationTests.Infrastructure;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
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
        Assert.Equal("/demo-login", response.Headers.Location?.AbsolutePath);
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
        Dictionary<string, ApplicationUser> users = await dbContext.Users
            .ToDictionaryAsync(user => user.UserName!);
        Guid centralBranchId = Assert.IsType<Guid>(users["branch.manager@fieldops.demo"].BranchId);
        Guid fieldBranchId = Assert.IsType<Guid>(users["field.tech@fieldops.demo"].BranchId);
        (string Role, BranchResourceAction Action, Guid BranchId, HttpStatusCode Expected)[] cases =
        [
            (DemoRoleNames.SystemAdministrator, BranchResourceAction.ViewAudit, fieldBranchId, HttpStatusCode.OK),
            (DemoRoleNames.BranchManager, BranchResourceAction.ManageSales, centralBranchId, HttpStatusCode.OK),
            (DemoRoleNames.BranchManager, BranchResourceAction.ViewAudit, fieldBranchId, HttpStatusCode.Forbidden),
            (DemoRoleNames.SalesRepresentative, BranchResourceAction.ManageParties, centralBranchId, HttpStatusCode.OK),
            (DemoRoleNames.SalesRepresentative, BranchResourceAction.ReadWorkOrders, centralBranchId, HttpStatusCode.OK),
            (DemoRoleNames.SalesRepresentative, BranchResourceAction.UpdateWorkOrders, centralBranchId, HttpStatusCode.Forbidden),
            (DemoRoleNames.FieldTechnician, BranchResourceAction.ViewDashboard, fieldBranchId, HttpStatusCode.OK),
            (DemoRoleNames.FieldTechnician, BranchResourceAction.ReadSales, fieldBranchId, HttpStatusCode.OK),
            (DemoRoleNames.FieldTechnician, BranchResourceAction.UpdateWorkOrders, fieldBranchId, HttpStatusCode.OK),
            (DemoRoleNames.FieldTechnician, BranchResourceAction.ManageSales, fieldBranchId, HttpStatusCode.Forbidden),
            (DemoRoleNames.FieldTechnician, BranchResourceAction.UpdateWorkOrders, centralBranchId, HttpStatusCode.Forbidden)
        ];

        foreach ((string role, BranchResourceAction action, Guid branchId, HttpStatusCode expected) in cases)
        {
            using HttpClient client = application.CreateClient(new()
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            await LoginAsAsync(client, role);
            using HttpResponseMessage response = await client.GetAsync(
                $"/authorization-probe/resource/{action}/{branchId}");
            Assert.True(
                response.StatusCode == expected,
                $"{role} requesting {action} for {branchId}: expected {(int)expected}, received {(int)response.StatusCode}.");
        }
    }

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
