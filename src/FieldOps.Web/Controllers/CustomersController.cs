using System.Security.Claims;

using FieldOps.Domain.Enums;
using FieldOps.Features.Parties;
using FieldOps.Infrastructure.Identity;
using FieldOps.Web.Authorization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[Authorize(Policy = Policies.ManageParties)]
[Route("customers")]
public sealed class CustomersController(
    PartyQueries queries,
    IFieldOpsResourceAuthorizer resourceAuthorizer) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] PartySearchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.BranchId == Guid.Empty && !User.IsInRole(DemoRoleNames.SystemAdministrator))
        {
            Guid branchId = Guid.TryParse(
                User.FindFirstValue(DemoUserClaimsPrincipalFactory.BranchIdClaimType),
                out Guid claimedBranchId)
                    ? claimedBranchId
                    : await queries.GetDefaultBranchIdAsync(cancellationToken);
            return RedirectToAction(nameof(Index), new
            {
                branchId,
                request.Search,
                request.Page,
                request.PageSize
            });
        }

        if (request.BranchId != Guid.Empty)
        {
            ResourceAuthorizationOutcome outcome = await resourceAuthorizer.AuthorizeBranchAsync(
                User,
                request.BranchId,
                BranchResourceAction.ManageParties,
                cancellationToken);
            if (outcome is ResourceAuthorizationOutcome.NotFound)
            {
                return NotFound();
            }

            if (outcome is not ResourceAuthorizationOutcome.Allowed)
            {
                return Forbid();
            }
        }

        try
        {
            return View(await queries.SearchAsync(new PartySearchRequest
            {
                BranchId = request.BranchId,
                Search = request.Search,
                Page = request.Page,
                PageSize = request.PageSize,
                Role = PartyRoleType.Customer
            }, cancellationToken));
        }
        catch (PartyPageOutOfRangeException exception)
        {
            _ = exception;
            return BadRequest("ページ番号が正しくありません。");
        }
    }
}