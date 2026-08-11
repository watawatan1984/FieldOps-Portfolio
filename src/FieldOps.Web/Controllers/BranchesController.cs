using FieldOps.Features.Dashboard;
using FieldOps.Infrastructure.Identity;
using FieldOps.Web.Authorization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Web.Controllers;

[Authorize(Policy = Policies.ViewDashboard)]
[Route("branches")]
public sealed class BranchesController(
    BranchProgressQueries queries,
    IFieldOpsResourceAuthorizer resourceAuthorizer) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!User.IsInRole(DemoRoleNames.SystemAdministrator))
        {
            return Forbid();
        }

        return View(await queries.GetNationalAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        if (!User.IsInRole(DemoRoleNames.SystemAdministrator) &&
            !User.IsInRole(DemoRoleNames.BranchManager))
        {
            return Forbid();
        }

        ResourceAuthorizationOutcome outcome = await resourceAuthorizer.AuthorizeBranchAsync(
            User,
            id,
            BranchResourceAction.ViewDashboard,
            cancellationToken);
        if (outcome == ResourceAuthorizationOutcome.NotFound)
        {
            return NotFound();
        }
        if (outcome != ResourceAuthorizationOutcome.Allowed)
        {
            return Forbid();
        }

        BranchProgressItem? model = await queries.GetDetailsAsync(id, cancellationToken);
        return model is null ? NotFound() : View(model);
    }
}