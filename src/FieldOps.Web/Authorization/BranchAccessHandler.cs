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

internal enum BranchAccessResourceKind
{
    Branch,
    SalesOpportunity,
    WorkOrder
}

internal sealed record BranchAccessResource(
    Guid BranchId,
    string? AssignedUserId,
    string? OwnerUserId = null,
    BranchAccessResourceKind Kind = BranchAccessResourceKind.Branch);

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

        string? userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        bool allowed = context.User.IsInRole(DemoRoleNames.BranchManager) ||
            context.User.IsInRole(DemoRoleNames.SalesRepresentative) &&
            IsSalesRepresentativeAllowed(requirement.Action, resource, userId) ||
            context.User.IsInRole(DemoRoleNames.FieldTechnician) &&
            IsTechnicianAllowed(requirement.Action, resource, userId);

        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool TryGetBranchId(ClaimsPrincipal user, out Guid branchId) =>
        Guid.TryParse(user.FindFirstValue(DemoUserClaimsPrincipalFactory.BranchIdClaimType), out branchId);

    private static bool IsSalesRepresentativeAllowed(
        BranchResourceAction action,
        BranchAccessResource resource,
        string? userId) =>
        action switch
        {
            BranchResourceAction.ViewDashboard or BranchResourceAction.ManageParties or BranchResourceAction.ReadWorkOrders => true,
            BranchResourceAction.ManageSales or BranchResourceAction.ReadSales =>
                resource.Kind != BranchAccessResourceKind.SalesOpportunity || resource.OwnerUserId == userId,
            _ => false
        };

    private static bool IsTechnicianAllowed(
        BranchResourceAction action,
        BranchAccessResource resource,
        string? userId) =>
        action switch
        {
            BranchResourceAction.ReadSales when resource.Kind == BranchAccessResourceKind.Branch => true,
            BranchResourceAction.ViewDashboard or BranchResourceAction.ReadSales or BranchResourceAction.ReadWorkOrders or BranchResourceAction.UpdateWorkOrders =>
                resource.AssignedUserId == userId,
            _ => false
        };
}