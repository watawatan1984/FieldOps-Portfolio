using FieldOps.Domain.Enums;
using FieldOps.Features.Parties;
using FieldOps.Web.Authorization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[Authorize(Policy = Policies.ManageParties)]
[Route("business-partners")]
public sealed class BusinessPartnersController(
    PartyQueries queries,
    IFieldOpsResourceAuthorizer resourceAuthorizer) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] PartySearchRequest request,
        CancellationToken cancellationToken)
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

        return View(await queries.SearchAsync(new PartySearchRequest
        {
            BranchId = request.BranchId,
            Search = request.Search,
            Page = request.Page,
            PageSize = request.PageSize,
            Role = PartyRoleType.BusinessPartner
        }, cancellationToken));
    }
}