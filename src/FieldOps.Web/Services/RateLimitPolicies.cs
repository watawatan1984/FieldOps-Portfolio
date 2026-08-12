using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.RateLimiting;

namespace FieldOps.Web.Services;

public static class RateLimitPolicies
{
    public const string DemoLogin = "demo-login";
    public const string DemoReset = "demo-reset";

    private static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ResetWindow = TimeSpan.FromMinutes(10);

    public static void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = WriteSafeRejectionAsync;
        options.AddPolicy(DemoLogin, context => FixedWindow(
            $"login:{NormalizeClientIp(context)}",
            permitLimit: 20,
            LoginWindow));
        options.AddPolicy(DemoReset, context => FixedWindow(
            GetResetPartition(context),
            permitLimit: 3,
            ResetWindow));
    }

    private static RateLimitPartition<string> FixedWindow(string partitionKey, int permitLimit, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = permitLimit,
                QueueLimit = 0,
                Window = window
            });

    private static string GetResetPartition(HttpContext context)
    {
        string? userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return !string.IsNullOrWhiteSpace(userId)
            ? $"reset-user:{userId}"
            : $"reset-anonymous:{NormalizeClientIp(context)}";
    }

    private static string NormalizeClientIp(HttpContext context)
    {
        System.Net.IPAddress? address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "unknown";
        }

        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : address.ToString().ToLowerInvariant();
    }

    private static async ValueTask WriteSafeRejectionAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        TimeSpan retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfterMetadata)
            ? retryAfterMetadata
            : TimeSpan.FromSeconds(1);
        int retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { correlationId = context.HttpContext.TraceIdentifier },
            cancellationToken);
    }
}