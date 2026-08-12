using System.ComponentModel.DataAnnotations;

using FieldOps.Features.Abstractions;
using FieldOps.Features.Administration;
using FieldOps.Infrastructure.Demo;
using FieldOps.Web.Authorization;
using FieldOps.Web.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FieldOps.Web.Controllers;

[Authorize(Policy = Policies.ResetDemo)]
[Route("administration")]
public sealed class AdministrationController : Controller
{
    [HttpGet("reset")]
    public async Task<IActionResult> Reset(
        [FromServices] IDemoModeVerifier demoModeVerifier,
        [FromServices] DemoResetIntentProtector intentProtector,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (!await demoModeVerifier.IsApprovedAsync(cancellationToken) ||
            !await demoModeVerifier.IsDatabaseApprovedAsync(cancellationToken))
        {
            return Forbid();
        }

        return CreateResetView(intentProtector, currentUser.UserId);
    }

    [HttpPost("reset")]
    [EnableRateLimiting(RateLimitPolicies.DemoReset)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset(
        DemoResetViewModel model,
        [FromServices] IDemoResetService resetService,
        [FromServices] IDemoModeVerifier demoModeVerifier,
        [FromServices] DemoResetIntentProtector intentProtector,
        [FromServices] DemoResetCompletionProtector completionProtector,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (!await demoModeVerifier.IsApprovedAsync(cancellationToken) ||
            !await demoModeVerifier.IsDatabaseApprovedAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!string.Equals(model.Confirmation, "RESET", StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(model.Confirmation), "確認欄に RESET と入力してください。");
        }

        if (!intentProtector.IsValid(model.IntentToken, currentUser.UserId, model.IdempotencyKey))
        {
            ModelState.AddModelError(
                nameof(model.IntentToken),
                "初期化の確認期限が切れたか、確認内容が一致しません。確認画面を開き直してください。");
        }

        if (!ModelState.IsValid)
        {
            if (IsFetchRequest())
            {
                return BadRequest(new
                {
                    correlationId = HttpContext.TraceIdentifier,
                    errors = CreateSafeValidationErrors(),
                    retry = "確認画面を開き直してください。"
                });
            }

            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(model);
        }

        DemoResetResult result = await resetService.ResetAsync(
            new DemoResetCommand(model.IdempotencyKey, currentUser.UserId, HttpContext.TraceIdentifier),
            cancellationToken);
        if (IsFetchRequest())
        {
            string completionToken = completionProtector.Protect(currentUser.UserId, result.CorrelationId);
            return Ok(new
            {
                redirectUrl = Url.Action("Index", "Home", new { resetCompletion = completionToken }) ?? "/"
            });
        }

        model.Result = result;
        return View(model);
    }

    private bool IsFetchRequest()
    {
        return string.Equals(Request.Headers.XRequestedWith, "fetch", StringComparison.Ordinal);
    }

    private Dictionary<string, string[]> CreateSafeValidationErrors()
    {
        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);
        if (ModelState.ContainsKey(nameof(DemoResetViewModel.Confirmation)))
        {
            errors[nameof(DemoResetViewModel.Confirmation)] =
                ["確認欄には大文字で RESET と正確に入力してください。"];
        }

        if (ModelState.ContainsKey(nameof(DemoResetViewModel.IdempotencyKey)))
        {
            errors[nameof(DemoResetViewModel.IdempotencyKey)] =
                ["初期化キーが無効です。確認画面を開き直してください。"];
        }

        if (ModelState.ContainsKey(nameof(DemoResetViewModel.IntentToken)))
        {
            errors[nameof(DemoResetViewModel.IntentToken)] =
                ["初期化の確認期限が切れたか内容が一致しません。確認画面を開き直してください。"];
        }

        return errors;
    }

    private ViewResult CreateResetView(DemoResetIntentProtector intentProtector, string userId)
    {
        string idempotencyKey = Guid.NewGuid().ToString("N");
        return View(new DemoResetViewModel
        {
            IdempotencyKey = idempotencyKey,
            IntentToken = intentProtector.Protect(userId, idempotencyKey)
        });
    }
}

public sealed class DemoResetViewModel
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required]
    public string IntentToken { get; set; } = string.Empty;

    [Required]
    public string Confirmation { get; set; } = string.Empty;

    public DemoResetResult? Result { get; set; }
}