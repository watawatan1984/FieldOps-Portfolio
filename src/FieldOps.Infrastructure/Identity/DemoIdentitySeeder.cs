using System.Security.Cryptography;

using FieldOps.Domain.Entities;
using FieldOps.Infrastructure.Demo;
using FieldOps.Infrastructure.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Infrastructure.Identity;

public sealed class DemoIdentitySeeder(
    FieldOpsDbContext dbContext,
    RoleManager<IdentityRole> roleManager,
    UserManager<ApplicationUser> userManager,
    IDemoModeVerifier demoModeVerifier)
{
    public static bool TryGetUserName(string role, out string userName)
    {
        if (DemoDataManifest.UsersByRole.TryGetValue(role, out DemoUser? account))
        {
            userName = account.UserName;
            return true;
        }

        userName = string.Empty;
        return false;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!await demoModeVerifier.IsApprovedAsync(cancellationToken))
        {
            return;
        }

        await EnsureBranchesAsync(cancellationToken);

        foreach (string role in DemoRoleNames.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roleManager.CreateAsync(new IdentityRole(role)
                {
                    Id = DemoDataManifest.RoleIds[role],
                    ConcurrencyStamp = $"role-{DemoDataManifest.RoleIds[role]}"
                }));
            }

            DemoUser account = DemoDataManifest.UsersByRole[role];
            ApplicationUser? user = await userManager.FindByNameAsync(account.UserName);
            if (user is null)
            {
                user = new ApplicationUser
                {
                    Id = account.Id,
                    UserName = account.UserName,
                    Email = account.UserName,
                    EmailConfirmed = true,
                    DisplayName = account.DisplayName,
                    BranchId = account.BranchId,
                    SecurityStamp = account.SecurityStamp,
                    ConcurrencyStamp = account.ConcurrencyStamp
                };

                EnsureSucceeded(await userManager.CreateAsync(user, GenerateStrongPassword()));
            }

            if (!await userManager.IsInRoleAsync(user, role))
            {
                EnsureSucceeded(await userManager.AddToRoleAsync(user, role));
            }
        }
    }

    private async Task EnsureBranchesAsync(CancellationToken cancellationToken)
    {
        DemoBranch[] startupBranches = DemoDataManifest.Branches.Take(2).ToArray();
        string[] names = startupBranches.Select(branch => branch.Name).ToArray();
        List<Branch> existing = await dbContext.Branches
            .Where(branch => names.Contains(branch.Name))
            .ToListAsync(cancellationToken);

        foreach (DemoBranch branch in startupBranches.Where(candidate =>
            existing.All(current => current.Name != candidate.Name)))
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "Branches" ("Id", "Name", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES ({branch.Id}, {branch.Name}, {DemoDataManifest.EpochUtc}, {DemoDataManifest.EpochUtc})
                """,
                cancellationToken);
        }
    }

    private static string GenerateStrongPassword() =>
        $"Aa1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Demo identity seeding failed: {string.Join("; ", result.Errors.Select(error => error.Code))}");
        }
    }
}