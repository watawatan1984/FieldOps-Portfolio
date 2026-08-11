using System.Security.Cryptography;

using FieldOps.Infrastructure.Identity;
using FieldOps.Web.Models;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[AllowAnonymous]
[Route("demo-login")]
public sealed class DemoLoginController(IDataProtectionProvider dataProtectionProvider) : Controller
{
    private readonly IDataProtector _roleProtector = dataProtectionProvider.CreateProtector("FieldOps.DemoLogin.Role.v1");

    [HttpGet]
    public IActionResult Index() => View(new[]
    {
        CreateCard(DemoRoleNames.SystemAdministrator, "Alex Morgan", "Full demo administration and audit access."),
        CreateCard(DemoRoleNames.BranchManager, "Jordan Lee", "Manage one fictional operating branch."),
        CreateCard(DemoRoleNames.SalesRepresentative, "Casey Rivera", "Manage customers and sales for one fictional branch."),
        CreateCard(DemoRoleNames.FieldTechnician, "Taylor Kim", "Work with assigned fictional field activities.")
    });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(
        [FromForm] string roleToken,
        [FromServices] UserManager<ApplicationUser> userManager,
        [FromServices] SignInManager<ApplicationUser> signInManager)
    {
        string role;
        try
        {
            role = _roleProtector.Unprotect(roleToken);
        }
        catch (CryptographicException)
        {
            return BadRequest();
        }

        if (!DemoIdentitySeeder.TryGetUserName(role, out string userName))
        {
            return BadRequest();
        }

        ApplicationUser? user = await userManager.FindByNameAsync(userName);
        if (user is null || !await userManager.IsInRoleAsync(user, role))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await signInManager.SignOutAsync();
        await signInManager.SignInAsync(user, isPersistent: false);
        return RedirectToAction("Index", "Home");
    }

    private DemoRoleCardViewModel CreateCard(string role, string displayName, string description) =>
        new(role, displayName, description, _roleProtector.Protect(role));
}
