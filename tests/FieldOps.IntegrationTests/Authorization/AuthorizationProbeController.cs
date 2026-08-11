using FieldOps.Web.Authorization;
using FieldOps.Infrastructure.Identity;
using FieldOps.Infrastructure.Persistence;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.IntegrationTests.Authorization;

[ApiController]
[Route("authorization-probe")]
public sealed class AuthorizationProbeController : ControllerBase
{
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
        [FromServices] FieldOpsDbContext dbContext,
        [FromServices] IAuthorizationService authorizationService)
    {
        Guid loadedBranchId = await dbContext.Branches
            .Where(branch => branch.Id == branchId)
            .Select(branch => branch.Id)
            .SingleAsync();
        DemoIdentitySeeder.TryGetUserName(DemoRoleNames.FieldTechnician, out string technicianUserName);
        string? assignedUserId = await dbContext.Users
            .Where(user => user.UserName == technicianUserName && user.BranchId == loadedBranchId)
            .Select(user => user.Id)
            .SingleOrDefaultAsync();
        AuthorizationResult result = await authorizationService.AuthorizeAsync(
            User,
            new BranchAccessResource(loadedBranchId, assignedUserId),
            new BranchAccessRequirement(resourceAction));

        return result.Succeeded ? Ok() : Forbid();
    }
}
