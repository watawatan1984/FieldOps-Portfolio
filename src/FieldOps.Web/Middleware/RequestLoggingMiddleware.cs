using System.Diagnostics;

using FieldOps.Features.Abstractions;

namespace FieldOps.Web.Middleware;

public sealed class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string route = GetSafeRouteIdentifier(context);
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = context.TraceIdentifier,
            ["UserId"] = currentUser.UserId,
            ["Role"] = currentUser.Role,
            ["Route"] = route
        });

        await next(context);

        int statusCode = context.Response.StatusCode;
        logger.LogInformation(
            "HTTP operation {Operation} completed with {Outcome}; correlation {CorrelationId}; user {UserId}; role {Role}; route {Route}; status {StatusCode}; elapsed {ElapsedMs} ms",
            "http.request",
            statusCode < StatusCodes.Status400BadRequest ? "success" : "failure",
            context.TraceIdentifier,
            currentUser.UserId,
            currentUser.Role,
            route,
            statusCode,
            stopwatch.ElapsedMilliseconds);
    }

    private static string GetSafeRouteIdentifier(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint routeEndpoint)
        {
            return "unmatched";
        }

        string? routeTemplate = routeEndpoint.RoutePattern.RawText;
        return !string.IsNullOrWhiteSpace(routeTemplate) && routeTemplate.Length <= 256
            ? routeTemplate
            : "matched";
    }
}