using System.Security.Cryptography;

using FieldOps.Infrastructure.Demo;
using FieldOps.Infrastructure.Identity;
using FieldOps.Web.Formatting;
using FieldOps.Web.Models;
using FieldOps.Web.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FieldOps.Web.Controllers;

[AllowAnonymous]
[Route("demo-login")]
public sealed class DemoLoginController(
    IDataProtectionProvider dataProtectionProvider,
    IDemoModeVerifier demoModeVerifier) : Controller
{
    public const string RoleTokenPurpose = "FieldOps.DemoLogin.Role.v2";
    public static readonly TimeSpan RoleTokenLifetime = TimeSpan.FromMinutes(5);

    private readonly ITimeLimitedDataProtector _roleProtector = dataProtectionProvider
        .CreateProtector(RoleTokenPurpose)
        .ToTimeLimitedDataProtector();

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!await demoModeVerifier.IsApprovedAsync(cancellationToken))
        {
            return NotFound();
        }

        return View(new[]
        {
            CreateCard(DemoRoleNames.SystemAdministrator, "佐藤 健一", "全体設定、監査、デモデータの初期化を確認できます。"),
            CreateCard(DemoRoleNames.BranchManager, "鈴木 美咲", "担当支店の顧客、営業案件、作業予定を管理できます。"),
            CreateCard(DemoRoleNames.SalesRepresentative, "高橋 翔太", "担当支店の顧客登録と営業案件の進捗管理ができます。"),
            CreateCard(DemoRoleNames.FieldTechnician, "田中 葵", "割り当てられた作業予定の記録と完了処理ができます。")
        });
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.DemoLogin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(
        [FromForm] string roleToken,
        [FromServices] UserManager<ApplicationUser> userManager,
        [FromServices] SignInManager<ApplicationUser> signInManager)
    {
        if (!await demoModeVerifier.IsApprovedAsync(HttpContext.RequestAborted))
        {
            return NotFound();
        }

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

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout([FromServices] SignInManager<ApplicationUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return RedirectToAction(nameof(Index));
    }

    private DemoRoleCardViewModel CreateCard(string role, string displayName, string description) =>
        new(role, UiDisplayText.ForRole(role), displayName, description, _roleProtector.Protect(role, RoleTokenLifetime));
}