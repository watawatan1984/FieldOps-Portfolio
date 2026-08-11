using System.Security.Claims;

using FieldOps.Features.Abstractions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Web.Authorization;

public enum ResourceAuthorizationOutcome
{
    Allowed,
    Forbidden,
    NotFound
}

public interface IFieldOpsResourceAuthorizer
{
    Task<ResourceAuthorizationOutcome> AuthorizeBranchAsync(
        ClaimsPrincipal user,
        Guid branchId,
        BranchResourceAction action,
        CancellationToken cancellationToken = default);

    Task<ResourceAuthorizationOutcome> AuthorizeSalesOpportunityAsync(
        ClaimsPrincipal user,
        Guid salesOpportunityId,
        BranchResourceAction action,
        CancellationToken cancellationToken = default);

    Task<ResourceAuthorizationOutcome> AuthorizePartyAsync(
        ClaimsPrincipal user,
        Guid partyId,
        Guid branchId,
        BranchResourceAction action,
        CancellationToken cancellationToken = default);

    Task<ResourceAuthorizationOutcome> AuthorizeWorkOrderAsync(
        ClaimsPrincipal user,
        Guid workOrderId,
        BranchResourceAction action,
        CancellationToken cancellationToken = default);
}

public sealed class FieldOpsResourceAuthorizer(
    IFieldOpsDbContext dbContext,
    IAuthorizationService authorizationService) : IFieldOpsResourceAuthorizer
{
    public async Task<ResourceAuthorizationOutcome> AuthorizeBranchAsync(
        ClaimsPrincipal user,
        Guid branchId,
        BranchResourceAction action,
        CancellationToken cancellationToken = default)
    {
        Guid? loadedBranchId = await dbContext.Branches
            .Where(branch => branch.Id == branchId)
            .Select(branch => (Guid?)branch.Id)
            .SingleOrDefaultAsync(cancellationToken);
        return loadedBranchId is null
            ? ResourceAuthorizationOutcome.NotFound
            : await AuthorizeAsync(
                user,
                new BranchAccessResource(loadedBranchId.Value, null),
                action,
                action is BranchResourceAction.ViewDashboard or BranchResourceAction.ManageParties or
                    BranchResourceAction.ReadSales or BranchResourceAction.ManageSales or
                    BranchResourceAction.ReadWorkOrders or BranchResourceAction.ManageWorkOrders or BranchResourceAction.ViewAudit);
    }

    public async Task<ResourceAuthorizationOutcome> AuthorizeSalesOpportunityAsync(
        ClaimsPrincipal user,
        Guid salesOpportunityId,
        BranchResourceAction action,
        CancellationToken cancellationToken = default)
    {
        BranchAccessResource? resource = await dbContext.SalesOpportunities
            .Where(opportunity => opportunity.Id == salesOpportunityId)
            .Select(opportunity => new BranchAccessResource(
                opportunity.BranchId,
                opportunity.AssignedUserId,
                opportunity.OwnerUserId,
                BranchAccessResourceKind.SalesOpportunity))
            .SingleOrDefaultAsync(cancellationToken);
        return resource is null
            ? ResourceAuthorizationOutcome.NotFound
            : await AuthorizeAsync(
                user,
                resource,
                action,
                action is BranchResourceAction.ViewDashboard or BranchResourceAction.ReadSales or
                    BranchResourceAction.ManageSales or BranchResourceAction.ManageWorkOrders or BranchResourceAction.ViewAudit);
    }

    public async Task<ResourceAuthorizationOutcome> AuthorizePartyAsync(
        ClaimsPrincipal user,
        Guid partyId,
        Guid branchId,
        BranchResourceAction action,
        CancellationToken cancellationToken = default)
    {
        bool? assignedToRequestedBranch = await dbContext.Parties
            .Where(party => party.Id == partyId)
            .Select(party => (bool?)party.BranchAssignments.Any(assignment => assignment.BranchId == branchId))
            .SingleOrDefaultAsync(cancellationToken);
        if (assignedToRequestedBranch is null)
        {
            return ResourceAuthorizationOutcome.NotFound;
        }

        if (!assignedToRequestedBranch.Value)
        {
            return ResourceAuthorizationOutcome.Forbidden;
        }

        return await AuthorizeAsync(
            user,
            new BranchAccessResource(branchId, null),
            action,
            action is BranchResourceAction.ManageParties);
    }

    public async Task<ResourceAuthorizationOutcome> AuthorizeWorkOrderAsync(
        ClaimsPrincipal user,
        Guid workOrderId,
        BranchResourceAction action,
        CancellationToken cancellationToken = default)
    {
        BranchAccessResource? resource = await dbContext.WorkOrders
            .Where(workOrder => workOrder.Id == workOrderId)
            .Select(workOrder => new BranchAccessResource(
                workOrder.BranchId,
                workOrder.AssignedUserId,
                null,
                BranchAccessResourceKind.WorkOrder))
            .SingleOrDefaultAsync(cancellationToken);
        return resource is null
            ? ResourceAuthorizationOutcome.NotFound
            : await AuthorizeAsync(
                user,
                resource,
                action,
                action is BranchResourceAction.ViewDashboard or
                    BranchResourceAction.ReadWorkOrders or
                    BranchResourceAction.ManageWorkOrders or
                    BranchResourceAction.UpdateWorkOrders);
    }

    private async Task<ResourceAuthorizationOutcome> AuthorizeAsync(
        ClaimsPrincipal user,
        BranchAccessResource resource,
        BranchResourceAction action,
        bool actionMatchesResource)
    {
        if (!actionMatchesResource)
        {
            return ResourceAuthorizationOutcome.Forbidden;
        }

        AuthorizationResult result = await authorizationService.AuthorizeAsync(
            user,
            resource,
            new BranchAccessRequirement(action));
        return result.Succeeded
            ? ResourceAuthorizationOutcome.Allowed
            : ResourceAuthorizationOutcome.Forbidden;
    }
}
