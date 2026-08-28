using System.Net;
using System.Text.RegularExpressions;

using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.IntegrationTests.Infrastructure;
using FieldOps.Web.Controllers;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Npgsql;

namespace FieldOps.IntegrationTests.Authorization;

[Collection(DatabaseCollection.Name)]
public sealed class DemoLoginTests(PostgresFixture postgres)
{
    [Fact]
    public async Task DisabledDemoModeHidesTheConvenienceLogin()
    {
        string connectionString = Task12ConnectionString(await postgres.CreateEmptyDatabaseAsync());
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configuration: new Dictionary<string, string?>
            {
                ["DemoMode:Enabled"] = "false",
                ["DemoMode:DatasetIdentifier"] = null,
                ["DemoMode:DatasetVersion"] = null
            });
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        using HttpResponseMessage response = await client.GetAsync("/demo-login");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EnabledDemoModeWithAnUnapprovedDatasetConfigurationFailsStartup()
    {
        string connectionString = Task12ConnectionString(await postgres.CreateEmptyDatabaseAsync());
        await using FieldOpsWebApplicationFactory application = new(
            connectionString,
            configuration: new Dictionary<string, string?>
            {
                ["DemoMode:Enabled"] = "true",
                ["DemoMode:DatasetIdentifier"] = "unapproved-dataset",
                ["DemoMode:DatasetVersion"] = "1"
            });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(application.CreateClient);

        Assert.Contains("exact approved dataset identifier and version", exception.Message, StringComparison.Ordinal);
    }

    private static string Task12ConnectionString(string connectionString)
    {
        return new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
    }

    [Fact]
    public async Task LoginPageOffersExactlyFourPublicRolesWithoutPasswordInput()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/demo-login");
        string html = await response.Content.ReadAsStringAsync();
        string decodedHtml = WebUtility.HtmlDecode(html);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("担当する仕事を選んでください", html, StringComparison.Ordinal);
        Assert.Contains("システム管理者", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("支店管理者", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("営業担当者", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("現場担当者", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("架空のデモデータ", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Continue as", html, StringComparison.Ordinal);
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

    [Fact]
    public async Task RoleChoiceTokenHasPurposeSpecificFiveMinuteLifetime()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        using HttpClient client = application.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost")
        });
        string html = await client.GetStringAsync("/demo-login");
        string token = GetRoleToken(html, DemoRoleNames.SystemAdministrator);
        ITimeLimitedDataProtector protector = application.Services
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("FieldOps.DemoLogin.Role.v2")
            .ToTimeLimitedDataProtector();

        string role = protector.Unprotect(token, out DateTimeOffset expiration);

        Assert.Equal(DemoRoleNames.SystemAdministrator, role);
        Assert.InRange(expiration - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ExpiredAndWrongPurposeRoleTokensAreRejectedWithoutWaiting()
    {
        string connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using FieldOpsWebApplicationFactory application = new(connectionString);
        IDataProtectionProvider provider = application.Services.GetRequiredService<IDataProtectionProvider>();
        string expired = provider
            .CreateProtector(DemoLoginController.RoleTokenPurpose)
            .ToTimeLimitedDataProtector()
            .Protect(DemoRoleNames.SystemAdministrator, DateTimeOffset.UtcNow.AddMinutes(-1));
        string wrongPurpose = provider
            .CreateProtector("FieldOps.DemoLogin.WrongPurpose")
            .ToTimeLimitedDataProtector()
            .Protect(DemoRoleNames.SystemAdministrator, TimeSpan.FromMinutes(5));

        Assert.Equal(HttpStatusCode.BadRequest, await PostRoleTokenAsync(application, expired));
        Assert.Equal(HttpStatusCode.BadRequest, await PostRoleTokenAsync(application, wrongPurpose));
    }

    private static string GetRoleToken(string html, string role)
    {
        string token = Regex.Match(
            html,
            $"data-role=\"{Regex.Escape(role)}\".*?name=\"roleToken\" value=\"([^\"]+)\"",
            RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    private static async Task<HttpStatusCode> PostRoleTokenAsync(
        FieldOpsWebApplicationFactory application,
        string roleToken)
    {
        using HttpClient client = application.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        string html = await client.GetStringAsync("/demo-login");
        string requestToken = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        using HttpResponseMessage response = await client.PostAsync(
            "/demo-login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleToken"] = roleToken,
                ["__RequestVerificationToken"] = requestToken
            }));
        return response.StatusCode;
    }
}