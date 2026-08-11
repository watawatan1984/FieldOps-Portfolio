using FieldOps.Domain.Common;
using FieldOps.Features.Abstractions;
using FieldOps.Infrastructure;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.Web.Authorization;
using FieldOps.Web.Logging;
using FieldOps.Web.Middleware;
using FieldOps.Web.Services;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = RedactedJsonConsoleFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<RedactedJsonConsoleFormatter, ConsoleFormatterOptions>(options =>
    options.IncludeScopes = true);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
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

await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
{
    FieldOpsDbContext dbContext = scope.ServiceProvider.GetRequiredService<FieldOpsDbContext>();
    await dbContext.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<DemoIdentitySeeder>().SeedAsync();
}

app.UseMiddleware<CorrelationIdMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
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
        (int statusCode, string category, string safeType) = exception switch
        {
            DomainException => (StatusCodes.Status400BadRequest, "domain", nameof(DomainException)),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "concurrency", nameof(DbUpdateConcurrencyException)),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "authorization", nameof(UnauthorizedAccessException)),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "not_found", nameof(KeyNotFoundException)),
            _ => (StatusCodes.Status500InternalServerError, "unexpected", "UnhandledException")
        };
        ILogger safeExceptionLogger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("FieldOps.Web.Diagnostics.SafeException");
        safeExceptionLogger.Log(
            statusCode == StatusCodes.Status500InternalServerError ? LogLevel.Error : LogLevel.Warning,
            "Request exception classified as {ExceptionCategory} with safe type {ExceptionType}",
            category,
            safeType);
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { correlationId = context.TraceIdentifier });
    }
});
app.UseAuthorization();

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


app.Run();

public partial class Program;