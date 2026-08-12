using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;

namespace FieldOps.Web.Services;

public sealed class DemoResetCompletionProtector(
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider)
{
    public const string Purpose = "FieldOps.DemoReset.Completion.v1";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(Purpose);

    public string Protect(string userId, string correlationId)
    {
        DateTimeOffset issuedAtUtc = timeProvider.GetUtcNow();
        return _protector.Protect(JsonSerializer.Serialize(new CompletionPayload(
            userId,
            correlationId,
            issuedAtUtc,
            issuedAtUtc.Add(Lifetime))));
    }

    public bool TryGetCorrelationId(string? token, string userId, out string correlationId)
    {
        correlationId = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            CompletionPayload? payload = JsonSerializer.Deserialize<CompletionPayload>(_protector.Unprotect(token));
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (payload is null ||
                !string.Equals(payload.UserId, userId, StringComparison.Ordinal) ||
                payload.IssuedAtUtc > now ||
                payload.ExpiresAtUtc < now ||
                payload.ExpiresAtUtc - payload.IssuedAtUtc != Lifetime)
            {
                return false;
            }

            correlationId = payload.CorrelationId;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return false;
        }
    }

    private sealed record CompletionPayload(
        string UserId,
        string CorrelationId,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}