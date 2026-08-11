using System.Net;
using System.Text.RegularExpressions;

using FieldOps.IntegrationTests.Infrastructure;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FieldOps.IntegrationTests.Authorization;

[Collection(DatabaseCollection.Name)]
public sealed class DemoLoginTests(PostgresFixture postgres)
{
    [Fact]
    public async Task LoginPageOffersExactlyFourPublicRolesWithoutPasswordInput()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/demo-login");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("System Administrator", html, StringComparison.Ordinal);
        Assert.Contains("Branch Manager", html, StringComparison.Ordinal);
        Assert.Contains("Sales Representative", html, StringComparison.Ordinal);
        Assert.Contains("Field Technician", html, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"password\"", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneClickLoginUsesServerSideIdentityAndIssuesHardenedCookie()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        using HttpResponseMessage loginPage = await client.GetAsync("/demo-login");
        string html = await loginPage.Content.ReadAsStringAsync();
        string requestToken = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(requestToken);
        string roleToken = GetRoleToken(html, DemoRoleNames.SystemAdministrator);

        using HttpResponseMessage response = await client.PostAsync(
            "/demo-login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = roleToken,
                ["__RequestVerificationToken"] = requestToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal) &&
            !value.StartsWith(".AspNetCore.Identity.Application=;", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        using HttpResponseMessage dashboard = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    [Fact]
    public async Task StartupSeedsExactlyFourStableDemoAccountsAndHardenedCookieOptions()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        _ = application.CreateClient();

        await using AsyncServiceScope scope = application.Services.CreateAsyncScope();
        FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
        List<ApplicationUser> users = await dbContext.Users.OrderBy(user => user.UserName).ToListAsync();
        List<string> roles = await dbContext.Roles.Select(role => role.Name!).OrderBy(role => role).ToListAsync();
        CookieAuthenticationOptions cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        Assert.Equal(4, users.Count);
        Assert.All(users, user => Assert.False(string.IsNullOrWhiteSpace(user.PasswordHash)));
        Assert.Equal(DemoRoleNames.All.Order(), roles);
        Assert.Equal(TimeSpan.FromMinutes(30), cookieOptions.ExpireTimeSpan);
        Assert.True(cookieOptions.SlidingExpiration);
        Assert.True(cookieOptions.Cookie.HttpOnly);
        Assert.Equal(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always, cookieOptions.Cookie.SecurePolicy);
        Assert.Equal(Microsoft.AspNetCore.Http.SameSiteMode.Strict, cookieOptions.Cookie.SameSite);
    }

    [Fact]
    public async Task TamperedRoleChoiceIsRejectedWithoutAuthenticationCookie()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        using HttpResponseMessage loginPage = await client.GetAsync("/demo-login");
        string html = await loginPage.Content.ReadAsStringAsync();
        string requestToken = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

        string roleToken = GetRoleToken(html, DemoRoleNames.SystemAdministrator);
        char replacement = roleToken[^1] == 'A' ? 'B' : 'A';
        string tamperedRoleToken = roleToken[..^1] + replacement;

        using HttpResponseMessage response = await client.PostAsync(
            "/demo-login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = tamperedRoleToken,
                ["__RequestVerificationToken"] = requestToken
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values) ? values : [],
            value => value.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal) &&
                !value.StartsWith(".AspNetCore.Identity.Application=;", StringComparison.Ordinal));
    }

    private static string GetRoleToken(string html, string role)
    {
        string token = Regex.Match(
            html,
            $"<h2 class=\"h5\">{Regex.Escape(role)}</h2>.*?name=\"roleToken\" value=\"([^\"]+)\"",
            RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }
}
