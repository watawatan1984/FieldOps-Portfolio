using System.Globalization;

using FieldOps.Features.Abstractions;
using FieldOps.Features.Administration;
using FieldOps.Features.Dashboard;
using FieldOps.Features.Parties;
using FieldOps.Features.Sales;
using FieldOps.Features.Work;
using FieldOps.Infrastructure;
using FieldOps.Infrastructure.Demo;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.Web.Authorization;
using FieldOps.Web.Controllers;
using FieldOps.Web.Logging;
using FieldOps.Web.Middleware;
using FieldOps.Web.Services;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

if (args.Length == 1 && string.Equals(args[0], "--health-check", StringComparison.Ordinal))
{
    string port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
    using HttpClient client = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    try
    {
        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/health/live");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        Environment.ExitCode = 1;
    }
    catch (TaskCanceledException)
    {
        Environment.ExitCode = 1;
    }

    return;
}

var builder = WebApplication.CreateBuilder(args);
CultureInfo japaneseCulture = CultureInfo.GetCultureInfo("ja-JP");

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = RedactedJsonConsoleFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<RedactedJsonConsoleFormatter, ConsoleFormatterOptions>(options =>
    options.IncludeScopes = true);

bool mapsLoadTestSurface =
    builder.Environment.IsDevelopment() ||
    string.Equals(builder.Environment.EnvironmentName, "LoadTest", StringComparison.Ordinal);

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
{
    if (!mapsLoadTestSurface)
    {
        options.Conventions.Add(new SuppressControllerConvention(typeof(LoadTestController)));
    }

    options.ModelBindingMessageProvider.SetAttemptedValueIsInvalidAccessor(
        (value, fieldName) => $"{fieldName}の値「{value}」は無効です。");
    options.ModelBindingMessageProvider.SetMissingBindRequiredValueAccessor(
        fieldName => $"{fieldName}は必須です。");
    options.ModelBindingMessageProvider.SetMissingKeyOrValueAccessor(
        () => "必須の値が入力されていません。");
    options.ModelBindingMessageProvider.SetMissingRequestBodyRequiredValueAccessor(
        () => "リクエスト本文は必須です。");
    options.ModelBindingMessageProvider.SetNonPropertyAttemptedValueIsInvalidAccessor(
        value => $"値「{value}」は無効です。");
    options.ModelBindingMessageProvider.SetNonPropertyUnknownValueIsInvalidAccessor(
        () => "入力値は無効です。");
    options.ModelBindingMessageProvider.SetNonPropertyValueMustBeANumberAccessor(
        () => "数値を入力してください。");
    options.ModelBindingMessageProvider.SetUnknownValueIsInvalidAccessor(
        fieldName => $"{fieldName}の入力値は無効です。");
    options.ModelBindingMessageProvider.SetValueIsInvalidAccessor(
        value => $"値「{value}」は無効です。");
    options.ModelBindingMessageProvider.SetValueMustBeANumberAccessor(
        fieldName => $"{fieldName}には数値を入力してください。");
    options.ModelBindingMessageProvider.SetValueMustNotBeNullAccessor(
        fieldName => $"{fieldName}は必須です。");
});
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(japaneseCulture);
    options.SupportedCultures = [japaneseCulture];
    options.SupportedUICultures = [japaneseCulture];
});
builder.Services.AddRateLimiter(RateLimitPolicies.Configure);
builder.Services.AddOptions<TrustedProxyOptions>()
    .Bind(builder.Configuration.GetSection(TrustedProxyOptions.SectionName))
    .Validate(TrustedProxyOptions.HasValidForwardLimit, "Trusted proxy ForwardLimit must be between 1 and 5.")
    .Validate(TrustedProxyOptions.HasValidProxies, "Every trusted proxy must be an IP address.")
    .Validate(TrustedProxyOptions.HasValidNetworks, "Every trusted network must be in CIDR notation.")
    .ValidateOnStart();
builder.Services.AddOptions<DemoModeOptions>()
    .Bind(builder.Configuration.GetSection(DemoModeOptions.SectionName))
    .Validate(
        options => options.HasApprovedDatasetConfiguration,
        "Enabled demo mode requires the exact approved dataset identifier and version.")
    .ValidateOnStart();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<DemoResetIntentProtector>();
builder.Services.AddSingleton<DemoResetCompletionProtector>();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<DashboardQueries>();
builder.Services.AddScoped<DashboardPageModelFactory>();
builder.Services.AddScoped<BranchProgressQueries>();
builder.Services.AddScoped<AuditQueries>();
builder.Services.AddScoped<PartyQueries>();
builder.Services.AddScoped<PartyCommands>();
builder.Services.AddScoped<SalesQueries>();
builder.Services.AddScoped<SalesCommands>();
builder.Services.AddScoped<WorkOrderCommands>();
builder.Services.AddScoped<WorkOrderQueries>();
builder.Services.AddScoped<WorkHistorySearch>();
builder.Services.AddFieldOpsAuthorization();
builder.Services.AddFieldOpsInfrastructure(
    builder.Configuration.GetConnectionString("FieldOps") ??
    "Host=localhost;Database=fieldops;Username=fieldops");
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/demo-login";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    options.SlidingExpiration = true;
    options.Events = new CookieAuthenticationEvents
    {
        OnRedirectToLogin = context =>
        {
            context.Response.Redirect(options.LoginPath);
            return Task.CompletedTask;
        },
        OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddHealthChecks()
    .AddCheck("process", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<PostgresReadinessHealthCheck>("postgresql", tags: ["ready"]);

var app = builder.Build();

TrustedProxyOptions trustedProxyOptions = app.Services.GetRequiredService<IOptions<TrustedProxyOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<DemoModeOptions>>().Value;

await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
{
    FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
    await dbContext.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<IDemoModeVerifier>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<DemoIdentitySeeder>().SeedAsync();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (trustedProxyOptions.HasTrustedSources)
{
    ForwardedHeadersOptions forwardedHeaders = new()
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = trustedProxyOptions.ForwardLimit
    };
    forwardedHeaders.KnownProxies.Clear();
    forwardedHeaders.KnownIPNetworks.Clear();
    foreach (string address in trustedProxyOptions.KnownProxies)
    {
        forwardedHeaders.KnownProxies.Add(System.Net.IPAddress.Parse(address));
    }

    foreach (string network in trustedProxyOptions.KnownNetworks)
    {
        forwardedHeaders.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }

    app.UseForwardedHeaders(forwardedHeaders);
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

bool edgeTerminatesTls = builder.Configuration.GetValue<bool>("Hosting:EdgeTerminatesTls");
if (!edgeTerminatesTls)
{
    app.UseHttpsRedirection();
}
if (!mapsLoadTestSurface)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/__load-test"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    });
}

app.UseRequestLocalization();

app.UseRouting();

app.UseAuthentication();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    AllowStatusCode404Response = true,
    SuppressDiagnosticsCallback = _ => true,
    ExceptionHandler = async context =>
    {
        Exception exception = context.Features.Get<IExceptionHandlerFeature>()?.Error
            ?? new InvalidOperationException("An exception was not available to the handler.");
        SafeExceptionClassification classification = SafeExceptionClassifier.Classify(exception);
        ILogger safeExceptionLogger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("FieldOps.Web.Diagnostics.SafeException");
        safeExceptionLogger.Log(
            classification.StatusCode == StatusCodes.Status500InternalServerError ? LogLevel.Error : LogLevel.Warning,
            "Request exception classified as {ExceptionCategory} with safe type {ExceptionType}",
            classification.Category,
            classification.SafeType);
        context.Response.StatusCode = classification.StatusCode;
        if (AcceptsHtml(context.Request))
        {
            await SafeHtmlErrorResponse.WriteAsync(context, classification.StatusCode, context.TraceIdentifier);
            return;
        }

        await context.Response.WriteAsJsonAsync(new { correlationId = context.TraceIdentifier });
    }
});
app.UseStatusCodePagesWithReExecute("/status/{0}");
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
}).AllowAnonymous();

app.MapStaticAssets().AllowAnonymous();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


static bool AcceptsHtml(HttpRequest request) =>
    request.Headers.Accept.Any(value => value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);

app.Run();

public partial class Program;

internal sealed class SuppressControllerConvention(Type controllerType) : Microsoft.AspNetCore.Mvc.ApplicationModels.IControllerModelConvention
{
    public void Apply(Microsoft.AspNetCore.Mvc.ApplicationModels.ControllerModel controller)
    {
        if (controller.ControllerType.AsType() == controllerType)
        {
            controller.Actions.Clear();
        }
    }
}