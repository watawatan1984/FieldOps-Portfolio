using System.Security.Claims;

using FieldOps.Features.Abstractions;

namespace FieldOps.Web.Services;

public sealed class HttpCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private const string Anonymous = "anonymous";

    public string UserId => httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Anonymous;

    public string Role => httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Role) ?? Anonymous;
}