using System.ComponentModel.DataAnnotations;

using FieldOps.Features.Abstractions;
using FieldOps.Features.Administration;
using FieldOps.Web.Authorization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[Authorize(Policy = Policies.ResetDemo)]
[Route("administration")]
public sealed class AdministrationController : Controller
{
    [HttpGet("reset")]
    public IActionResult Reset() => View(new DemoResetViewModel
    {
        IdempotencyKey = Guid.NewGuid().ToString("N")
    });

    [HttpPost("reset")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset(
        DemoResetViewModel model,
        [FromServices] IDemoResetService resetService,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(model.Confirmation, "RESET", StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(model.Confirmation), "確認欄に RESET と入力してください。");
        }

        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View(model);
        }

        DemoResetResult result = await resetService.ResetAsync(
            new DemoResetCommand(model.IdempotencyKey, currentUser.UserId, HttpContext.TraceIdentifier),
            cancellationToken);
        model.Result = result;
        return View(model);
    }
}

public sealed class DemoResetViewModel
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required]
    public string Confirmation { get; set; } = string.Empty;

    public DemoResetResult? Result { get; set; }
}