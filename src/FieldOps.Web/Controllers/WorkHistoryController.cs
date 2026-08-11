using System.Security.Claims;

using FieldOps.Features.Work;
using FieldOps.Infrastructure.Identity;
using FieldOps.Web.Authorization;
using FieldOps.Web.Models;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[Authorize(Policy = Policies.ReadWorkOrders)]
[Route("work-history")]
public sealed class WorkHistoryController(
    WorkHistorySearch search,
    IFieldOpsResourceAuthorizer resourceAuthorizer) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] WorkHistorySearchViewModel model,
        CancellationToken cancellationToken)
    {
        bool canSelectBranch = User.IsInRole(DemoRoleNames.SystemAdministrator);
        if (!model.BranchId.HasValue && !canSelectBranch)
        {
            if (!Guid.TryParse(
                User.FindFirstValue(DemoUserClaimsPrincipalFactory.BranchIdClaimType),
                out Guid branchId))
            {
                return Forbid();
            }

            return RedirectToAction(nameof(Index), new
            {
                branchId,
                model.CustomerId,
                model.BusinessPartnerId,
                model.SiteId,
                model.WorkStatus,
                model.EventType,
                model.TechnicianId,
                model.ScheduledFrom,
                model.ScheduledTo,
                model.CompletedFrom,
                model.CompletedTo,
                model.Keyword,
                model.Page,
                model.PageSize
            });
        }

        if (model.BranchId.HasValue)
        {
            ResourceAuthorizationOutcome outcome = await resourceAuthorizer.AuthorizeBranchAsync(
                User,
                model.BranchId.Value,
                BranchResourceAction.ReadWorkOrders,
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

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            WorkHistorySearchResult result = await search.SearchAsync(model.ToCriteria(), cancellationToken);
            WorkHistoryFilterOptions options = await search.GetFilterOptionsAsync(
                model.BranchId,
                canSelectBranch,
                cancellationToken);
            model.Populate(result, options, canSelectBranch);
            return View(model);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return BadRequest(exception.Message);
        }
    }
}