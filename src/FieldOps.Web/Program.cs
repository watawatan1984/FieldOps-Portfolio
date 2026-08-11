using FieldOps.Domain.Common;
using FieldOps.Features.Abstractions;
using FieldOps.Infrastructure;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;
using FieldOps.Web.Authorization;
using FieldOps.Web.Middleware;
using FieldOps.Web.Services;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

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
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    AllowStatusCode404Response = true,
    SuppressDiagnosticsCallback = _ => true,
    ExceptionHandler = async context =>
    {
        Exception exception = context.Features.Get<IExceptionHandlerFeature>()?.Error
            ?? new InvalidOperationException("An exception was not available to the handler.");
        context.Response.StatusCode = exception switch
        {
            DomainException => StatusCodes.Status400BadRequest,
            DbUpdateConcurrencyException => StatusCodes.Status409Conflict,
            UnauthorizedAccessException => StatusCodes.Status403Forbidden,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status500InternalServerError
        };
        await context.Response.WriteAsJsonAsync(new { correlationId = context.TraceIdentifier });
    }
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
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