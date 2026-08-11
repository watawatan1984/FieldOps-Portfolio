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
        await next(context);

        int statusCode = context.Response.StatusCode;
        logger.LogInformation(
            "HTTP operation {Operation} completed with {Outcome}; correlation {CorrelationId}; user {UserId}; role {Role}; route {Route}; status {StatusCode}; elapsed {ElapsedMs} ms",
            "http.request",
            statusCode < StatusCodes.Status400BadRequest ? "success" : "failure",
            context.TraceIdentifier,
            currentUser.UserId,
            currentUser.Role,
            context.Request.Path.Value ?? "/",
            statusCode,
            stopwatch.ElapsedMilliseconds);
    }
}