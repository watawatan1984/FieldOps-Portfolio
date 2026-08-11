using System.Security.Claims;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FieldOps.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser
{
    public required string DisplayName { get; set; }

    public Guid? BranchId { get; set; }
}

public static class DemoRoleNames
{
    public const string SystemAdministrator = "System Administrator";
    public const string BranchManager = "Branch Manager";
    public const string SalesRepresentative = "Sales Representative";
    public const string FieldTechnician = "Field Technician";

    public static IReadOnlyList<string> All { get; } =
    [
        SystemAdministrator,
        BranchManager,
        SalesRepresentative,
        FieldTechnician
    ];
}

public sealed class DemoUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    public const string BranchIdClaimType = "fieldops:branch_id";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        ClaimsIdentity identity = await base.GenerateClaimsAsync(user);
        if (user.BranchId is Guid branchId)
        {
            identity.AddClaim(new Claim(BranchIdClaimType, branchId.ToString()));
        }

        return identity;
    }
}
