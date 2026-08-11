using System.Text.RegularExpressions;

namespace FieldOps.Web.Middleware;

public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId = TryGetValidIncomingId(context, out string? incoming)
            ? incoming
            : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        await next(context);
    }

    private static bool TryGetValidIncomingId(HttpContext context, out string correlationId)
    {
        correlationId = string.Empty;
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values) || values.Count != 1)
        {
            return false;
        }

        string? value = values[0];
        if (value is null || !ValidCorrelationId().IsMatch(value))
        {
            return false;
        }

        correlationId = value;
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidCorrelationId();
}