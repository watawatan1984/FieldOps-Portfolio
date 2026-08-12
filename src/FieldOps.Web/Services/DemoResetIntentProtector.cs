using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;

namespace FieldOps.Web.Services;

public sealed class DemoResetIntentProtector(
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider)
{
    public const string Purpose = "FieldOps.DemoReset.Intent.v1";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(Purpose);

    public string Protect(string userId, string idempotencyKey)
    {
        DateTimeOffset issuedAtUtc = timeProvider.GetUtcNow();
        ResetIntentPayload payload = new(
            userId,
            idempotencyKey,
            issuedAtUtc,
            issuedAtUtc.Add(Lifetime));
        return _protector.Protect(JsonSerializer.Serialize(payload));
    }

    public bool IsValid(string? token, string userId, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            ResetIntentPayload? payload = JsonSerializer.Deserialize<ResetIntentPayload>(_protector.Unprotect(token));
            DateTimeOffset now = timeProvider.GetUtcNow();
            return payload is not null &&
                string.Equals(payload.UserId, userId, StringComparison.Ordinal) &&
                string.Equals(payload.IdempotencyKey, idempotencyKey, StringComparison.Ordinal) &&
                payload.IssuedAtUtc <= now &&
                payload.ExpiresAtUtc >= now &&
                payload.ExpiresAtUtc - payload.IssuedAtUtc == Lifetime;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return false;
        }
    }

    private sealed record ResetIntentPayload(
        string UserId,
        string IdempotencyKey,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}