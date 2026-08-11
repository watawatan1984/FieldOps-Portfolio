using System.Security.Claims;

using FieldOps.Features.Administration;
using FieldOps.Infrastructure.Identity;
using FieldOps.Web.Authorization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[Authorize(Policy = Policies.ViewAudit)]
[Route("audit")]
public sealed class AuditController(
    AuditQueries queries,
    IFieldOpsResourceAuthorizer resourceAuthorizer) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        Guid? branchId,
        int page = 1,
        int pageSize = AuditQueries.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        Guid? effectiveBranchId = branchId;
        if (!User.IsInRole(DemoRoleNames.SystemAdministrator))
        {
            if (!Guid.TryParse(
                User.FindFirstValue(DemoUserClaimsPrincipalFactory.BranchIdClaimType),
                out Guid claimedBranchId))
            {
                return Forbid();
            }

            effectiveBranchId ??= claimedBranchId;
            if (effectiveBranchId != claimedBranchId)
            {
                return Forbid();
            }
        }

        if (branchId.HasValue)
        {
            ResourceAuthorizationOutcome outcome = await resourceAuthorizer.AuthorizeBranchAsync(
                User,
                branchId.Value,
                BranchResourceAction.ViewAudit,
                cancellationToken);
            if (outcome == ResourceAuthorizationOutcome.NotFound)
            {
                return NotFound();
            }
            if (outcome != ResourceAuthorizationOutcome.Allowed)
            {
                return Forbid();
            }
        }

        try
        {
            return View(await queries.SearchAsync(
                effectiveBranchId,
                page,
                pageSize,
                cancellationToken));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return BadRequest(exception.Message);
        }
    }
}