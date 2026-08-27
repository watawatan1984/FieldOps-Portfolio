using FieldOps.Web.Authorization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.IntegrationTests.Authorization;

[ApiController]
[Route("authorization-probe")]
public sealed class AuthorizationProbeController : ControllerBase
{
    [HttpGet("unadorned")]
    public IActionResult Unadorned() => Ok();

    [Authorize(Policy = Policies.ViewDashboard)]
    [HttpGet("dashboard")]
    public IActionResult Dashboard() => Ok();

    [Authorize(Policy = Policies.ManageParties)]
    [HttpGet("parties")]
    public IActionResult Parties() => Ok();

    [Authorize(Policy = Policies.ReadSales)]
    [HttpGet("sales")]
    public IActionResult Sales() => Ok();

    [Authorize(Policy = Policies.ManageSales)]
    [HttpGet("sales/manage")]
    public IActionResult ManageSales() => Ok();

    [Authorize(Policy = Policies.ReadWorkOrders)]
    [HttpGet("work-orders")]
    public IActionResult WorkOrders() => Ok();

    [Authorize(Policy = Policies.ManageWorkOrders)]
    [HttpGet("work-orders/manage")]
    public IActionResult ManageWorkOrders() => Ok();

    [Authorize(Policy = Policies.UpdateWorkOrders)]
    [HttpGet("work-orders/update")]
    public IActionResult UpdateWorkOrders() => Ok();

    [Authorize(Policy = Policies.ViewAudit)]
    [HttpGet("audit")]
    public IActionResult Audit() => Ok();

    [Authorize(Policy = Policies.ResetDemo)]
    [HttpGet("reset")]
    public IActionResult Reset() => Ok();

    [Authorize]
    [HttpGet("resource/{resourceAction}/{branchId:guid}")]
    public async Task<IActionResult> Resource(
        BranchResourceAction resourceAction,
        Guid branchId,
        [FromServices] IFieldOpsResourceAuthorizer resourceAuthorizer)
    {
        ResourceAuthorizationOutcome result = await resourceAuthorizer.AuthorizeBranchAsync(User, branchId, resourceAction);
        return ToActionResult(result);
    }

    [Authorize]
    [HttpGet("sales-opportunity/{resourceAction}/{id:guid}")]
    public async Task<IActionResult> SalesOpportunityResource(
        BranchResourceAction resourceAction,
        Guid id,
        [FromServices] IFieldOpsResourceAuthorizer resourceAuthorizer) =>
        ToActionResult(await resourceAuthorizer.AuthorizeSalesOpportunityAsync(User, id, resourceAction));

    [Authorize]
    [HttpGet("work-order/{resourceAction}/{id:guid}")]
    public async Task<IActionResult> WorkOrderResource(
        BranchResourceAction resourceAction,
        Guid id,
        [FromServices] IFieldOpsResourceAuthorizer resourceAuthorizer) =>
        ToActionResult(await resourceAuthorizer.AuthorizeWorkOrderAsync(User, id, resourceAction));

    private IActionResult ToActionResult(ResourceAuthorizationOutcome outcome) => outcome switch
    {
        ResourceAuthorizationOutcome.Allowed => Ok(),
        ResourceAuthorizationOutcome.Forbidden => Forbid(),
        ResourceAuthorizationOutcome.NotFound => NotFound(),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };
}