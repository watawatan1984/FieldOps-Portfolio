using System.Security.Claims;

using FieldOps.Infrastructure.Identity;

using Microsoft.AspNetCore.Authorization;

namespace FieldOps.Web.Authorization;

public enum BranchResourceAction
{
    ViewDashboard,
    ManageParties,
    ManageSales,
    ReadSales,
    ReadWorkOrders,
    ManageWorkOrders,
    UpdateWorkOrders,
    ViewAudit
}

internal sealed record BranchAccessResource(Guid BranchId, string? AssignedUserId);

internal sealed record BranchAccessRequirement(BranchResourceAction Action) : IAuthorizationRequirement;

internal sealed class BranchAccessHandler
    : AuthorizationHandler<BranchAccessRequirement, BranchAccessResource>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BranchAccessRequirement requirement,
        BranchAccessResource resource)
    {
        if (context.User.IsInRole(DemoRoleNames.SystemAdministrator))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (!TryGetBranchId(context.User, out Guid userBranchId) || userBranchId != resource.BranchId)
        {
            return Task.CompletedTask;
        }

        bool allowed = context.User.IsInRole(DemoRoleNames.BranchManager) ||
            context.User.IsInRole(DemoRoleNames.SalesRepresentative) && IsSalesRepresentativeAction(requirement.Action) ||
            context.User.IsInRole(DemoRoleNames.FieldTechnician) &&
            IsTechnicianAction(requirement.Action) &&
            resource.AssignedUserId == context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool TryGetBranchId(ClaimsPrincipal user, out Guid branchId) =>
        Guid.TryParse(user.FindFirstValue(DemoUserClaimsPrincipalFactory.BranchIdClaimType), out branchId);

    private static bool IsSalesRepresentativeAction(BranchResourceAction action) =>
        action is BranchResourceAction.ViewDashboard or
            BranchResourceAction.ManageParties or
            BranchResourceAction.ManageSales or
            BranchResourceAction.ReadSales or
            BranchResourceAction.ReadWorkOrders;

    private static bool IsTechnicianAction(BranchResourceAction action) =>
        action is BranchResourceAction.ViewDashboard or
            BranchResourceAction.ReadSales or
            BranchResourceAction.ReadWorkOrders or
            BranchResourceAction.UpdateWorkOrders;
}
