using System.Security.Cryptography;

using FieldOps.Domain.Entities;
using FieldOps.Infrastructure.Persistence;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Infrastructure.Identity;

public sealed class DemoIdentitySeeder(
    FieldOpsDbContext dbContext,
    RoleManager<IdentityRole> roleManager,
    UserManager<ApplicationUser> userManager)
{
    private const string CentralBranchName = "Fictional Central Service Branch";
    private const string FieldBranchName = "Fictional Field Service Branch";

    private static readonly IReadOnlyDictionary<string, DemoAccount> Accounts =
        new Dictionary<string, DemoAccount>(StringComparer.Ordinal)
        {
            [DemoRoleNames.SystemAdministrator] = new("system.admin@fieldops.demo", "Alex Morgan", null),
            [DemoRoleNames.BranchManager] = new("branch.manager@fieldops.demo", "Jordan Lee", CentralBranchName),
            [DemoRoleNames.SalesRepresentative] = new("sales.rep@fieldops.demo", "Casey Rivera", CentralBranchName),
            [DemoRoleNames.FieldTechnician] = new("field.tech@fieldops.demo", "Taylor Kim", FieldBranchName)
        };

    public static bool TryGetUserName(string role, out string userName)
    {
        if (Accounts.TryGetValue(role, out DemoAccount? account))
        {
            userName = account.UserName;
            return true;
        }

        userName = string.Empty;
        return false;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, Branch> branches = await EnsureBranchesAsync(cancellationToken);

        foreach (string role in DemoRoleNames.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roleManager.CreateAsync(new IdentityRole(role)));
            }

            DemoAccount account = Accounts[role];
            ApplicationUser? user = await userManager.FindByNameAsync(account.UserName);
            if (user is null)
            {
                user = new ApplicationUser
                {
                    UserName = account.UserName,
                    Email = account.UserName,
                    EmailConfirmed = true,
                    DisplayName = account.DisplayName,
                    BranchId = account.BranchName is null ? null : branches[account.BranchName].Id
                };

                EnsureSucceeded(await userManager.CreateAsync(user, GenerateStrongPassword()));
            }

            if (!await userManager.IsInRoleAsync(user, role))
            {
                EnsureSucceeded(await userManager.AddToRoleAsync(user, role));
            }
        }
    }

    private async Task<Dictionary<string, Branch>> EnsureBranchesAsync(CancellationToken cancellationToken)
    {
        string[] names = [CentralBranchName, FieldBranchName];
        List<Branch> existing = await dbContext.Branches
            .Where(branch => names.Contains(branch.Name))
            .ToListAsync(cancellationToken);

        foreach (string name in names.Except(existing.Select(branch => branch.Name), StringComparer.Ordinal))
        {
            Branch branch = Branch.Create(name);
            dbContext.Branches.Add(branch);
            existing.Add(branch);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return existing.ToDictionary(branch => branch.Name, StringComparer.Ordinal);
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

    private sealed record DemoAccount(string UserName, string DisplayName, string? BranchName);
}
