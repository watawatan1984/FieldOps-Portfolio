using System.Security.Claims;

using FieldOps.Infrastructure.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
    IOptions<IdentityOptions> options,
    FieldOpsDbContext dbContext)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    public const string BranchIdClaimType = "fieldops:branch_id";
    public const string BranchNameClaimType = "fieldops:branch_name";
    public const string DisplayNameClaimType = "fieldops:display_name";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        ClaimsIdentity identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(DisplayNameClaimType, user.DisplayName));
        if (user.BranchId is Guid branchId)
        {
            identity.AddClaim(new Claim(BranchIdClaimType, branchId.ToString()));
            string branchName = await dbContext.Branches
                .Where(branch => branch.Id == branchId)
                .Select(branch => branch.Name)
                .SingleAsync();
            identity.AddClaim(new Claim(BranchNameClaimType, branchName));
        }
        else
        {
            identity.AddClaim(new Claim(BranchNameClaimType, "全支店"));
        }

        return identity;
    }
}